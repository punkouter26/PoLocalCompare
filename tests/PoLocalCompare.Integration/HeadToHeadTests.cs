using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// The head-to-head endpoint aggregates across three tables — ELO history for the record, duels
/// for the timeline, duel results for the telemetry — so the arithmetic only holds against real
/// storage. The asymmetry is the interesting part: every history row is written from A's
/// perspective, so B's numbers are derived by negation and can silently invert.
/// </summary>
[Collection("Integration")]
public sealed class HeadToHeadTests(AzuriteFixture azurite) : IAsyncLifetime
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

    private async Task<string> RunDuelAsync(string leftId, string rightId, string verdictSide)
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

        var verdict = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = verdictSide });
        verdict.EnsureSuccessStatusCode();

        return duelId;
    }

    [Fact]
    public async Task GetHeadToHead_AfterThreeDuels_ReportsTheCorrectScoreline()
    {
        var a = await RegisterRemoteModelAsync("H2H Alpha");
        var b = await RegisterRemoteModelAsync("H2H Beta");

        await RunDuelAsync(a, b, "Left");   // A wins
        await RunDuelAsync(a, b, "Left");   // A wins
        await RunDuelAsync(a, b, "Right");  // B wins

        var response = await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, detail.GetProperty("totalDuels").GetInt32());
        Assert.Equal(2, detail.GetProperty("a").GetProperty("wins").GetInt32());
        Assert.Equal(1, detail.GetProperty("b").GetProperty("wins").GetInt32());
    }

    [Fact]
    public async Task GetHeadToHead_ReversingTheOrder_MirrorsTheScoreline()
    {
        var a = await RegisterRemoteModelAsync("H2H Mirror One");
        var b = await RegisterRemoteModelAsync("H2H Mirror Two");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");

        var forward = await (await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var reversed = await (await _client.GetAsync($"/api/leaderboard/h2h/{b}/{a}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            forward.GetProperty("a").GetProperty("wins").GetInt32(),
            reversed.GetProperty("b").GetProperty("wins").GetInt32());
        Assert.Equal(
            forward.GetProperty("b").GetProperty("wins").GetInt32(),
            reversed.GetProperty("a").GetProperty("wins").GetInt32());
    }

    [Fact]
    public async Task GetHeadToHead_AvgEloSwing_IsPositiveForTheWinnerAndNegativeForTheLoser()
    {
        var a = await RegisterRemoteModelAsync("H2H Swing Winner");
        var b = await RegisterRemoteModelAsync("H2H Swing Loser");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");

        var detail = await (await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(detail.GetProperty("a").GetProperty("avgEloShift").GetDouble() > 0);
        Assert.True(detail.GetProperty("b").GetProperty("avgEloShift").GetDouble() < 0);
    }

    [Fact]
    public async Task GetHeadToHead_ListsTheRecentMeetingsNewestFirst()
    {
        var a = await RegisterRemoteModelAsync("H2H Timeline A");
        var b = await RegisterRemoteModelAsync("H2H Timeline B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");
        var newest = await RunDuelAsync(a, b, "Left");

        var detail = await (await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var duels = detail.GetProperty("recentDuels").EnumerateArray().ToList();
        Assert.Equal(3, duels.Count);
        Assert.Equal(newest, duels[0].GetProperty("duelId").GetString());
    }

    [Fact]
    public async Task GetHeadToHead_ModelsThatNeverMet_ReturnsAnEmptyRecordNotAnError()
    {
        var a = await RegisterRemoteModelAsync("H2H Stranger A");
        var b = await RegisterRemoteModelAsync("H2H Stranger B");

        var response = await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, detail.GetProperty("totalDuels").GetInt32());
        Assert.False(detail.GetProperty("hasMet").GetBoolean());
        Assert.Empty(detail.GetProperty("recentDuels").EnumerateArray());
    }

    [Fact]
    public async Task GetHeadToHead_SameModelTwice_Returns404()
    {
        var a = await RegisterRemoteModelAsync("H2H Self");

        var response = await _client.GetAsync($"/api/leaderboard/h2h/{a}/{a}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetHeadToHead_UnknownModel_Returns404()
    {
        var a = await RegisterRemoteModelAsync("H2H Known");

        var response = await _client.GetAsync($"/api/leaderboard/h2h/{a}/01NOSUCHMODELIDXXXXXXXXXXX");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetHeadToHead_ReportsBothModelsDisplayNames()
    {
        var a = await RegisterRemoteModelAsync("H2H Named Alpha");
        var b = await RegisterRemoteModelAsync("H2H Named Beta");

        var detail = await (await _client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("H2H Named Alpha", detail.GetProperty("a").GetProperty("displayName").GetString());
        Assert.Equal("H2H Named Beta", detail.GetProperty("b").GetProperty("displayName").GetString());
    }
}
