using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Endpoints;

public static class OllamaEndpoints
{
    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ollama").WithTags("Ollama");

        group.MapGet("/gpu-status", async (
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] IConfiguration config,
            [FromServices] ILogger<OllamaEndpointsMarker> logger) =>
        {
            var http = httpClientFactory.CreateClient("OllamaStatus");
            var baseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
            try
            {
                var ps = await http.GetFromJsonAsync<OllamaPsResponse>($"{baseUrl}/api/ps");
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

        // GET /api/ollama/available-models — all models pulled in Ollama (not just loaded)
        group.MapGet("/available-models", async (
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] IConfiguration config) =>
        {
            var http = httpClientFactory.CreateClient("OllamaStatus");
            var baseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
            try
            {
                var tags = await http.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags");
                var names = tags?.Models?.Select(m => m.Name).ToArray() ?? [];
                return Results.Ok(names);
            }
            catch
            {
                return Results.Ok(Array.Empty<string>());
            }
        })
        .WithName("GetOllamaAvailableModels")
        .WithSummary("Lists all models pulled in the local Ollama instance.")
        .Produces<string[]>();

        // POST /api/ollama/benchmark — timed inference run, returns stats like WebLLM benchmark
        group.MapPost("/benchmark", async (
            [FromBody] OllamaBenchmarkRequest req,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] IConfiguration config,
            [FromServices] ILogger<OllamaEndpointsMarker> logger,
            CancellationToken ct) =>
        {
            var http = httpClientFactory.CreateClient("Ollama");
            var baseUrl = (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');

            // Use /api/chat — model-agnostic chat format; avoids [INST] prompt-template conflicts
            // with newer models like Gemma4, Qwen3.5 that manage their own templates internally.
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = req.ModelName,
                messages = new[] { new { role = "user", content = req.Prompt } },
                stream = true,
                options = new { num_predict = 256 }
            });

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat");
            httpRequest.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    logger.LogError("Ollama /api/chat returned {StatusCode} for {Model}: {Body}", (int)response.StatusCode, req.ModelName, errorBody[..Math.Min(400, errorBody.Length)]);
                    return Results.Ok(new PoLocalCompare.Shared.DTOs.OllamaBenchmarkResultDto
                    {
                        IsFailure = true,
                        FailureReason = $"Ollama returned {(int)response.StatusCode}: {errorBody[..Math.Min(200, errorBody.Length)]}"
                    });
                }
            }
            catch (Exception ex)
            {
                return Results.Ok(new PoLocalCompare.Shared.DTOs.OllamaBenchmarkResultDto
                {
                    IsFailure = true,
                    FailureReason = $"Cannot reach Ollama: {ex.Message}"
                });
            }

            var output = new System.Text.StringBuilder();
            int evalCount = 0;
            long loadDurationNs = 0, evalDurationNs = 0, promptEvalDurationNs = 0;

            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new System.IO.StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var chunk = System.Text.Json.JsonSerializer.Deserialize<OllamaChatChunk>(line);
                        if (chunk is null) continue;

                        if (!string.IsNullOrEmpty(chunk.Message?.Content))
                            output.Append(chunk.Message.Content);

                        if (chunk.Done)
                        {
                            evalCount            = chunk.EvalCount;
                            loadDurationNs       = chunk.LoadDuration;
                            evalDurationNs       = chunk.EvalDuration;
                            promptEvalDurationNs = chunk.PromptEvalDuration;
                        }
                    }
                    catch { /* skip malformed lines */ }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stream read error from Ollama for model {Model}", req.ModelName);
                return Results.Ok(new PoLocalCompare.Shared.DTOs.OllamaBenchmarkResultDto
                {
                    IsFailure = true,
                    FailureReason = $"Ollama stream interrupted: {ex.Message}"
                });
            }

            double evalSecs    = evalDurationNs / 1_000_000_000.0;
            int tokensPerSec   = evalSecs > 0 ? (int)Math.Round(evalCount / evalSecs) : 0;
            int loadMs         = (int)(loadDurationNs / 1_000_000);
            int firstTokenMs   = (int)(promptEvalDurationNs / 1_000_000);

            return Results.Ok(new PoLocalCompare.Shared.DTOs.OllamaBenchmarkResultDto
            {
                LoadMs       = loadMs,
                FirstTokenMs = firstTokenMs,
                TokensPerSec = tokensPerSec,
                TotalTokens  = evalCount,
                Output       = output.ToString()
            });
        })
        .WithName("BenchmarkOllamaModel")
        .WithSummary("Runs a timed inference benchmark on an Ollama model and returns timing stats.")
        .Produces<PoLocalCompare.Shared.DTOs.OllamaBenchmarkResultDto>();

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

    private sealed record OllamaTagsResponse(
        [property: JsonPropertyName("models")] List<OllamaTagsModel>? Models);

    private sealed record OllamaTagsModel(
        [property: JsonPropertyName("name")] string Name);

    // /api/chat chunk — message.content carries each token
    private sealed record OllamaChatChunk(
        [property: JsonPropertyName("message")]             OllamaChatMessage? Message,
        [property: JsonPropertyName("done")]                bool Done,
        [property: JsonPropertyName("eval_count")]          int EvalCount,
        [property: JsonPropertyName("load_duration")]       long LoadDuration,
        [property: JsonPropertyName("eval_duration")]       long EvalDuration,
        [property: JsonPropertyName("prompt_eval_duration")] long PromptEvalDuration);

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")]    string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record OllamaBenchmarkRequest(string ModelName, string Prompt);
}