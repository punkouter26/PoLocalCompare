using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box contract for the surface demo mode drives: the plan endpoint and the per-duel
/// auto-judge override on commence. The override matters because it is the only way a caller
/// can influence when ELO moves, so its bounds are part of the public contract.
/// </summary>
[Collection("E2EAPI")]
public sealed class DemoModeContractTests(ApiAppFixture app)
{
    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "demo-deployment",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    [Fact]
    public async Task DemoPlan_Anonymous_Returns401()
    {
        // The whole /api/duels group is behind the BFF gate; the plan endpoint is no exception.
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync("/api/duels/demo-plan");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DemoPlan_Returns200WithAPlanShape()
    {
        using var client = app.CreateAuthenticatedClient();
        await RegisterModelAsync(client, "Demo Plan A");
        await RegisterModelAsync(client, "Demo Plan B");

        var response = await client.GetAsync("/api/duels/demo-plan?rounds=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(plan.TryGetProperty("rounds", out _));
        Assert.True(plan.TryGetProperty("canRun", out _));
        Assert.True(plan.TryGetProperty("availableModels", out _));
    }

    [Fact]
    public async Task DemoPlan_DoesNotShadowTheDuelByIdRoute()
    {
        // /api/duels/{duelId} and /api/duels/demo-plan share a segment; the literal must win,
        // or the plan request would be parsed as a duel id and 404.
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/duels/demo-plan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Commence_WithZeroAutoJudgeDelay_ReportsZeroBackToTheClient()
    {
        // The Arena mirrors this value as its countdown, so a demo duel must report the window
        // it will actually get rather than the configured default.
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Delay Left");
        var right = await RegisterModelAsync(client, "Delay Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML spinning cube with CSS 3D transforms.",
            AutoJudgeDelaySeconds = 0,
        });

        response.EnsureSuccessStatusCode();
        var duel = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, duel.GetProperty("autoJudgeDelaySeconds").GetInt32());
    }

    [Fact]
    public async Task Commence_WithAnAbsurdAutoJudgeDelay_IsClamped()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Clamp Left");
        var right = await RegisterModelAsync(client, "Clamp Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML particle constellation animation.",
            AutoJudgeDelaySeconds = 999_999,
        });

        response.EnsureSuccessStatusCode();
        var duel = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(duel.GetProperty("autoJudgeDelaySeconds").GetInt32(), 0, 3600);
    }

    [Fact]
    public async Task Commence_WithoutAnOverride_StillSucceeds()
    {
        // The field is optional; omitting it must keep the configured grace window path working.
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Default Left");
        var right = await RegisterModelAsync(client, "Default Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML self-playing tic-tac-toe board.",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
