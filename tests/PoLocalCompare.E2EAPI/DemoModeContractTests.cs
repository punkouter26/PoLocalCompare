using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box checks on the demo plan endpoint and the auto-judge delay clamp. Kept to the
/// most behaviour-covering cases per the audit's test ratio.
/// </summary>
[Collection("E2EAPI")]
public sealed class DemoModeContractTests(ApiAppFixture app)
{
    [Fact]
    public async Task DemoPlan_Returns200WithAPlanShape()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/duels/demo-plan?rounds=3&seed=42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("rounds").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Commence_WithAnAbsurdAutoJudgeDelay_IsClamped()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Left");
        var right = await RegisterModelAsync(client, "Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML calculator.",
            AutoJudgeDelaySeconds = 99999,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The clamp is 0..3600 — 99999 must be clamped to 3600.
        Assert.Equal(3600, body.GetProperty("autoJudgeDelaySeconds").GetInt32());
    }

    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "demo-deployment",
            InputTokenPricePerMillion = 0.10m,
            OutputTokenPricePerMillion = 0.30m,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }
}
