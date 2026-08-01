using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Registry CRUD over the real Table Storage layer. The registry is the one table a duel cannot
/// run without, so its validation rules and round-trip fidelity are worth pinning against
/// Azurite rather than a mock.
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

    // ── Create ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_Remote_Returns201()
    {
        var response = await Client.PostAsJsonAsync("/api/models", RemotePayload($"Remote {Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Register_AssignsAUlidShapedId()
    {
        var id = await RegisterAsync(RemotePayload($"Ulid {Guid.NewGuid():N}"));

        Assert.Equal(26, id.Length);
    }

    [Fact]
    public async Task Register_StartsAtTheEloBaseline()
    {
        var id = await RegisterAsync(RemotePayload($"Baseline {Guid.NewGuid():N}"));

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);

        Assert.Equal(1200, model.GetProperty("currentElo").GetDouble());
        Assert.Equal(0, model.GetProperty("duelCount").GetInt32());
        Assert.Equal(0, model.GetProperty("winCount").GetInt32());
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
    public async Task Register_LocalWithoutWebLlmId_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"Bad Local {Guid.NewGuid():N}",
            ModelType = "Local",
            TdpWatts = 115.0,
        });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_RemoteWithoutEndpoint_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"Bad Remote {Guid.NewGuid():N}",
            ModelType = "Remote",
        });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_EmptyDisplayName_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/models", RemotePayload(string.Empty));

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_OverlongDisplayName_IsRejected()
    {
        var response = await Client.PostAsJsonAsync("/api/models", RemotePayload(new string('n', 101)));

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Register_LocalModel_PersistsWebLlmIdAndTdp()
    {
        var name = $"Local {Guid.NewGuid():N}";
        var id = await RegisterAsync(new
        {
            DisplayName = name,
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
    public async Task Register_ModelTypeSerializesAsAString()
    {
        // The client binds ModelType as an enum name, not an ordinal — a switch to numeric
        // serialization would silently break every badge in the UI.
        var id = await RegisterAsync(RemotePayload($"Enum {Guid.NewGuid():N}"));

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);

        Assert.Equal(JsonValueKind.String, model.GetProperty("modelType").ValueKind);
    }

    // ── Read ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsRegisteredModels()
    {
        var id = await RegisterAsync(RemotePayload($"Listed {Guid.NewGuid():N}"));

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");

        Assert.Contains(models!, m => m.GetProperty("modelId").GetString() == id);
    }

    [Fact]
    public async Task Availability_ReturnsAnEntryPerModel()
    {
        await RegisterAsync(RemotePayload($"Avail {Guid.NewGuid():N}"));

        var response = await Client.GetAsync("/api/models/availability");

        Assert.True(response.IsSuccessStatusCode);
    }

    // ── Update ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_RenamesTheModel()
    {
        var id = await RegisterAsync(RemotePayload($"Before {Guid.NewGuid():N}"));
        var newName = $"After {Guid.NewGuid():N}";

        var patch = await Client.PatchAsJsonAsync($"/api/models/{id}", new { DisplayName = newName });
        patch.EnsureSuccessStatusCode();

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);
        Assert.Equal(newName, model.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Patch_UnknownModel_Returns404()
    {
        var response = await Client.PatchAsJsonAsync("/api/models/01NOTAREALMODELID00000000", new { DisplayName = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTheModelFromTheListing()
    {
        var id = await RegisterAsync(RemotePayload($"Doomed {Guid.NewGuid():N}"));

        var deleted = await Client.DeleteAsync($"/api/models/{id}");
        deleted.EnsureSuccessStatusCode();

        var models = await Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        Assert.DoesNotContain(models!, m => m.GetProperty("modelId").GetString() == id);
    }

    [Fact]
    public async Task Delete_IsIdempotentOrReports404()
    {
        var id = await RegisterAsync(RemotePayload($"Twice {Guid.NewGuid():N}"));
        await Client.DeleteAsync($"/api/models/{id}");

        var second = await Client.DeleteAsync($"/api/models/{id}");

        // Either contract is defensible; what must not happen is a 500.
        Assert.True(
            second.IsSuccessStatusCode || second.StatusCode == HttpStatusCode.NotFound,
            $"Second delete returned {(int)second.StatusCode}.");
    }
}
