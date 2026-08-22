using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// The model profile assembles five separate reads — catalog, leaderboard, ELO history, results
/// and the winning-output gallery — into one row, so it only holds together against real storage.
/// It is also where the kill list now lives, having moved off the leaderboard's expanding drawer.
/// </summary>
[Collection("Integration")]
public sealed class ModelProfileTests(AzuriteFixture azurite) : IAsyncLifetime
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
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }

    private async Task RunDuelAsync(string leftId, string rightId, string verdictSide)
    {
        var commence = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build an HTML app.",
        });
        commence.EnsureSuccessStatusCode();

        var duelId = (await commence.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("duelId").GetString()!;

        var verdict = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = verdictSide });
        verdict.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> GetProfileAsync(string modelId) =>
        (await (await _client.GetAsync($"/api/leaderboard/{modelId}/profile"))
            .Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Profile_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/leaderboard/aaa/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Unlike the kill list — which degrades to "no history" for a retired id — the profile is
    /// about a model, so an id naming no model is genuinely not found rather than an empty page.
    /// </summary>
    [Fact]
    public async Task Profile_UnknownModel_Returns404()
    {
        var response = await _client.GetAsync("/api/leaderboard/01NOSUCHMODELIDXXXXXXXXXXX/profile");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Profile_ReportsTheRecordFromRealDuels()
    {
        var a = await RegisterRemoteModelAsync("MP Record A");
        var b = await RegisterRemoteModelAsync("MP Record B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");

        var profile = await GetProfileAsync(a);

        Assert.Equal("MP Record A", profile.GetProperty("displayName").GetString());
        Assert.Equal(3, profile.GetProperty("duelCount").GetInt32());
        Assert.Equal(2, profile.GetProperty("winCount").GetInt32());
        Assert.Equal(1, profile.GetProperty("lossCount").GetInt32());
    }

    /// <summary>
    /// LossCount is derived rather than stored, so it has to stay consistent with the other
    /// three counts once ties are in the mix.
    /// </summary>
    [Fact]
    public async Task Profile_DerivesLossesFromDuelsMinusWinsAndDraws()
    {
        var a = await RegisterRemoteModelAsync("MP Draw A");
        var b = await RegisterRemoteModelAsync("MP Draw B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Tie");
        await RunDuelAsync(a, b, "Right");

        var profile = await GetProfileAsync(a);

        Assert.Equal(3, profile.GetProperty("duelCount").GetInt32());
        Assert.Equal(1, profile.GetProperty("winCount").GetInt32());
        Assert.Equal(1, profile.GetProperty("drawCount").GetInt32());
        Assert.Equal(1, profile.GetProperty("lossCount").GetInt32());
    }

    /// <summary>
    /// Rank is taken from the leaderboard projection rather than re-sorted locally, so that the
    /// number here is by construction the number the row the user clicked was showing.
    /// </summary>
    [Fact]
    public async Task Profile_RankAgreesWithTheLeaderboard()
    {
        var a = await RegisterRemoteModelAsync("MP Rank A");
        var b = await RegisterRemoteModelAsync("MP Rank B");

        await RunDuelAsync(a, b, "Left");

        var board = (await (await _client.GetAsync("/api/leaderboard"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;
        var boardRank = board.Single(r => r.GetProperty("modelId").GetString() == a)
            .GetProperty("rank").GetInt32();

        var profile = await GetProfileAsync(a);

        Assert.Equal(boardRank, profile.GetProperty("rank").GetInt32());
    }

    [Fact]
    public async Task Profile_CarriesTheKillListThatUsedToLiveOnTheLeaderboard()
    {
        var a = await RegisterRemoteModelAsync("MP KL A");
        var b = await RegisterRemoteModelAsync("MP KL B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");

        var killList = (await GetProfileAsync(a)).GetProperty("killList").EnumerateArray().ToArray();
        var row = killList.Single(r => r.GetProperty("opponentModelId").GetString() == b);

        Assert.Equal(1, row.GetProperty("wins").GetInt32());
        Assert.Equal(1, row.GetProperty("losses").GetInt32());
        Assert.Equal("MP KL B", row.GetProperty("opponentName").GetString());
    }

    [Fact]
    public async Task Profile_EloHistoryIsChronologicalOldestFirst()
    {
        var a = await RegisterRemoteModelAsync("MP Elo A");
        var b = await RegisterRemoteModelAsync("MP Elo B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");

        var history = (await GetProfileAsync(a)).GetProperty("eloHistory").EnumerateArray().ToArray();

        Assert.Equal(3, history.Length);

        var timestamps = history.Select(h => h.GetProperty("at").GetDateTimeOffset()).ToArray();
        Assert.Equal(timestamps.OrderBy(t => t), timestamps);

        // Three wins in a row can only go up.
        var ratings = history.Select(h => h.GetProperty("elo").GetDouble()).ToArray();
        Assert.True(ratings[^1] > ratings[0], "Rating should rise across three wins.");
    }

    /// <summary>A rating chart with no causes on it is a squiggle; each point names its opponent.</summary>
    [Fact]
    public async Task Profile_EloHistoryPointsNameTheirOpponentAndOutcome()
    {
        var a = await RegisterRemoteModelAsync("MP Point A");
        var b = await RegisterRemoteModelAsync("MP Point B");

        await RunDuelAsync(a, b, "Left");

        var point = (await GetProfileAsync(a)).GetProperty("eloHistory").EnumerateArray().Single();

        Assert.Equal("Win", point.GetProperty("outcome").GetString());
        Assert.Equal("MP Point B", point.GetProperty("opponentName").GetString());
        Assert.Equal(b, point.GetProperty("opponentModelId").GetString());
    }

    [Fact]
    public async Task Profile_AModelWithNoDuels_IsUnrankedRatherThanMissing()
    {
        var lonely = await RegisterRemoteModelAsync("MP Lonely");

        var profile = await GetProfileAsync(lonely);

        Assert.Equal("MP Lonely", profile.GetProperty("displayName").GetString());
        Assert.Equal(0, profile.GetProperty("duelCount").GetInt32());
        Assert.Empty(profile.GetProperty("eloHistory").EnumerateArray());
        Assert.Empty(profile.GetProperty("killList").EnumerateArray());
        Assert.Empty(profile.GetProperty("winningOutputs").EnumerateArray());
    }

    /// <summary>
    /// Each gallery item carries a whole HTML document, so the cap is load-bearing rather than
    /// cosmetic — without it the response grows without bound as a model wins more duels.
    /// </summary>
    [Fact]
    public async Task Profile_GalleryIsCappedEvenAfterManyWins()
    {
        var a = await RegisterRemoteModelAsync("MP Gallery A");
        var b = await RegisterRemoteModelAsync("MP Gallery B");

        for (var i = 0; i < 8; i++)
            await RunDuelAsync(a, b, "Left");

        var gallery = (await GetProfileAsync(a)).GetProperty("winningOutputs").EnumerateArray().ToArray();

        Assert.True(gallery.Length <= 6, $"Gallery should be capped at 6, got {gallery.Length}.");
    }

    /// <summary>The loser's page must not show the winner's artifacts as its own.</summary>
    [Fact]
    public async Task Profile_GalleryHoldsOnlyDuelsThisModelWon()
    {
        var winner = await RegisterRemoteModelAsync("MP Won A");
        var loser = await RegisterRemoteModelAsync("MP Won B");

        await RunDuelAsync(winner, loser, "Left");

        Assert.Empty((await GetProfileAsync(loser)).GetProperty("winningOutputs").EnumerateArray());
    }
}
