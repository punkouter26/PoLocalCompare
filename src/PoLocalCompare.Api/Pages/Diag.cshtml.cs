using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace PoLocalCompare.Api.Pages;

public class DiagModel : PageModel
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly IConfiguration _configuration;

    public string TableStatus { get; private set; } = "Unknown";
    public long TableLatencyMs { get; private set; }
    public string? FoundryEndpoint { get; private set; }
    public string? KeyVaultUri { get; private set; }
    public IReadOnlyList<KeyValuePair<string, string>> ConfigItems { get; private set; } = [];

    public DiagModel(TableServiceClient tableServiceClient, IConfiguration configuration)
    {
        _tableServiceClient = tableServiceClient;
        _configuration = configuration;
    }

    public async Task OnGetAsync()
    {
        // Table Storage check
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _tableServiceClient.GetPropertiesAsync();
            sw.Stop();
            TableStatus = "Healthy";
            TableLatencyMs = sw.ElapsedMilliseconds;
        }
        catch
        {
            TableStatus = "Unhealthy";
        }

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

    public string MaskValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        if (value.Length <= 8) return "****";
        return value[..4] + "****" + value[^4..];
    }
}
