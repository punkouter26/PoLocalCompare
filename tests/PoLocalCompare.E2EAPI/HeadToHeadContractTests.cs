using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box contract for /api/leaderboard/h2h. The client renders this response directly into
/// a comparison table, so the property names and the null-versus-absent distinction are the
/// contract — a side with no telemetry must send null, not zero, or "no data" renders as "0".
/// </summary>
[Collection("E2EAPI")]
public sealed class HeadToHeadContractTests(ApiAppFixture app)
{
    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "h2h-deployment",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    [Fact]
    public async Task HeadToHead_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync("/api/leaderboard/h2h/aaa/bbb");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HeadToHead_ReturnsBothSidesWithTheExpectedShape()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Shape A");
        var b = await RegisterModelAsync(client, "Shape B");

        var response = await client.GetAsync($"/api/leaderboard/h2h/{a}/{b}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var side in new[] { "a", "b" })
        {
            var payload = detail.GetProperty(side);
            Assert.True(payload.TryGetProperty("modelId", out _));
            Assert.True(payload.TryGetProperty("displayName", out _));
            Assert.True(payload.TryGetProperty("currentElo", out _));
            Assert.True(payload.TryGetProperty("wins", out _));
        }
    }

    [Fact]
    public async Task HeadToHead_WithNoDuels_SendsNullTelemetryRatherThanZero()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Null A");
        var b = await RegisterModelAsync(client, "Null B");

        var detail = await (await client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("a").GetProperty("avgTokenVelocity").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("a").GetProperty("avgQuality").ValueKind);
    }

    [Fact]
    public async Task HeadToHead_UnknownModel_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Real");

        var response = await client.GetAsync($"/api/leaderboard/h2h/{a}/01DOESNOTEXIST0000000000AA");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HeadToHead_SameModelOnBothSides_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Same");

        var response = await client.GetAsync($"/api/leaderboard/h2h/{a}/{a}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HeadToHead_ReportsTheSampleSizeBehindItsAverages()
    {
        // The page labels the telemetry "last N meetings"; without this the numbers would read
        // as lifetime averages they are not.
        using var client = app.CreateAuthenticatedClient();
        var a = await RegisterModelAsync(client, "Sample A");
        var b = await RegisterModelAsync(client, "Sample B");

        var detail = await (await client.GetAsync($"/api/leaderboard/h2h/{a}/{b}"))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(detail.TryGetProperty("sampledDuels", out var sampled));
        Assert.Equal(0, sampled.GetInt32());
    }
}
