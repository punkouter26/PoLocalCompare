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

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/diag/smoke")]
    public async Task ProbeEndpoints_AreAnonymous(string path)
    {
        // Deny-by-default would otherwise make the platform probe fail on every request.
        // /diag/smoke has the same expectation — the diagnostics surface has to work
        // before anyone is signed in.
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync(path);

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithAzuriteUp_Returns200AndReportsKeyVaultAsNotConfigured()
    {
        // Combined: the platform probe must return 200 with Azurite up, AND the keyVault
        // probe must say NotConfigured (not Healthy — that would falsely claim we reached
        // a vault that was never configured) when the host has empty config.
        var response = await Client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var checks = body.GetProperty("checks");

        Assert.Equal("Healthy", checks.GetProperty("azureTableStorage").GetProperty("status").GetString());
        Assert.Equal("NotConfigured", checks.GetProperty("keyVault").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DiagSmoke_ReportsTheEnvironment()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/diag/smoke");

        Assert.Equal("Development", body.GetProperty("environment").GetString());
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
