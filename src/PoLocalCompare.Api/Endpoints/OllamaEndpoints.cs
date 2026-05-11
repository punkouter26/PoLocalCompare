using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Endpoints;

public static class OllamaEndpoints
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ollama").WithTags("Ollama");

        group.MapGet("/gpu-status", async (
            [FromServices] IConfiguration config,
            [FromServices] ILogger<OllamaEndpointsMarker> logger) =>
        {
            var baseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
            try
            {
                var ps = await _http.GetFromJsonAsync<OllamaPsResponse>($"{baseUrl}/api/ps");
                if (ps?.Models is null)
                    return Results.Ok(Array.Empty<OllamaGpuStatusDto>());

                var result = ps.Models
                    .Select(m => new OllamaGpuStatusDto
                    {
                        ModelName = m.Name,
                        IsGpu = m.SizeVram > 0,
                        DeviceName = m.SizeVram > 0 ? $"VRAM: {m.SizeVram / 1_000_000_000:F1}GB" : $"RAM: {m.Size / 1_000_000_000:F1}GB"
                    })
                    .ToArray();

                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to query Ollama /api/ps at {BaseUrl}", baseUrl);
                return Results.Ok(Array.Empty<OllamaGpuStatusDto>());
            }
        })
        .WithName("GetOllamaGpuStatus")
        .WithSummary("Returns GPU vs CPU placement for each model currently loaded in Ollama.")
        .Produces<IEnumerable<OllamaGpuStatusDto>>();

        return app;
    }

    // Marker type for ILogger generic — avoids creating a real class just for logging
    private sealed class OllamaEndpointsMarker;

    private sealed record OllamaPsResponse(
        [property: JsonPropertyName("models")] List<OllamaPsModel>? Models);

    private sealed record OllamaPsModel(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("size")] long Size);
}