using PoLocalCompare.Shared.Enums;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Round-trips a model registration through Azure Table Storage (Azurite) and back out
/// of GET /api/models, so DTO mapping — enum-as-string, nullable decimal pricing — is
/// verified against the real persistence layer rather than a mock.
/// </summary>
[Collection("Integration")]
public sealed class ModelRegistryTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public ModelRegistryTests(AzuriteFixture azurite)
    {
        _connectionString = azurite.ConnectionString;
    }

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(_connectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task RegisterModel_ThenList_PersistsDtoFieldsThroughTableStorage()
    {
        var displayName = $"Registry Round Trip {Guid.NewGuid():N}";

        var created = await _client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = displayName,
            ModelType = "Remote",
            ApiEndpointRef = "round-trip-deployment",
            InputTokenPricePerMillion = 0.15m,
            OutputTokenPricePerMillion = 0.60m,
        });
        created.EnsureSuccessStatusCode();

        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var modelId = createdBody.GetProperty("modelId").GetString()!;

        var models = await _client.GetFromJsonAsync<JsonElement[]>("/api/models");
        Assert.NotNull(models);

        var persisted = Assert.Single(
            models!, m => m.GetProperty("modelId").GetString() == modelId);

        Assert.Equal(displayName, persisted.GetProperty("displayName").GetString());
        Assert.Equal(nameof(ModelType.Remote), persisted.GetProperty("modelType").GetString());
        Assert.Equal("round-trip-deployment", persisted.GetProperty("apiEndpointRef").GetString());
        Assert.Equal(0.15m, persisted.GetProperty("inputTokenPricePerMillion").GetDecimal());
        Assert.Equal(0.60m, persisted.GetProperty("outputTokenPricePerMillion").GetDecimal());
        // New models start at the 1200 baseline with no duel history.
        Assert.Equal(1200, persisted.GetProperty("currentElo").GetDouble());
        Assert.Equal(0, persisted.GetProperty("duelCount").GetInt32());
    }
}
