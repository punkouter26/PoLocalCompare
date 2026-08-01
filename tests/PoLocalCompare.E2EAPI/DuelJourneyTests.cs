using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// End-to-end API journey emulating the core product loop over HTTP:
/// register two models → commence a duel → record a verdict → confirm the leaderboard moved.
/// </summary>
[Collection("E2EAPI")]
public sealed class DuelJourneyTests(ApiAppFixture app)
{
    [Fact]
    public async Task FullDuelLoop_OverHttp_UpdatesLeaderboard()
    {
        using var client = app.CreateAuthenticatedClient();

        var left = await RegisterModelAsync(client, "Journey Left");
        var right = await RegisterModelAsync(client, "Journey Right");

        var commence = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build an accessible HTML pricing table.",
        });
        Assert.Equal(HttpStatusCode.Accepted, commence.StatusCode);
        var duelId = (await commence.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("duelId").GetString()!;

        var verdict = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        Assert.Equal(HttpStatusCode.OK, verdict.StatusCode);
        var verdictBody = await verdict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verdictBody.GetProperty("eloShiftWinner").GetDouble() > 0);

        var leaderboard = await client.GetFromJsonAsync<JsonElement[]>("/api/leaderboard");
        Assert.NotNull(leaderboard);
        Assert.Contains(leaderboard!, e => e.GetProperty("currentElo").GetDouble() > 1200);
    }

    private static async Task<string> RegisterModelAsync(HttpClient client, string displayName)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = displayName,
            ModelType = "Remote",
            ApiEndpointRef = "https://test.endpoint/v1",
            InputTokenPricePerMillion = 1.0m,
            OutputTokenPricePerMillion = 3.0m,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }
}
