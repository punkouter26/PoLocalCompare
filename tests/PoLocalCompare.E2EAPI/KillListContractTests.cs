using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box contract for /api/leaderboard/{id}/killlist. The Leaderboard renders this response
/// directly into the expandable per-opponent table, so the property names are the contract.
/// It replaced /api/leaderboard/h2h/{a}/{b}, whose contract tests these carry forward — most
/// importantly the cases where that endpoint answered 404 and this one answers with data.
/// </summary>
[Collection("E2EAPI")]
public sealed class KillListContractTests(ApiAppFixture app)
{
    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "killlist-deployment",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    private static async Task RunDuelAsync(HttpClient client, string leftId, string rightId, string verdict)
    {
        var commence = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build an HTML app.",
        });
        commence.EnsureSuccessStatusCode();
        var duelId = (await commence.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duelId").GetString()!;

        var recorded = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = verdict });
        recorded.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task KillList_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync("/api/leaderboard/aaa/killlist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KillList_ReturnsEachOpponentWithTheExpectedShape()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Shape A");
        var b = await RegisterModelAsync(client, "Shape B");
        await RunDuelAsync(client, a, b, "Left");

        var response = await client.GetAsync($"/api/leaderboard/{a}/killlist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
        var row = rows.Single(r => r.GetProperty("opponentModelId").GetString() == b);

        foreach (var property in new[] { "opponentModelId", "opponentName", "wins", "losses", "draws", "totalDuels", "lastDuelAt" })
            Assert.True(row.TryGetProperty(property, out _), $"missing '{property}'");
    }

    [Fact]
    public async Task KillList_UnknownModel_ReturnsEmptyRatherThan404()
    {
        // The endpoint this replaced resolved both models from the catalog and 404'd when
        // either was missing, which is what made a retired id render as a dead end.
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/leaderboard/01DOESNOTEXIST0000000000AA/killlist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
    }

    [Fact]
    public async Task KillList_WithNoDuels_ReturnsAnEmptyArray()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Lonely");

        var rows = await (await client.GetAsync($"/api/leaderboard/{a}/killlist"))
            .Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(rows);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task KillList_SendsDrawsAsTheirOwnCount()
    {
        // The client derives nothing here: a tie must arrive as `draws`, or the W/L cell shows
        // it as a loss for both models.
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Draw A");
        var b = await RegisterModelAsync(client, "Draw B");
        await RunDuelAsync(client, a, b, "Tie");

        var rows = (await (await client.GetAsync($"/api/leaderboard/{a}/killlist"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;
        var row = rows.Single(r => r.GetProperty("opponentModelId").GetString() == b);

        Assert.Equal(1, row.GetProperty("draws").GetInt32());
        Assert.Equal(0, row.GetProperty("wins").GetInt32());
        Assert.Equal(0, row.GetProperty("losses").GetInt32());
    }
}
