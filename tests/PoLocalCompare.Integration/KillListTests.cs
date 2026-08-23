using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// The kill list aggregates a model's whole ELO history into one row per opponent, so the
/// arithmetic only holds against real storage. It replaced the /h2h/{a}/{b} endpoint, which
/// answered the same question for one pairing at a time and 404'd whenever either id had been
/// retired from the catalog — these tests carry over that endpoint's coverage.
/// </summary>
[Collection("Integration")]
public sealed class KillListTests(AzuriteFixture azurite) : IAsyncLifetime
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

        var body = await commence.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = body.GetProperty("duelId").GetString()!;

        var verdict = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = verdictSide });
        verdict.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement[]> GetKillListAsync(string modelId) =>
        (await (await _client.GetAsync($"/api/leaderboard/{modelId}/killlist"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

    private static JsonElement RowFor(JsonElement[] rows, string opponentId) =>
        rows.Single(r => r.GetProperty("opponentModelId").GetString() == opponentId);

    [Fact]
    public async Task KillList_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/leaderboard/aaa/killlist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KillList_AfterThreeDuels_ReportsTheCorrectScoreline()
    {
        var a = await RegisterRemoteModelAsync("KL Alpha");
        var b = await RegisterRemoteModelAsync("KL Beta");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");

        var row = RowFor(await GetKillListAsync(a), b);

        Assert.Equal(2, row.GetProperty("wins").GetInt32());
        Assert.Equal(1, row.GetProperty("losses").GetInt32());
        Assert.Equal(0, row.GetProperty("draws").GetInt32());
        Assert.Equal(3, row.GetProperty("totalDuels").GetInt32());

        // Mirror is derived from the same history; an inversion here means the projection has
        // silently swapped the two perspectives.
        var reversed = RowFor(await GetKillListAsync(b), a);
        Assert.Equal(2, reversed.GetProperty("losses").GetInt32());
        Assert.Equal(1, reversed.GetProperty("wins").GetInt32());
    }

    [Fact]
    public async Task KillList_CountsATieSeparatelyFromAWinOrALoss()
    {
        // A tie is a judged outcome with no winner. It has to land in its own column or the
        // W/L record reads it as a loss for both sides.
        var a = await RegisterRemoteModelAsync("KL Tie A");
        var b = await RegisterRemoteModelAsync("KL Tie B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Tie");

        var row = RowFor(await GetKillListAsync(a), b);

        Assert.Equal(1, row.GetProperty("wins").GetInt32());
        Assert.Equal(0, row.GetProperty("losses").GetInt32());
        Assert.Equal(1, row.GetProperty("draws").GetInt32());
    }

    [Fact]
    public async Task KillList_ReportsTheOpponentsDisplayName()
    {
        var a = await RegisterRemoteModelAsync("KL Named A");
        var b = await RegisterRemoteModelAsync("KL Named B");

        await RunDuelAsync(a, b, "Left");

        var row = RowFor(await GetKillListAsync(a), b);

        Assert.Equal("KL Named B", row.GetProperty("opponentName").GetString());
    }

    [Theory]
    [InlineData(false)]   // known model with no duels
    [InlineData(true)]    // unknown model id — same shape, no error
    public async Task KillList_NoHistory_ReturnsAnEmptyListNotA404(bool useUnknownId)
    {
        // Unlike the /h2h endpoint it replaced, this does not resolve the subject model from
        // the catalog, so a retired id degrades to "no history" instead of an error page.
        var id = useUnknownId
            ? "01NOSUCHMODELIDXXXXXXXXXXX"
            : await RegisterRemoteModelAsync("KL Lonely");

        var response = await _client.GetAsync($"/api/leaderboard/{id}/killlist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
    }

    [Fact]
    public async Task KillList_ListsOpponentsMostRecentlyFoughtFirst()
    {
        var a = await RegisterRemoteModelAsync("KL Order A");
        var older = await RegisterRemoteModelAsync("KL Order Older");
        var newer = await RegisterRemoteModelAsync("KL Order Newer");

        await RunDuelAsync(a, older, "Left");
        await RunDuelAsync(a, newer, "Left");

        var rows = await GetKillListAsync(a);

        Assert.Equal(newer, rows[0].GetProperty("opponentModelId").GetString());
    }
}
