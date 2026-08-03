using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoLocalCompare.Api.Common.Background;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// The auto-judge moves ELO without a human, so these tests pin the three invariants that keep
/// that safe: a human decision always wins the race; a judge that cannot decide (or fails for a
/// non-rate-limit reason) leaves the duel Pending rather than guessing; and a judge that hits
/// a transient upstream rate-limit is re-queued once for the requested delay, so an unattended
/// demo does not silently produce a graveyard of "judge stood down" rounds.
/// </summary>
public class AutoJudgeTests
{
    private static Model MakeModel(ModelId id) =>
        new(id, $"Model {id}", ModelType.Local, tdpWatts: 115.0, webLlmModelId: "llm-1");

    private static DuelResult MakeResult(DuelId duelId, ModelId modelId, string html) =>
        new(duelId, modelId) { HtmlOutputRaw = html, IsFailure = false };

    private static DuelResult MakeFailure(DuelId duelId, ModelId modelId, string reason) =>
        new(duelId, modelId) { HtmlOutputRaw = string.Empty, IsFailure = true, FailureReason = reason };

    private sealed record Harness(
        AutoJudge Judge,
        Mock<IDuelJudge> Llm,
        Mock<IDuelRepository> DuelRepo,
        InMemoryBackgroundQueue Queue,
        Duel Duel);

    private sealed class InMemoryBackgroundQueue : IBackgroundTaskQueue
    {
        public ConcurrentBag<Func<CancellationToken, Task>> Work { get; } = new();
        public void QueueBackgroundWork(Func<CancellationToken, Task> workItem) => Work.Add(workItem);
        public Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Wires a real <see cref="RecordVerdictHandler"/> behind the auto-judge so the ELO write
    /// path is genuinely exercised rather than mocked away. DelaySeconds is 0 so tests do not
    /// sit through the grace window.
    /// </summary>
    private static Harness BuildHarness(
        Duel duel,
        IEnumerable<DuelResult> results,
        JudgeDecision? llmDecision,
        bool enabled = true,
        int rateLimitRetryMax = 0)
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
            RateLimitRetryMax = rateLimitRetryMax,
        });

        var queue = new InMemoryBackgroundQueue();

        var autoJudge = new AutoJudge(
            duelRepo.Object,
            resultRepo.Object,
            recordVerdict,
            llm.Object,
            hub.Object,
            options,
            queue,
            NullLogger<AutoJudge>.Instance);

        return new Harness(autoJudge, llm, duelRepo, queue, duel);
    }

    private static Duel MakePendingDuel() =>
        new(DuelId.From("duel-aj"), "Build a page", "Build a page (full)", ModelId.From("left-aj"), ModelId.From("right-aj"));

    // ── The judge decides an unjudged duel ────────────────────────────────────

    [Fact]
    public async Task RunAsync_BothProducedOutput_RecordsJudgeVerdictAsAi()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "Right implemented every requested element."));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Right, duel.Verdict);
        Assert.Equal(VerdictSource.Ai, duel.VerdictSource);
        Assert.Equal(ModelId.From("right-aj"), duel.WinnerModelId);
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
        duel.WinnerModelId = ModelId.From("left-aj");

        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
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
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
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
            [MakeFailure(duel.DuelId, ModelId.From("left-aj"), "OOM"), MakeFailure(duel.DuelId, ModelId.From("right-aj"), "Watchdog timeout")],
            new JudgeDecision(DuelVerdict.Left, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A walkover does not move ratings ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_OneModelFailed_LeavesDuelPendingWithoutCallingJudge()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeFailure(duel.DuelId, ModelId.From("right-aj"), "Watchdog timeout (900s)")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Contains("One model did not produce usable output", duel.JudgeStoodDownReason);
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
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"),
            enabled: false);

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        harness.DuelRepo.Verify(r => r.GetByIdAsync(It.IsAny<DuelId>()), Times.Never);
    }

    // ── A judge hit by a rate-limit is re-queued, not silently stood down ──────

    [Fact]
    public async Task RunAsync_RateLimitWithinRetryBudget_RequeuesAndPersistsReason()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"),
             MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            llmDecision: null,
            rateLimitRetryMax: 2);

        // Make IDuelJudge throw the rate-limit exception instead of returning null — that is
        // the contract the production judge now uses.
        harness.Llm
            .Setup(j => j.JudgeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JudgeRateLimitedException(TimeSpan.FromSeconds(15), "HTTP 429 from judge endpoint"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        // The duel must stay Pending (no ELO movement) and the re-queued work item must exist.
        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Single(harness.Queue.Work);
        Assert.NotNull(duel.JudgeStoodDownReason);
        Assert.Contains("Rate-limited", duel.JudgeStoodDownReason!);
    }

    [Fact]
    public async Task RunAsync_RateLimitExhaustedRetries_LeavesPendingAndDoesNotQueue()
    {
        var duel = MakePendingDuel();
        // Pretend the same duel has already retried once (would be stored in the static counter
        // across requests; we trigger the bound by RateLimitRetryMax=1 and two consecutive calls).
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"),
             MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            llmDecision: null,
            rateLimitRetryMax: 0);

        harness.Llm
            .Setup(j => j.JudgeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JudgeRateLimitedException(TimeSpan.FromSeconds(15), "HTTP 429 from judge endpoint"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        // No retries allowed → no queued item, duel stays Pending, reason is still persisted so
        // a human arriving from the queue understands the cause.
        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Empty(harness.Queue.Work);
        Assert.NotNull(duel.JudgeStoodDownReason);
    }
}
