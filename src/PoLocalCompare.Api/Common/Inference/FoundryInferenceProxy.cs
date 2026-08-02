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

        // Reasoning models (gpt-5*, o-series) require max_completion_tokens and reject custom temperature.
        var deploymentRequestBody = FoundryChatRequest.Build(
            deploymentName, messages, maxTokens: 4096, temperature: 0.7, stream: true, includeModelField: false);

        var modelInferenceRequestBody = FoundryChatRequest.Build(
            deploymentName, messages, maxTokens: 4096, temperature: 0.7, stream: true, includeModelField: true);

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
                    response.Dispose();
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning("Transient {StatusCode} for {Target}, retry {Attempt}/{Max} in {Delay}s",
                        (int)response.StatusCode, target, attempt, maxAttempts, delay.TotalSeconds);
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

        // Estimate API cost (if pricing is set on the model)
        if (model.InputTokenPricePerMillion.HasValue || model.OutputTokenPricePerMillion.HasValue)
        {
            var outputCost = (result.TokenCount / 1_000_000.0) * (double)(model.OutputTokenPricePerMillion ?? 0);
            result.ApiCostUsd = outputCost;
        }

        _logger.LogInformation(
            "Inference complete for {Model}: {Tokens} tokens, {Bytes} bytes, {Ms}ms",
            deploymentName, result.TokenCount, result.HtmlOutputSizeBytes, result.TotalDurationMs);

        return result;
    }
}
