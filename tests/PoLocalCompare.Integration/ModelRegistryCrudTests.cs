using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Registry CRUD over the real Table Storage layer. The registry is the one table a duel cannot
/// run without, so its validation rules and round-trip fidelity are worth pinning against
/// Azurite rather than a mock. Kept to the most behaviour-covering cases per the audit's test
/// ratio.
/// </summary>
[Collection("Integration")]
public sealed class ModelRegistryCrudTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient Client => _host.Client;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static object RemotePayload(string name) => new
    {
        DisplayName = name,
        ModelType = "Remote",
        ApiEndpointRef = "deployment-x",
        InputTokenPricePerMillion = 0.10m,
        OutputTokenPricePerMillion = 0.40m,
    };

    private async Task<string> RegisterAsync(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/models", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }

    [Fact]
    public async Task Register_Remote_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/models", RemotePayload($"Remote {Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateDisplayName_IsRejected()
    {
        var name = $"Duplicate {Guid.NewGuid():N}";
        await RegisterAsync(RemotePayload(name));

        var second = await Client.PostAsJsonAsync("/api/models", RemotePayload(name));

        Assert.False(second.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_LocalWithoutTdp_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"Bad Local {Guid.NewGuid():N}",
            ModelType = "Local",
            WebLlmModelId = "some-llm",
        });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_LocalModel_PersistsWebLlmIdAndTdp()
    {
        var id = await RegisterAsync(new
        {
            DisplayName = $"Local {Guid.NewGuid():N}",
            ModelType = "Local",
            TdpWatts = 115.0,
            WebLlmModelId = "Qwen2.5-0.5B-Instruct-q4f32_1-MLC",
        });

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);

        Assert.Equal("Local", model.GetProperty("modelType").GetString());
        Assert.Equal("Qwen2.5-0.5B-Instruct-q4f32_1-MLC", model.GetProperty("webLlmModelId").GetString());
    }

    [Fact]
    public async Task Delete_RemovesTheModelFromTheListing()
    {
        var id = await RegisterAsync(RemotePayload($"Doomed {Guid.NewGuid():N}"));

        var deleted = await Client.DeleteAsync($"/api/models/{id}");
        deleted.EnsureSuccessStatusCode();

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        Assert.DoesNotContain(models!, m => m.GetProperty("modelId").GetString() == id);
    }
}
