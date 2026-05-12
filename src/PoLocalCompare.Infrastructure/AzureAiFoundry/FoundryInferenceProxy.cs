using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Infrastructure.AzureAiFoundry;

/// <summary>
/// Calls Azure OpenAI via the native deployment endpoint with streaming SSE.
/// Uses api-key header against /openai/deployments/{name}/chat/completions.
/// </summary>
public sealed class FoundryInferenceProxy : IRemoteInferenceProxy
{
    private static readonly HttpClient _http = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<FoundryInferenceProxy> _logger;

    public FoundryInferenceProxy(IConfiguration configuration, ILogger<FoundryInferenceProxy> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        string duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var sw = Stopwatch.StartNew();

        var endpoint = _configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
        var apiKey = _configuration["AzureAiFoundry:ApiKey"];
        var deploymentName = model.ApiEndpointRef;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deploymentName))
        {
            result.IsFailure = true;
            result.FailureReason = "Azure AI Foundry configuration is incomplete (Endpoint, ApiKey, or ApiEndpointRef missing).";
            return result;
        }

        var url = $"{endpoint}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-08-01-preview";

        var requestBody = new
        {
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

        HttpResponseMessage response;
        const int maxAttempts = 3;
        int attempt = 0;
        while (true)
        {
            attempt++;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex)
            {
                result.IsFailure = true;
                result.FailureReason = $"HTTP request failed: {ex.Message}";
                _logger.LogError(ex, "HTTP request failed for model {Model}", deploymentName);
                return result;
            }

            // Retry on transient server errors (503, 502, 429) up to maxAttempts
            var isTransient = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
                                                    or System.Net.HttpStatusCode.BadGateway
                                                    or System.Net.HttpStatusCode.TooManyRequests;
            if (isTransient && attempt < maxAttempts)
            {
                response.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s
                _logger.LogWarning("Azure OpenAI transient {StatusCode} for {Model}, retry {Attempt}/{Max} in {Delay}s",
                    (int)response.StatusCode, deploymentName, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            break;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            result.IsFailure = true;
            result.FailureReason = response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? $"Deployment \"{deploymentName}\" not found (HTTP 404). Verify the deployment name in Azure AI Foundry matches ApiEndpointRef for this model, or create the deployment in your Azure AI Foundry project."
                : $"HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}";
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                _logger.LogError("Azure OpenAI deployment \"{Deployment}\" not found (HTTP 404). Verify the deployment name in your Azure AI Foundry project matches the model's ApiEndpointRef.", deploymentName);
            else
                _logger.LogError("Azure OpenAI HTTP {StatusCode} for {Model}: {Body}", (int)response.StatusCode, deploymentName, body[..Math.Min(500, body.Length)]);
            return result;
        }

        var warmUpMs = sw.ElapsedMilliseconds; // time-to-first-byte (response headers received)

        var sb = new StringBuilder();
        int tokenCount = 0;
        long? firstTokenMs = null; // time-to-first-token (actual warm-up per PRD)
        int tagCount = 0;
        int openDepth = 0;
        int styleRules = 0;
        long lastCallbackAt = -500; // trigger first callback immediately

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

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

                // Update HTML stats incrementally
                tagCount += Regex.Matches(token, @"<[a-zA-Z]").Count;
                openDepth += token.Count(c => c == '<') - token.Count(c => c == '>');
                styleRules += Regex.Matches(token, @"\{[^}]*\}").Count;

                // Throttle callback to ~500ms
                if (elapsed - lastCallbackAt >= 500)
                {
                    lastCallbackAt = elapsed;
                    var stats = new HtmlStreamStats(tagCount, Math.Max(0, openDepth), styleRules, 0.0);
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
            _logger.LogError(ex, "Stream read failed for model {Model}", deploymentName);
            return result;
        }

        sw.Stop();

        var html = sb.ToString();
        result.HtmlOutputRaw = html;
        result.HtmlOutputSizeBytes = Encoding.UTF8.GetByteCount(html);
        result.TokenCount = tokenCount;
        result.WarmUpDurationMs = firstTokenMs ?? warmUpMs; // first-token latency
        result.TotalDurationMs = sw.ElapsedMilliseconds;
        result.GenerationDurationMs = Math.Max(0L, result.TotalDurationMs - result.WarmUpDurationMs);
        result.TokenVelocity = result.GenerationDurationMs > 0
            ? Math.Round(tokenCount / (result.GenerationDurationMs / 1000.0), 1)
            : 0;
        result.CharacterDensityRatio = html.Length > 0
            ? (double)Regex.Matches(html, @"<[^>]+>").Count / html.Length
            : 0;

        // Estimate API cost (if pricing is set on the model)
        if (model.InputTokenPricePerMillion.HasValue || model.OutputTokenPricePerMillion.HasValue)
        {
            var outputCost = (tokenCount / 1_000_000.0) * (double)(model.OutputTokenPricePerMillion ?? 0);
            result.ApiCostUsd = outputCost;
        }

        _logger.LogInformation(
            "Inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            deploymentName, tokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }
}
