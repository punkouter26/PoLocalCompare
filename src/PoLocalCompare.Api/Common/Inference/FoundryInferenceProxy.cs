using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Calls Azure OpenAI deployment endpoint with streaming SSE.
/// If deployment lookup returns 404, falls back to Azure AI Foundry model inference endpoint.
/// </summary>
public sealed class FoundryInferenceProxy(
    HttpClient http,
    IConfiguration configuration,
    ILogger<FoundryInferenceProxy> logger) : IRemoteInferenceProxy
{
    private readonly HttpClient _http = http;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<FoundryInferenceProxy> _logger = logger;

    public async Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var sw = Stopwatch.StartNew();
        using var activity = InferenceTelemetry.ActivitySource.StartActivity("gen_ai.chat", ActivityKind.Client);

        var endpoint = _configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
        var apiKey = _configuration["AzureAiFoundry:ApiKey"];
        var deploymentName = model.ApiEndpointRef;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deploymentName))
        {
            result.IsFailure = true;
            result.FailureReason = "Azure AI Foundry configuration is incomplete (Endpoint, ApiKey, or ApiEndpointRef missing).";
            return result;
        }

        var deploymentUrl = FoundryChatRequest.DeploymentUrl(endpoint, deploymentName);
        var modelInferenceUrl = FoundryChatRequest.ModelInferenceUrl(endpoint);

        var messages = new[]
        {
            new { role = "system", content = "You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS. No markdown, no explanation, no code fences." },
            new { role = "user", content = promptFull }
        };

        // Reasoning tokens are drawn from max_completion_tokens before visible output, so a
        // reasoning model still needs headroom above the classic budget — but far less of it
        // now that FoundryChatRequest pins reasoning_effort to minimal. 16,384 existed to
        // survive an unbounded default-effort reasoning pass; 8,192 leaves roughly the same
        // room for the document itself while capping the damage if a model ignores the hint.
        var maxTokens = FoundryChatRequest.IsReasoningModel(deploymentName) ? 8_192 : 4_096;

        // Reasoning models (gpt-5*, o-series) require max_completion_tokens and reject custom temperature.
        // 0.2 rather than 0.7. The task is "return valid HTML5 with inline CSS" — a fixed
        // output format with no creative latitude worth sampling for — and high temperature
        // buys only longer, more meandering documents: more tokens, more latency, more cost.
        // It also makes duels more reproducible, which matters more here than in a typical
        // app because these outputs are judged and the verdict moves a persistent rating.
        // Reasoning models ignore this (they reject a non-default temperature and
        // FoundryChatRequest omits the field for them).
        const double GenerationTemperature = 0.2;

        var deploymentRequestBody = FoundryChatRequest.Build(
            deploymentName, messages, maxTokens, GenerationTemperature, stream: true, includeModelField: false);

        var modelInferenceRequestBody = FoundryChatRequest.Build(
            deploymentName, messages, maxTokens, GenerationTemperature, stream: true, includeModelField: true);

        var deploymentJson = JsonSerializer.Serialize(deploymentRequestBody);
        var modelInferenceJson = JsonSerializer.Serialize(modelInferenceRequestBody);

        async Task<(HttpResponseMessage? Response, string? TransportError)> SendWithRetryAsync(
            string url,
            string payload,
            string target,
            CancellationToken ct)
        {
            const int maxAttempts = 3;
            int attempt = 0;

            while (true)
            {
                attempt++;
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("api-key", apiKey);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch (Exception ex)
                {
                    return (null, $"HTTP request failed: {ex.Message}");
                }

                var isTransient = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
                                                        or System.Net.HttpStatusCode.BadGateway
                                                        or System.Net.HttpStatusCode.TooManyRequests;
                if (isTransient && attempt < maxAttempts)
                {
                    var delay = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? GetRetryAfter(response.Headers.RetryAfter)
                        : TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    var statusCode = response.StatusCode;
                    response.Dispose();
                    _logger.LogWarning("Transient {StatusCode} for {Target}, retry {Attempt}/{Max} in {Delay}s",
                        (int)statusCode, target, attempt, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                return (response, null);
            }
        }

        var (deploymentResponse, deploymentTransportError) = await SendWithRetryAsync(
            deploymentUrl,
            deploymentJson,
            $"deployment '{deploymentName}'",
            cancellationToken);

        if (deploymentResponse is null)
        {
            result.IsFailure = true;
            result.FailureReason = deploymentTransportError ?? "Unknown transport error while calling Azure AI Foundry.";
            _logger.LogError("HTTP request failed for model {Model}: {Error}", deploymentName, result.FailureReason);
            return result;
        }

        var response = deploymentResponse;

        if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var deploymentNotFoundBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();

            _logger.LogWarning(
                "Deployment endpoint returned 404 for {Model}. Trying Foundry model inference endpoint /models/chat/completions.",
                deploymentName);

            var (modelInferenceResponse, modelInferenceTransportError) = await SendWithRetryAsync(
                modelInferenceUrl,
                modelInferenceJson,
                $"model '{deploymentName}'",
                cancellationToken);

            if (modelInferenceResponse is null)
            {
                result.IsFailure = true;
                result.FailureReason = modelInferenceTransportError ?? "Unknown transport error while calling model inference endpoint.";
                _logger.LogError("Fallback model inference call failed for {Model}: {Error}", deploymentName, result.FailureReason);
                return result;
            }

            response = modelInferenceResponse;
            if (!response.IsSuccessStatusCode)
            {
                var fallbackBody = await response.Content.ReadAsStringAsync(cancellationToken);
                result.IsFailure = true;
                result.FailureReason =
                    $"Model '{deploymentName}' failed on both endpoints. " +
                    $"Deployment endpoint: HTTP 404. Model inference endpoint: HTTP {(int)response.StatusCode}: {fallbackBody[..Math.Min(200, fallbackBody.Length)]}";
                _logger.LogError(
                    "Model '{Model}' failed on deployment endpoint (404) and model inference endpoint ({StatusCode}). Deployment body: {DeployBody}. Fallback body: {FallbackBody}",
                    deploymentName,
                    (int)response.StatusCode,
                    deploymentNotFoundBody[..Math.Min(300, deploymentNotFoundBody.Length)],
                    fallbackBody[..Math.Min(300, fallbackBody.Length)]);
                return result;
            }

            _logger.LogInformation("Model '{Model}' succeeded via model inference endpoint fallback.", deploymentName);
        }
        else if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            result.IsFailure = true;
            result.FailureReason = $"HTTP {(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}";
            _logger.LogError("Azure OpenAI HTTP {StatusCode} for {Model}: {Body}", (int)response.StatusCode, deploymentName, body[..Math.Min(500, body.Length)]);
            return result;
        }

        var warmUpMs = sw.ElapsedMilliseconds; // time-to-first-byte (response headers received)

        var streamError = await SseChatStreamReader.ReadIntoAsync(
            response, result, sw, warmUpMs, onTokenUpdate, cancellationToken);

        if (streamError is not null)
        {
            if (streamError is not OperationCanceledException)
                _logger.LogError(streamError, "Stream read failed for model {Model}", deploymentName);
            return result;
        }

        // Estimate API cost from provider-reported usage. TokenCount falls back to stream chunks
        // only for providers that do not send usage, so cost remains explicitly approximate there.
        if (model.InputTokenPricePerMillion.HasValue || model.OutputTokenPricePerMillion.HasValue)
        {
            var inputCost = ((result.PromptTokenCount ?? 0) / 1_000_000.0) * (double)(model.InputTokenPricePerMillion ?? 0);
            var outputCost = (result.TokenCount / 1_000_000.0) * (double)(model.OutputTokenPricePerMillion ?? 0);
            result.ApiCostUsd = inputCost + outputCost;
        }

        activity?.SetTag("gen_ai.provider.name", "azure.ai.foundry");
        activity?.SetTag("gen_ai.request.model", deploymentName);
        activity?.SetTag("gen_ai.usage.input_tokens", result.PromptTokenCount);
        activity?.SetTag("gen_ai.usage.output_tokens", result.TokenCount);
        activity?.SetTag("gen_ai.response.finish_reasons", result.FinishReason);
        activity?.SetTag("gen_ai.response.truncated", result.WasTruncated);
        InferenceTelemetry.Record("azure.ai.foundry", deploymentName, result);

        _logger.LogInformation(
            "Inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            deploymentName, result.TokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }

    private static TimeSpan GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 1, 90));
        if (retryAfter?.Date is { } retryAt)
            return TimeSpan.FromSeconds(Math.Clamp((retryAt - DateTimeOffset.UtcNow).TotalSeconds, 1, 90));
        return TimeSpan.FromSeconds(30);
    }
}
