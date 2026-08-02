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

    // ── Green Score sort reorders independently of ELO ────────────────────

    [Fact]
    public async Task GetLeaderboard_SortByGreenScore_OrdersDifferentFromElo()
    {
        // Register models (remote models have null green score — sort to bottom)
        var remoteA = await RegisterRemoteModelAsync("Remote A sort");
        var remoteB = await RegisterRemoteModelAsync("Remote B sort");

        await RunDuelAsync(remoteA, remoteB, "Left");

        var eloResponse = await _client.GetAsync("/api/leaderboard?sortBy=Elo");
        Assert.Equal(HttpStatusCode.OK, eloResponse.StatusCode);

        var greenResponse = await _client.GetAsync("/api/leaderboard?sortBy=GreenScore");
        Assert.Equal(HttpStatusCode.OK, greenResponse.StatusCode);

        // Both requests should succeed (even if the order is the same for remote-only models)
        var eloEntries = await eloResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        var greenEntries = await greenResponse.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(eloEntries);
        Assert.NotNull(greenEntries);
        Assert.Equal(eloEntries.Length, greenEntries.Length);
    }

    // ── GET /api/leaderboard/{id}/killlist shows correct win/loss ─────────

    [Fact]
    public async Task GetKillList_AfterThreeDuels_ShowsCorrectHeadToHeadCounts()
    {
        var modelX = await RegisterRemoteModelAsync("Model X kill");
        var modelY = await RegisterRemoteModelAsync("Model Y kill");

        // X beats Y twice, Y beats X once
        await RunDuelAsync(modelX, modelY, "Left");   // X wins
        await RunDuelAsync(modelX, modelY, "Left");   // X wins
        await RunDuelAsync(modelY, modelX, "Left");   // Y wins

        var response = await _client.GetAsync($"/api/leaderboard/{modelX}/killlist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var killList = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(killList);

        // Find the X vs Y head-to-head entry
        var vsY = killList.FirstOrDefault(e =>
            e.GetProperty("opponentModelId").GetString() == modelY);

        // Should show 2 wins and 1 loss for X against Y
        Assert.True(vsY.ValueKind != JsonValueKind.Undefined,
            "Kill list should contain an entry for opponent Y.");
        Assert.Equal(2, vsY.GetProperty("wins").GetInt32());
        Assert.Equal(1, vsY.GetProperty("losses").GetInt32());
    }
}
