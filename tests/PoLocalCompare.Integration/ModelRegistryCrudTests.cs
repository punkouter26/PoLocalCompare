using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Registry round-trip over the real Table Storage layer. The registry is the one table a duel
/// cannot run without, so its round-trip fidelity is worth pinning against Azurite rather than a mock.
/// </summary>
[Collection("Integration")]
public sealed class ModelRegistryCrudTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task LocalModel_RoundTripsWebLlmId()
    {
        var name = $"Local {Guid.NewGuid():N}";
        var id = await TestModels.LocalAsync(_host.Services, name);

        var models = await _host.Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);

        Assert.Equal("Local", model.GetProperty("modelType").GetString());
        Assert.Equal($"test-webllm-{name.Replace(' ', '-').ToLowerInvariant()}", model.GetProperty("webLlmModelId").GetString());
    }
}
