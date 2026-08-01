using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoLocalCompare.Api.Pages;

/// <summary>
/// Human-readable view of the same <see cref="HealthCheckService"/> report that <c>/health</c>
/// serves as JSON, so the two can never disagree about what is up. This is a server-rendered
/// Razor Page rather than a Blazor route on purpose: it has to work when the WASM client is the
/// thing that is broken — <c>index.html</c>'s boot-timeout fallback links here for exactly that.
/// </summary>
public class DiagModel(HealthCheckService healthChecks, IConfiguration configuration) : PageModel
{
    private readonly HealthCheckService _healthChecks = healthChecks;
    private readonly IConfiguration _configuration = configuration;

    /// <summary>One row per registered health check.</summary>
    public sealed record DependencyRow(string Name, string Status, long? LatencyMs, string? Error);

    public string OverallStatus { get; private set; } = "Unknown";
    public IReadOnlyList<DependencyRow> Dependencies { get; private set; } = [];
    public string? FoundryEndpoint { get; private set; }
    public string? KeyVaultUri { get; private set; }
    public IReadOnlyList<KeyValuePair<string, string>> ConfigItems { get; private set; } = [];

    public bool IsHealthy => string.Equals(OverallStatus, "Healthy", StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        var report = await _healthChecks.CheckHealthAsync(HttpContext.RequestAborted);

        OverallStatus = report.Status.ToString();
        Dependencies =
        [
            .. report.Entries.Select(e => new DependencyRow(
                Name: e.Key,
                // A not-configured dependency is Healthy to the pipeline but must read as
                // "NotConfigured" here — "Healthy" would imply it had actually been reached.
                Status: e.Value.Data.TryGetValue("state", out var state)
                    ? state.ToString() ?? e.Value.Status.ToString()
                    : e.Value.Status.ToString(),
                LatencyMs: e.Value.Data.TryGetValue("latencyMs", out var ms) && ms is long l ? l : null,
                Error: e.Value.Status == HealthStatus.Unhealthy ? e.Value.Exception?.Message : null))
        ];

        FoundryEndpoint = _configuration["AzureAiFoundry:Endpoint"];
        KeyVaultUri = _configuration["KeyVault:Uri"];

        ConfigItems =
        [
            new("Elo:KFactor", _configuration["Elo:KFactor"] ?? "(not set)"),
            new("Elo:StartingRating", _configuration["Elo:StartingRating"] ?? "(not set)"),
            new("GreenStats:DefaultTdpWatts", _configuration["GreenStats:DefaultTdpWatts"] ?? "(not set)"),
            new("Duel:TimeLimitSeconds", _configuration["Duel:TimeLimitSeconds"] ?? "(not set)"),
            new("Features:UseRealAi", _configuration["Features:UseRealAi"] ?? "(not set)"),
            new("KeyVault:Uri", MaskValue(KeyVaultUri)),
            new("AzureAiFoundry:Endpoint", MaskValue(FoundryEndpoint)),
        ];
    }

    /// <summary>Standards §3: /diag must never render a secret in full.</summary>
    public string MaskValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= 8) return "****";
        return value[..4] + "****" + value[^4..];
    }
}
