using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Tests.Unit;

/// <summary>
/// The auto-judge moves ELO without a human, so these tests pin the two invariants that keep
/// that safe: a human decision always wins the race, and a judge that cannot decide leaves the
/// duel Pending rather than guessing.
/// </summary>
public class AutoJudgeTests
{
    private static Model MakeModel(string id) =>
        new(id, $"Model {id}", ModelType.Local, tdpWatts: 115.0, webLlmModelId: "llm-1");

    private static DuelResult MakeResult(string duelId, string modelId, string html) =>
        new(duelId, modelId) { HtmlOutputRaw = html, IsFailure = false };

    private static DuelResult MakeFailure(string duelId, string modelId, string reason) =>
        new(duelId, modelId) { HtmlOutputRaw = string.Empty, IsFailure = true, FailureReason = reason };

    private sealed record Harness(
        AutoJudge Judge,
        Mock<IDuelJudge> Llm,
        Mock<IDuelRepository> DuelRepo,
        Duel Duel);

    /// <summary>
    /// Wires a real <see cref="RecordVerdictHandler"/> behind the auto-judge so the ELO write
    /// path is genuinely exercised rather than mocked away. DelaySeconds is 0 so tests do not
    /// sit through the grace window.
    /// </summary>
    private static Harness BuildHarness(
        Duel duel,
        IEnumerable<DuelResult> results,
        JudgeDecision? llmDecision,
        bool enabled = true)
    {
        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(duel.DuelId)).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(duel.LeftModelId)).ReturnsAsync(MakeModel(duel.LeftModelId));
        modelRepo.Setup(r => r.GetByIdAsync(duel.RightModelId)).ReturnsAsync(MakeModel(duel.RightModelId));

        var resultRepo = new Mock<IDuelResultRepository>();
        resultRepo.Setup(r => r.GetByDuelIdAsync(duel.DuelId)).ReturnsAsync(results);

        var llm = new Mock<IDuelJudge>();
        llm.Setup(j => j.JudgeAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmDecision);

        var recordVerdict = new RecordVerdictHandler(
            duelRepo.Object, modelRepo.Object, new Mock<IEloHistoryRepository>().Object, kFactor: 32);

        var clientProxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<DuelHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var options = Options.Create(new AutoJudgeOptions
        {
            Enabled = enabled,
            DelaySeconds = 0,
            Deployment = "judge-model",
        });

        var autoJudge = new AutoJudge(
            duelRepo.Object,
            resultRepo.Object,
            recordVerdict,
            llm.Object,
            hub.Object,
            options,
            NullLogger<AutoJudge>.Instance);

        return new Harness(autoJudge, llm, duelRepo, duel);
    }

    private static Duel MakePendingDuel() =>
        new("duel-aj", "Build a page", "Build a page (full)", "left-aj", "right-aj");

    // ── The judge decides an unjudged duel ────────────────────────────────────

    [Fact]
    public async Task RunAsync_BothProducedOutput_RecordsJudgeVerdictAsAi()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, "left-aj", "<p>left</p>"), MakeResult(duel.DuelId, "right-aj", "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "Right implemented every requested element."));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Right, duel.Verdict);
        Assert.Equal(VerdictSource.Ai, duel.VerdictSource);
        Assert.Equal("right-aj", duel.WinnerModelId);
        Assert.Equal("Right implemented every requested element.", duel.JudgeRationale);
        Assert.Equal("judge-model", duel.JudgeModel);
    }

    // ── A human decision always wins the race ─────────────────────────────────

    [Fact]
    public async Task RunAsync_HumanAlreadyJudged_LeavesVerdictAloneAndNeverCallsJudge()
    {
        var duel = MakePendingDuel();
        duel.Verdict = DuelVerdict.Left;
        duel.VerdictSource = VerdictSource.Human;
        duel.WinnerModelId = "left-aj";

        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, "left-aj", "<p>left</p>"), MakeResult(duel.DuelId, "right-aj", "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Left, duel.Verdict);
        Assert.Equal(VerdictSource.Human, duel.VerdictSource);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── No decision ⇒ no ELO movement ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_JudgeCannotDecide_LeavesDuelPending()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, "left-aj", "<p>left</p>"), MakeResult(duel.DuelId, "right-aj", "<p>right</p>")],
            llmDecision: null);

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Null(duel.WinnerModelId);
    }

    [Fact]
    public async Task RunAsync_BothModelsFailed_LeavesDuelPendingAndNeverCallsJudge()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeFailure(duel.DuelId, "left-aj", "OOM"), MakeFailure(duel.DuelId, "right-aj", "Watchdog timeout")],
            new JudgeDecision(DuelVerdict.Left, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A walkover is settled without spending a judge call ───────────────────

    [Fact]
    public async Task RunAsync_OneModelFailed_AwardsSurvivorWithoutCallingJudge()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, "left-aj", "<p>left</p>"), MakeFailure(duel.DuelId, "right-aj", "Watchdog timeout (900s)")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Left, duel.Verdict);
        Assert.Equal(VerdictSource.Ai, duel.VerdictSource);
        Assert.Contains("Watchdog timeout", duel.JudgeRationale);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── The kill switch genuinely restores human-only verdicts ────────────────

    [Fact]
    public async Task RunAsync_Disabled_DoesNothing()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, "left-aj", "<p>left</p>"), MakeResult(duel.DuelId, "right-aj", "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"),
            enabled: false);

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        harness.DuelRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }
}
