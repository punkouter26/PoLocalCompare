using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Challenges;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// Challenge adjudication against real storage.
/// </summary>
/// <remarks>
/// Drives the adjudicator directly rather than through <c>POST /api/duels</c>, because the two
/// interesting cases need the sides to measure <em>differently</em> and the suite's inference
/// mock returns an identical result for every model. Writing the result rows by hand is also
/// what makes the assertions deterministic: no background queue, no timing window.
///
/// The whole path is still real — Table Storage round-trip, <c>ChallengeRules</c>,
/// <c>RecordVerdictHandler</c> and the ELO movement a forfeit produces.
/// </remarks>
[Collection("Integration")]
public sealed class ChallengeTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> RegisterRemoteModelAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = name,
            ModelType = "Remote",
            ApiEndpointRef = "https://test.endpoint/v1",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    /// <summary>
    /// Creates a challenge duel with both result rows already stored, then adjudicates it.
    /// Durations are in seconds and converted here, so the tests read in the units of the budget.
    /// </summary>
    private async Task<DuelId> RunChallengeAsync(
        string leftId,
        string rightId,
        ChallengeKind kind,
        double threshold,
        double leftSeconds,
        double rightSeconds,
        bool leftFailed = false,
        bool rightFailed = false,
        int leftTokens = 100,
        int rightTokens = 100,
        double? leftCost = null,
        double? rightCost = null)
    {
        using var scope = _host.Services.CreateScope();
        var services = scope.ServiceProvider;

        var duelRepository = services.GetRequiredService<IDuelRepository>();
        var resultRepository = services.GetRequiredService<IDuelResultRepository>();

        var duelId = DuelId.New();
        var duel = new Duel(duelId, "Build an HTML app.", "Build an HTML app.",
            ModelId.From(leftId), ModelId.From(rightId))
        {
            ChallengeKind = kind,
            ChallengeThreshold = threshold,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        await duelRepository.SaveAsync(duel);

        await resultRepository.SaveAsync(new DuelResult(duelId, ModelId.From(leftId))
        {
            HtmlOutputRaw = leftFailed ? string.Empty : "<html><body>left</body></html>",
            TotalDurationMs = (long)(leftSeconds * 1000),
            TokenCount = leftTokens,
            ApiCostUsd = leftCost,
            IsFailure = leftFailed,
            FailureReason = leftFailed ? "mock failure" : null,
        });

        await resultRepository.SaveAsync(new DuelResult(duelId, ModelId.From(rightId))
        {
            HtmlOutputRaw = rightFailed ? string.Empty : "<html><body>right</body></html>",
            TotalDurationMs = (long)(rightSeconds * 1000),
            TokenCount = rightTokens,
            ApiCostUsd = rightCost,
            IsFailure = rightFailed,
            FailureReason = rightFailed ? "mock failure" : null,
        });

        await services.GetRequiredService<ChallengeAdjudicator>()
            .TryAdjudicateAsync(duelId, CancellationToken.None);

        return duelId;
    }

    private async Task<JsonElement> GetDuelAsync(DuelId duelId) =>
        (await (await _client.GetAsync($"/api/duels/{duelId}")).Content.ReadFromJsonAsync<JsonElement>())!;

    // ── Adjudication ──────────────────────────────────────────────────────

    /// <summary>The headline rule: over the ceiling loses, however good the output was.</summary>
    [Fact]
    public async Task OneSideOverTheBudget_ForfeitsTheMatch()
    {
        var fast = await RegisterRemoteModelAsync("CH Fast");
        var slow = await RegisterRemoteModelAsync("CH Slow");

        var duelId = await RunChallengeAsync(fast, slow, ChallengeKind.MaxSeconds, 5, 4.0, 8.7);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Left", duel.GetProperty("verdict").GetString());
        Assert.Equal(fast, duel.GetProperty("winnerModelId").GetString());
    }

    /// <summary>
    /// A forfeit is not a judgement about quality — nothing read the outputs — so it carries its
    /// own source rather than being filed under the AI judge.
    /// </summary>
    [Fact]
    public async Task AForfeit_IsRecordedWithTheConstraintVerdictSource()
    {
        var fast = await RegisterRemoteModelAsync("CH Src Fast");
        var slow = await RegisterRemoteModelAsync("CH Src Slow");

        var duelId = await RunChallengeAsync(fast, slow, ChallengeKind.MaxSeconds, 5, 1.0, 30.0);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Constraint", duel.GetProperty("verdictSource").GetString());
        Assert.Contains("budget", duel.GetProperty("judgeRationale").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRightSideCanWinOnTheBudgetToo()
    {
        var slow = await RegisterRemoteModelAsync("CH Right Slow");
        var fast = await RegisterRemoteModelAsync("CH Right Fast");

        var duelId = await RunChallengeAsync(slow, fast, ChallengeKind.MaxSeconds, 5, 9.0, 2.0);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Right", duel.GetProperty("verdict").GetString());
        Assert.Equal(fast, duel.GetProperty("winnerModelId").GetString());
    }

    /// <summary>
    /// A budget both sides met separates nothing, so it must stand down and leave the duel for
    /// the ordinary judge. (The judge is disabled in this suite, so the duel stays Pending.)
    /// </summary>
    [Fact]
    public async Task BothInsideTheBudget_LeavesTheDuelToTheOrdinaryJudge()
    {
        var a = await RegisterRemoteModelAsync("CH Both A");
        var b = await RegisterRemoteModelAsync("CH Both B");

        var duelId = await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 20, 4.0, 8.7);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Pending", duel.GetProperty("verdict").GetString());
    }

    /// <summary>
    /// Neither side met the budget: a real, terminal result that moves no rating. Leaving it
    /// Pending would strand the duel with nothing a human could honestly do with it either.
    /// </summary>
    [Fact]
    public async Task NeitherInsideTheBudget_IsRecordedAsATie()
    {
        var a = await RegisterRemoteModelAsync("CH Neither A");
        var b = await RegisterRemoteModelAsync("CH Neither B");

        var duelId = await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 3, 6.0, 9.0);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Tie", duel.GetProperty("verdict").GetString());
        Assert.Equal(0, duel.GetProperty("eloShiftWinner").GetDouble());
    }
    /// <summary>An ordinary duel carries no budget and must not be touched by any of this.</summary>
    [Fact]
    public async Task ADuelWithNoBudget_IsLeftAlone()
    {
        var a = await RegisterRemoteModelAsync("CH None A");
        var b = await RegisterRemoteModelAsync("CH None B");

        var duelId = await RunChallengeAsync(a, b, ChallengeKind.None, 0, 4.0, 8.7);

        var duel = await GetDuelAsync(duelId);

        Assert.Equal("Pending", duel.GetProperty("verdict").GetString());
    }

}
