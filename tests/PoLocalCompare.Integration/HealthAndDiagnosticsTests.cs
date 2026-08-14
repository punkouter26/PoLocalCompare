using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// /health is what App Service probes to decide whether a deployment is live, so its status
/// code and payload shape are a deployment contract, not a convenience. Kept to the most
/// behaviour-covering cases per the audit's test ratio.
/// </summary>
[Collection("Integration")]
public sealed class HealthAndDiagnosticsTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient Client => _host.Client;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Health_IsAnonymous()
    {
        // Deny-by-default would otherwise make the platform probe fail on every request.
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/health");

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithAzuriteUp_Returns200()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_IncludesTableStorageCheck()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/health");

        var checks = body.GetProperty("checks");
        Assert.True(checks.TryGetProperty("azureTableStorage", out var table));
        Assert.Equal("Healthy", table.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_UnconfiguredKeyVault_ReadsAsNotConfiguredNotHealthy()
    {
        // The host sets KeyVault:Uri to empty. "Healthy" here would claim we reached a vault
        // that was never configured, which is exactly the sort of false green /diag exists
        // to avoid.
        var body = await Client.GetFromJsonAsync<JsonElement>("/health");

        var keyVault = body.GetProperty("checks").GetProperty("keyVault");
        Assert.Equal("NotConfigured", keyVault.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DiagSmoke_ReportsTheEnvironment()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/diag/smoke");

        Assert.Equal("Development", body.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task DiagSmoke_IsAnonymous()
    {
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/api/diag/smoke");

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DiagPage_RendersAndMasksSecrets()
    {
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/diag");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Diagnostics", html);
        // The masked form is "abcd****wxyz"; a rendered full URI would not contain the stars.
        Assert.DoesNotContain("BlobEndpoint=", html);
    }
}
