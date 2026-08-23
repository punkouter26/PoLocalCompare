using System.Net;
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
/// <c>RecordVerdictHandler</c>, ELO movement and the challenge-record partitions the challenge
/// board reads back.
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

    private async Task<JsonElement[]> GetBoardAsync(ChallengeKind kind) =>
        (await (await _client.GetAsync($"/api/challenges/leaderboard?kind={kind}"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

    // ── Auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChallengeBoard_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/challenges/leaderboard?kind=MaxSeconds");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

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

        // Scoped to this test's own models. The board is global and shared across every test
        // class in this collection, so asserting it is empty outright made this pass or fail
        // on execution order. What "left alone" means is that an unbudgeted duel banks no
        // attempt for the models that ran it.
        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);
        Assert.DoesNotContain(board, r => r.GetProperty("modelId").GetString() == a);
        Assert.DoesNotContain(board, r => r.GetProperty("modelId").GetString() == b);
    }

    // ── The challenge board ───────────────────────────────────────────────

    [Fact]
    public async Task Board_ReportsAttemptsAndPassesPerModel()
    {
        var reliable = await RegisterRemoteModelAsync("CH Board Reliable");
        var erratic = await RegisterRemoteModelAsync("CH Board Erratic");

        await RunChallengeAsync(reliable, erratic, ChallengeKind.MaxSeconds, 5, 2.0, 9.0);
        await RunChallengeAsync(reliable, erratic, ChallengeKind.MaxSeconds, 5, 3.0, 8.0);

        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);
        var row = board.Single(r => r.GetProperty("modelId").GetString() == reliable);

        Assert.Equal(2, row.GetProperty("attempts").GetInt32());
        Assert.Equal(2, row.GetProperty("met").GetInt32());
        Assert.Equal(1.0, row.GetProperty("passRate").GetDouble());
        Assert.Equal(2, row.GetProperty("wins").GetInt32());
    }

    [Fact]
    public async Task Board_RanksTheMoreReliableModelFirst()
    {
        var reliable = await RegisterRemoteModelAsync("CH Rank Reliable");
        var erratic = await RegisterRemoteModelAsync("CH Rank Erratic");

        await RunChallengeAsync(reliable, erratic, ChallengeKind.MaxSeconds, 5, 2.0, 9.0);
        await RunChallengeAsync(reliable, erratic, ChallengeKind.MaxSeconds, 5, 3.0, 8.0);

        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);

        // Relative order, not absolute position. The board is global and every test class in
        // this collection shares one Azurite, so another test's perfect-record model can
        // legitimately sit at board[0]. Asserting index 0 made this test pass or fail on
        // execution order; what it actually means to prove is that the reliable model outranks
        // the erratic one.
        var reliableRank = board.Single(r => r.GetProperty("modelId").GetString() == reliable)
            .GetProperty("rank").GetInt32();
        var erraticRank = board.Single(r => r.GetProperty("modelId").GetString() == erratic)
            .GetProperty("rank").GetInt32();

        Assert.True(reliableRank < erraticRank,
            $"expected the reliable model to outrank the erratic one, got {reliableRank} vs {erraticRank}");
    }

    /// <summary>Every kind is a ceiling, so "best" is always the smallest measurement.</summary>
    [Fact]
    public async Task Board_BestIsTheModelsLowestMeasurement()
    {
        var a = await RegisterRemoteModelAsync("CH Best A");
        var b = await RegisterRemoteModelAsync("CH Best B");

        await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 10, 4.0, 9.0);
        await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 10, 1.5, 9.0);

        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);
        var row = board.Single(r => r.GetProperty("modelId").GetString() == a);

        Assert.Equal(1.5, row.GetProperty("best").GetDouble(), precision: 3);
    }

    /// <summary>
    /// "Best" means seconds for one kind and dollars for another, so the board never mixes them.
    /// </summary>
    [Fact]
    public async Task Board_IsFilteredToOneKind()
    {
        var a = await RegisterRemoteModelAsync("CH Kind A");
        var b = await RegisterRemoteModelAsync("CH Kind B");

        await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 5, 2.0, 9.0);

        Assert.NotEmpty(await GetBoardAsync(ChallengeKind.MaxSeconds));
        Assert.Empty(await GetBoardAsync(ChallengeKind.MaxTokens));
    }

    /// <summary>
    /// A model that has never attempted this kind is absent, not a zero row: it has not failed
    /// the budget, and it has not met it either.
    /// </summary>
    [Fact]
    public async Task Board_OmitsModelsThatHaveNeverAttemptedThisKind()
    {
        var a = await RegisterRemoteModelAsync("CH Absent A");
        var b = await RegisterRemoteModelAsync("CH Absent B");
        var bystander = await RegisterRemoteModelAsync("CH Absent Bystander");

        await RunChallengeAsync(a, b, ChallengeKind.MaxSeconds, 5, 2.0, 9.0);

        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);

        Assert.DoesNotContain(board, r => r.GetProperty("modelId").GetString() == bystander);
    }

    /// <summary>
    /// The loser's attempt is banked too — the board ranks how often a model comes in under
    /// budget, which is a fact about every attempt rather than only the ones that won.
    /// </summary>
    [Fact]
    public async Task Board_BanksTheLosingSidesAttemptAsAMiss()
    {
        var winner = await RegisterRemoteModelAsync("CH Miss Winner");
        var loser = await RegisterRemoteModelAsync("CH Miss Loser");

        await RunChallengeAsync(winner, loser, ChallengeKind.MaxSeconds, 5, 2.0, 9.0);

        var board = await GetBoardAsync(ChallengeKind.MaxSeconds);
        var row = board.Single(r => r.GetProperty("modelId").GetString() == loser);

        Assert.Equal(1, row.GetProperty("attempts").GetInt32());
        Assert.Equal(0, row.GetProperty("met").GetInt32());
        Assert.Equal(0, row.GetProperty("wins").GetInt32());
    }
}
