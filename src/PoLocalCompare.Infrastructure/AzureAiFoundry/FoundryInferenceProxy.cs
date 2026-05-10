// GoF: Proxy pattern; SOLID: Interface Segregation
using Azure.AI.Inference;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Infrastructure.AzureAiFoundry;

public sealed class FoundryInferenceProxy : IRemoteInferenceProxy
{
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
        Func<int, long, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var startTime = DateTimeOffset.UtcNow;

        var useRealAi = bool.TryParse(_configuration["Features:UseRealAi"], out var flag) && flag;

        if (!useRealAi)
        {
            // Mock response for development/testing
            await Task.Delay(2000, cancellationToken);
            var mockHtml = "<html><body><h1>Mock Response</h1><p>Features:UseRealAi is false.</p></body></html>";
            var totalMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;

            result.WarmUpDurationMs = 100;
            result.GenerationDurationMs = totalMs - 100;
            result.TotalDurationMs = totalMs;
            result.TokenCount = 50;
            result.TokenVelocity = 25.0;
            result.HtmlOutputRaw = mockHtml;
            result.HtmlOutputSizeBytes = System.Text.Encoding.UTF8.GetByteCount(mockHtml);
            result.CharacterDensityRatio = 0.8;
            result.IsFailure = false;

            return result;
        }

        try
        {
            var endpoint = _configuration["AzureAiFoundry:Endpoint"]
                ?? throw new InvalidOperationException("AzureAiFoundry:Endpoint is not configured.");
            var apiKey = _configuration["AzureAiFoundry:ApiKey"];
            var deploymentName = model.ApiEndpointRef
                ?? throw new InvalidOperationException($"Model {model.ModelId} has no ApiEndpointRef.");

            ChatCompletionsClient client;
            if (!string.IsNullOrEmpty(apiKey))
            {
                client = new ChatCompletionsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            }
            else
            {
                // Managed Identity (production)
                client = new ChatCompletionsClient(
                    new Uri(endpoint),
                    new Azure.Identity.DefaultAzureCredential());
            }

            var warmUpStart = DateTimeOffset.UtcNow;
            var htmlBuilder = new System.Text.StringBuilder();
            var firstToken = true;
            var tokenCount = 0;

            var streamingResponse = await client.CompleteStreamingAsync(
                new ChatCompletionsOptions
                {
                    Messages = { new ChatRequestUserMessage(promptFull) },
                    Model = deploymentName,
                    MaxTokens = 8000
                },
                cancellationToken);

            await foreach (var chunk in streamingResponse.WithCancellation(cancellationToken))
            {
                foreach (var choice in chunk.Choices)
                {
                    if (choice.Delta?.Content is { } content)
                    {
                        if (firstToken)
                        {
                            result.WarmUpDurationMs = (long)(DateTimeOffset.UtcNow - warmUpStart).TotalMilliseconds;
                            firstToken = false;
                        }
                        htmlBuilder.Append(content);
                        tokenCount++;
                        var elapsedMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                        await onTokenUpdate(tokenCount, elapsedMs);
                    }
                }
            }

            var completedAt = DateTimeOffset.UtcNow;
            var totalElapsedMs = (long)(completedAt - startTime).TotalMilliseconds;
            var generationMs = totalElapsedMs - result.WarmUpDurationMs;

            result.GenerationDurationMs = generationMs;
            result.TotalDurationMs = totalElapsedMs;
            result.TokenCount = tokenCount;
            result.TokenVelocity = generationMs > 0 ? tokenCount / (generationMs / 1000.0) : 0;

            var htmlOutput = htmlBuilder.ToString();
            result.HtmlOutputRaw = htmlOutput;
            result.HtmlOutputSizeBytes = System.Text.Encoding.UTF8.GetByteCount(htmlOutput);
            result.IsFailure = false;

            // Calculate API cost if pricing is configured
            if (model.InputTokenPricePerMillion.HasValue && model.OutputTokenPricePerMillion.HasValue)
            {
                // Approximate: assume 10% input tokens for the prompt
                var estimatedOutputTokens = (int)(tokenCount * 0.9);
                var estimatedInputTokens = tokenCount - estimatedOutputTokens;
                result.ApiCostUsd = (double)(
                    estimatedInputTokens * model.InputTokenPricePerMillion.Value / 1_000_000m +
                    estimatedOutputTokens * model.OutputTokenPricePerMillion.Value / 1_000_000m);
            }
        }
        catch (OperationCanceledException)
        {
            result.IsFailure = true;
            result.FailureReason = "Timeout";
            result.TotalDurationMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            _logger.LogWarning("Remote inference timed out for model {ModelId} in duel {DuelId}", model.ModelId, duelId);
        }
        catch (Exception ex)
        {
            result.IsFailure = true;
            result.FailureReason = $"ApiError: {ex.Message}";
            result.TotalDurationMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            _logger.LogError(ex, "Remote inference failed for model {ModelId} in duel {DuelId}", model.ModelId, duelId);
        }

        return result;
    }
}
