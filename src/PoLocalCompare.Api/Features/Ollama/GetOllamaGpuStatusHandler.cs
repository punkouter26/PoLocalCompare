using System.Net.Http.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Ollama;

/// <summary>
/// Reports GPU vs CPU placement for each model currently loaded in the local Ollama daemon.
/// An unreachable daemon is the normal case on a machine without Ollama, so it degrades to an
/// empty list rather than an error.
/// </summary>
public sealed class GetOllamaGpuStatusHandler(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<GetOllamaGpuStatusHandler> logger)
{
    public async Task<IReadOnlyList<OllamaGpuStatusDto>> HandleAsync(CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient("OllamaStatus");
        var baseUrl = OllamaBaseUrl.Resolve(configuration);
        try
        {
            var ps = await http.GetFromJsonAsync<OllamaPsResponse>($"{baseUrl}/api/ps", ct);
            if (ps?.Models is null) return [];

            return ps.Models
                .Select(m => new OllamaGpuStatusDto
                {
                    ModelName = m.Name,
                    IsGpu = m.SizeVram > 0,
                    DeviceName = m.SizeVram > 0
                        ? $"VRAM: {m.SizeVram / 1_000_000_000:F1}GB"
                        : $"RAM: {m.Size / 1_000_000_000:F1}GB"
                })
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query Ollama /api/ps at {BaseUrl}", baseUrl);
            return [];
        }
    }
}
