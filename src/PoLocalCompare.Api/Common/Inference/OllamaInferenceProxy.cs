using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Calls a local Ollama instance via its OpenAI-compatible streaming SSE endpoint.
/// POST {BaseUrl}/v1/chat/completions — no auth required.
/// ApiEndpointRef on the Model is the Ollama model name (e.g. "llama3.2").
/// </summary>
public sealed class OllamaInferenceProxy(
    HttpClient http,
    IConfiguration configuration,
    ILogger<OllamaInferenceProxy> logger) : IRemoteInferenceProxy
{
    private readonly HttpClient _http = http;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<OllamaInferenceProxy> _logger = logger;

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var sw = Stopwatch.StartNew();

        var baseUrl = (_configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var modelName = model.ApiEndpointRef;

        if (string.IsNullOrWhiteSpace(modelName))
        {
            result.IsFailure = true;
            result.FailureReason = "ApiEndpointRef (Ollama model name) is not set on this model.";
            return result;
        }

        var url = $"{baseUrl}/v1/chat/completions";

        var requestBody = new
        {
            model = modelName,
            messages = new[]
            {
                new { role = "system", content = "You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS. No markdown, no explanation, no code fences." },
                new { role = "user", content = promptFull }
            },
            stream = true,
            max_tokens = 4096,
            temperature = 0.7
        };

        var json = JsonSerializer.Serialize(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex)
        {
            result.IsFailure = true;
            result.FailureReason = $"HTTP request to Ollama failed: {ex.Message}. Is Ollama running at {baseUrl}?";
            // Connection refused is expected when Ollama is not running locally — warn instead of error.
            if (ex is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
                _logger.LogWarning("Ollama unavailable for model {Model} at {Url} — ensure Ollama is running for local model support.", modelName, url);
            else
                _logger.LogError(ex, "HTTP request to Ollama failed for model {Model} at {Url}", modelName, url);
            return result;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            result.IsFailure = true;
            result.FailureReason = $"Ollama HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}";
            _logger.LogError("Ollama HTTP {StatusCode} for {Model}: {Body}", (int)response.StatusCode, modelName, body[..Math.Min(500, body.Length)]);
            return result;
        }

        var warmUpMs = sw.ElapsedMilliseconds;

        var sb = new StringBuilder();
        int tokenCount = 0;
        long? firstTokenMs = null;
        var counters = new HtmlStreamCounters();
        long lastCallbackAt = -500;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null &&
                   !cancellationToken.IsCancellationRequested)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line[6..];
                if (data == "[DONE]") break;

                string? token = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var contentEl) &&
                            contentEl.ValueKind == JsonValueKind.String)
                        {
                            token = contentEl.GetString();
                        }
                    }
                }
                catch (JsonException)
                {
                    continue;
                }

                if (token is null) continue;

                sb.Append(token);
                tokenCount++;

                var elapsed = sw.ElapsedMilliseconds;
                if (firstTokenMs is null) firstTokenMs = elapsed;

                counters.Accumulate(token);

                if (elapsed - lastCallbackAt >= 500)
                {
                    lastCallbackAt = elapsed;
                    // ToString(0, n) copies only the prefix; ToString()[..n] materialised the
                    // whole accumulated document first, twice a second, just to slice it.
                    string? preview = tokenCount % 25 == 0
                        ? sb.ToString(0, Math.Min(5000, sb.Length))
                        : null;
                    var stats = counters.ToStats(preview);
                    await onTokenUpdate(tokenCount, elapsed, stats);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.IsFailure = true;
            result.FailureReason = "Inference cancelled (timeout or user abort).";
            return result;
        }
        catch (Exception ex)
        {
            result.IsFailure = true;
            result.FailureReason = $"Stream read error: {ex.Message}";
            if (ex is System.IO.IOException or System.Net.Sockets.SocketException)
                _logger.LogWarning(ex, "Stream read failed for Ollama model {Model} — connection may have been dropped.", modelName);
            else
                _logger.LogError(ex, "Stream read failed for Ollama model {Model}", modelName);
            return result;
        }

        sw.Stop();

        // Normalization + density/size are applied centrally by DuelResultEnricher.
        var html = sb.ToString();
        result.HtmlOutputRaw = html;
        result.HtmlOutputSizeBytes = Encoding.UTF8.GetByteCount(html);
        result.TokenCount = tokenCount;
        result.WarmUpDurationMs = firstTokenMs ?? warmUpMs;
        result.TotalDurationMs = sw.ElapsedMilliseconds;
        result.GenerationDurationMs = Math.Max(0L, result.TotalDurationMs - result.WarmUpDurationMs);
        result.TokenVelocity = result.GenerationDurationMs > 0
            ? Math.Round(tokenCount / (result.GenerationDurationMs / 1000.0), 1)
            : 0;

        _logger.LogInformation(
            "Ollama inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            modelName, tokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }
}
