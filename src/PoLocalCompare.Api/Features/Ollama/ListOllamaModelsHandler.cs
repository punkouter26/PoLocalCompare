using System.Net.Http.Json;

namespace PoLocalCompare.Api.Features.Ollama;

/// <summary>Lists every model pulled into the local Ollama instance, not just the loaded ones.</summary>
public sealed class ListOllamaModelsHandler(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ListOllamaModelsHandler> logger)
{
    public async Task<IReadOnlyList<string>> HandleAsync(CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("OllamaStatus");
        var baseUrl = OllamaBaseUrl.Resolve(configuration);
        try
        {
            var tags = await http.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags", ct);
            return tags?.Models?.Select(m => m.Name).ToList() ?? [];
        }
        catch (Exception ex)
        {
            // Ollama not installed/running locally is expected — log at Debug, return empty.
            logger.LogDebug(ex, "Failed to query Ollama /api/tags at {BaseUrl}", baseUrl);
            return [];
        }
    }
}

/// <summary>One place that resolves the daemon address, instead of the same fallback in three endpoints.</summary>
internal static class OllamaBaseUrl
{
    public static string Resolve(IConfiguration configuration) =>
        (configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
}
