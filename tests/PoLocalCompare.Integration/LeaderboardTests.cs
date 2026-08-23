using PoLocalCompare.Api.Features.Leaderboard;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Integration tests for leaderboard ranking, Green Score sort, and Kill List.
/// Seed 3 models with 5 duels each to validate aggregation logic.
/// </summary>
[Collection("Integration")]
public sealed class LeaderboardTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public LeaderboardTests(AzuriteFixture azurite)
    {
        _connectionString = azurite.ConnectionString;
    }

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(_connectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ── Helper: register a remote model ───────────────────────────────────

    private async Task<string> RegisterRemoteModelAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = name,
            ModelType = "Remote",
            ApiEndpointRef = "https://test.endpoint/v1",
            InputTokenPricePerMillion = 1.0m,
            OutputTokenPricePerMillion = 3.0m,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }

    // ── Helper: run a duel and record verdict ─────────────────────────────

    private async Task RunDuelAsync(string leftId, string rightId, string verdictSide)
    {
        var commence = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build an HTML app.",
        });
        commence.EnsureSuccessStatusCode();

        var body = await commence.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = body.GetProperty("duelId").GetString()!;

        var verdict = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict",
            new { Verdict = verdictSide });
        verdict.EnsureSuccessStatusCode();
    }

    // ── GET /api/leaderboard returns correct ELO ranking ─────────────────

    [Fact]
    public async Task GetLeaderboard_AfterMultipleDuels_RankedByEloDescending()
    {
        var modelA = await RegisterRemoteModelAsync("Model Alpha");
        var modelB = await RegisterRemoteModelAsync("Model Beta");
        var modelC = await RegisterRemoteModelAsync("Model Gamma");

        // A beats B and C (A gains ELO each time)
        await RunDuelAsync(modelA, modelB, "Left");
        await RunDuelAsync(modelA, modelC, "Left");
        // B beats C
        await RunDuelAsync(modelB, modelC, "Left");

        var response = await _client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(entries);
        Assert.True(entries.Length >= 3);

        // Extract ELOs for our models
        var eloMap = entries
            .Select(e => (
                Id: e.GetProperty("modelId").GetString()!,
                Elo: e.GetProperty("currentElo").GetDouble()))
            .Where(x => x.Id == modelA || x.Id == modelB || x.Id == modelC)
            .ToDictionary(x => x.Id, x => x.Elo);

        // A should be ranked highest, C lowest (A won 2, B won 1, C won 0)
        Assert.True(eloMap[modelA] > eloMap[modelB], "Model A (2 wins) should have higher ELO than B (1 win).");
        Assert.True(eloMap[modelB] > eloMap[modelC], "Model B (1 win) should have higher ELO than C (0 wins).");
    }

    // ── An unknown sort key falls back to ELO rather than erroring ────────

    [Fact]
    public async Task GetLeaderboard_UnknownSortKey_FallsBackToEloOrdering()
    {
        // GreenScore used to be a supported key. A stale bookmark must not 500 — the handler
        // treats anything it does not recognise as the default ELO sort.
        var remoteA = await RegisterRemoteModelAsync("Remote A sort");
        var remoteB = await RegisterRemoteModelAsync("Remote B sort");

        await RunDuelAsync(remoteA, remoteB, "Left");

        var eloResponse = await _client.GetAsync("/api/leaderboard?sortBy=Elo");
        Assert.Equal(HttpStatusCode.OK, eloResponse.StatusCode);

        var unknownResponse = await _client.GetAsync("/api/leaderboard?sortBy=GreenScore");
        Assert.Equal(HttpStatusCode.OK, unknownResponse.StatusCode);

        var eloEntries = await eloResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var unknownEntries = await unknownResponse.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(eloEntries);
        Assert.NotNull(unknownEntries);
        Assert.Equal(eloEntries.Length, unknownEntries.Length);
        Assert.Equal(
            eloEntries.Select(e => e.GetProperty("displayName").GetString()),
            unknownEntries.Select(e => e.GetProperty("displayName").GetString()));
    }

    // ── Kill-list row shape is covered by KillListTests; this file stays focused on sort ──
}
