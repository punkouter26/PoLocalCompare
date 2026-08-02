using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Demo mode starts real duels, so what the plan endpoint returns is what gets written. The
/// pool filter is the part worth proving against a real registry: browser and Ollama models
/// must never be scheduled, because an unattended run cannot drive client-side inference.
/// </summary>
[Collection("Integration")]
public sealed class DemoPlanEndpointTests(AzuriteFixture azurite) : IAsyncLifetime
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

    private async Task<string> RegisterModelAsync(string name, string modelType)
    {
        object payload = modelType == "Local"
            ? new { DisplayName = name, ModelType = modelType, WebLlmModelId = "test-webllm-id", TdpWatts = 45.0 }
            : new { DisplayName = name, ModelType = modelType, ApiEndpointRef = "https://test.endpoint/v1" };

        var response = await _client.PostAsJsonAsync("/api/models", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }

    private async Task<JsonElement> GetPlanAsync(int rounds)
    {
        var response = await _client.GetAsync($"/api/duels/demo-plan?rounds={rounds}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task GetDemoPlan_WithTwoRemoteModels_ProducesTheRequestedRounds()
    {
        await RegisterModelAsync("Demo Remote One", "Remote");
        await RegisterModelAsync("Demo Remote Two", "Remote");

        var plan = await GetPlanAsync(10);

        Assert.True(plan.GetProperty("canRun").GetBoolean());
        Assert.Equal(10, plan.GetProperty("rounds").GetArrayLength());
    }

    [Fact]
    public async Task GetDemoPlan_NeverSchedulesAModelAgainstItself()
    {
        await RegisterModelAsync("Demo Self One", "Remote");
        await RegisterModelAsync("Demo Self Two", "Remote");

        var plan = await GetPlanAsync(10);

        foreach (var round in plan.GetProperty("rounds").EnumerateArray())
        {
            Assert.NotEqual(
                round.GetProperty("leftModelId").GetString(),
                round.GetProperty("rightModelId").GetString());
        }
    }

    [Fact]
    public async Task GetDemoPlan_ExcludesBrowserModels()
    {
        await RegisterModelAsync("Demo Remote A", "Remote");
        await RegisterModelAsync("Demo Remote B", "Remote");
        var browserModelId = await RegisterModelAsync("Demo Browser", "Local");

        var plan = await GetPlanAsync(12);

        foreach (var round in plan.GetProperty("rounds").EnumerateArray())
        {
            Assert.NotEqual(browserModelId, round.GetProperty("leftModelId").GetString());
            Assert.NotEqual(browserModelId, round.GetProperty("rightModelId").GetString());
        }
    }

    [Fact]
    public async Task GetDemoPlan_CarriesTheFullPromptTextForEachRound()
    {
        await RegisterModelAsync("Demo Prompt One", "Remote");
        await RegisterModelAsync("Demo Prompt Two", "Remote");

        var plan = await GetPlanAsync(4);

        foreach (var round in plan.GetProperty("rounds").EnumerateArray())
        {
            // The client posts this verbatim to /api/duels, which enforces a 10-character floor.
            Assert.True(round.GetProperty("promptText").GetString()!.Length >= 10);
            Assert.False(string.IsNullOrWhiteSpace(round.GetProperty("promptTitle").GetString()));
        }
    }

    [Fact]
    public async Task GetDemoPlan_ResolvesModelDisplayNamesRatherThanIds()
    {
        // The fixture is shared across the collection, so the pool contains every remote model
        // any test registered — assert the names are resolved, not which models were drawn.
        var leftId = await RegisterModelAsync("Demo Named One", "Remote");
        await RegisterModelAsync("Demo Named Two", "Remote");

        var plan = await GetPlanAsync(3);

        foreach (var round in plan.GetProperty("rounds").EnumerateArray())
        {
            var leftName = round.GetProperty("leftModelName").GetString();
            var rightName = round.GetProperty("rightModelName").GetString();

            Assert.False(string.IsNullOrWhiteSpace(leftName));
            Assert.False(string.IsNullOrWhiteSpace(rightName));
            // A name falling back to the id is the failure this guards against.
            Assert.NotEqual(round.GetProperty("leftModelId").GetString(), leftName);
            Assert.NotEqual(round.GetProperty("rightModelId").GetString(), rightName);
        }

        Assert.NotEqual(leftId, string.Empty);
    }

    [Fact]
    public async Task GetDemoPlan_ClampsAnAbsurdRoundCount()
    {
        await RegisterModelAsync("Demo Clamp One", "Remote");
        await RegisterModelAsync("Demo Clamp Two", "Remote");

        var plan = await GetPlanAsync(9999);

        Assert.True(plan.GetProperty("rounds").GetArrayLength() <= 25);
    }
}
