using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoLocalCompare.Integration;

/// <summary>
/// /health is what App Service probes to decide whether a deployment is live, so its status
/// code and payload shape are a deployment contract, not a convenience.
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

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithAzuriteUp_Returns200()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsOverallStatus()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/health");

        Assert.Equal("Healthy", body.GetProperty("status").GetString());
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
    public async Task Health_ReportsTableStorageLatency()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/health");

        var table = body.GetProperty("checks").GetProperty("azureTableStorage");
        Assert.True(table.TryGetProperty("latencyMs", out var latency));
        Assert.True(latency.GetInt64() >= 0);
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
    public async Task Health_ContentTypeIsJson()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task HealthCheckService_IsRegistered()
    {
        // Guards the standards §3 requirement directly: /health must be backed by the real
        // health-check pipeline, not a hand-rolled endpoint that merely looks like one.
        var service = _host.Services.GetService<HealthCheckService>();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task HealthCheckService_ExposesEveryDependencyProbe()
    {
        var service = _host.Services.GetRequiredService<HealthCheckService>();

        var report = await service.CheckHealthAsync();

        Assert.Contains("azureTableStorage", report.Entries.Keys);
        Assert.Contains("azureAiFoundry", report.Entries.Keys);
        Assert.Contains("keyVault", report.Entries.Keys);
    }

    [Fact]
    public async Task DiagSmoke_IsAnonymous()
    {
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/api/diag/smoke");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DiagSmoke_ReportsTheEnvironment()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/diag/smoke");

        Assert.Equal("Development", body.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task DiagSmoke_ReportsModelCounts()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/diag/smoke");

        Assert.True(body.GetProperty("models").GetProperty("total").GetInt32() >= 0);
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

    [Fact]
    public async Task DiagPage_ListsTheSameDependenciesAsHealth()
    {
        var health = await Client.GetFromJsonAsync<JsonElement>("/health");
        using var anonymous = _host.CreateAnonymousClient();
        var html = await (await anonymous.GetAsync("/diag")).Content.ReadAsStringAsync();

        foreach (var check in health.GetProperty("checks").EnumerateObject())
        {
            Assert.Contains(check.Name, html);
        }
    }
}
