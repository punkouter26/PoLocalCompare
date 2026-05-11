// GoF: Proxy pattern; SOLID: Interface Segregation
using System.ClientModel;
using System.Text;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
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
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var result = new DuelResult(duelId, model.ModelId);
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            var endpoint = (_configuration["AzureAiFoundry:Endpoint"] ?? throw new InvalidOperationException("AzureAiFoundry:Endpoint is not configured.")).TrimEnd('/');
            var apiKey = _configuration["AzureAiFoundry:ApiKey"];
            var deploymentName = model.ApiEndpointRef
                ?? throw new InvalidOperationException($"Model {model.ModelId} has no ApiEndpointRef.");

            // Use the OpenAI SDK targeting Azure OpenAI's OpenAI-compatible endpoint (/openai/v1/)
            // This uses Authorization: Bearer <api-key> which the /openai/v1/ path accepts.
            // Managed Identity fallback: exchange a token from DefaultAzureCredential.
            ChatClient chatClient;
            if (string.IsNullOrEmpty(apiKey))
            {
                var credential = new DefaultAzureCredential();
                var ctx = new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]);
                var token = await credential.GetTokenAsync(ctx, cancellationToken);
                var options = new OpenAIClientOptions { Endpoint = new Uri($"{endpoint}/openai/v1/") };
                chatClient = new OpenAIClient(new ApiKeyCredential(token.Token), options).GetChatClient(deploymentName);
            }
            else
            {
                var options = new OpenAIClientOptions { Endpoint = new Uri($"{endpoint}/openai/v1/") };
                chatClient = new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(deploymentName);
            }

            // Create an MAF AIAgent backed by the OpenAI-compatible Chat Completions API
            AIAgent agent = chatClient.AsAIAgent(
                instructions: "You are an expert web developer. Output only complete, standalone HTML pages with no explanations.",
                name: $"HtmlGenerator-{deploymentName}");

            var warmUpStart = DateTimeOffset.UtcNow;
            var htmlBuilder = new StringBuilder();
            var firstToken = true;
            var tokenCount = 0;

            // HTML stats tracking
            var htmlTagCount = 0;
            var openTagDepth = 0;
            var styleRuleCount = 0;
            var inStyleBlock = false;
            var recentChunks = new Queue<string>(50);

            // Stream tokens via MAF RunStreamingAsync
            await foreach (var update in agent.RunStreamingAsync(promptFull, cancellationToken: cancellationToken))
            {
                var content = update.Text;
                if (string.IsNullOrEmpty(content)) continue;

                if (firstToken)
                {
                    result.WarmUpDurationMs = (long)(DateTimeOffset.UtcNow - warmUpStart).TotalMilliseconds;
                    firstToken = false;
                }

                htmlBuilder.Append(content);
                tokenCount++;

                UpdateHtmlStats(content, ref htmlTagCount, ref openTagDepth, ref styleRuleCount, ref inStyleBlock);

                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (recentChunks.Count >= 50) recentChunks.Dequeue();
                    recentChunks.Enqueue(content);
                }
                var repScore = ComputeRepetitionScore(recentChunks);

                // Include partial HTML preview every 25 tokens to show live progress
                var preview = tokenCount % 25 == 0 ? htmlBuilder.ToString() : null;
                var htmlStats = new HtmlStreamStats(htmlTagCount, Math.Max(0, openTagDepth),
                    styleRuleCount, repScore, preview);

                var elapsedMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                await onTokenUpdate(tokenCount, elapsedMs, htmlStats);
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

    /// <summary>Incrementally updates HTML structure stats from a new content chunk.</summary>
    private static void UpdateHtmlStats(string chunk,
        ref int tagCount, ref int depth, ref int styleRules, ref bool inStyle)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            if (chunk[i] == '<')
            {
                // Peek ahead for closing tag or style tag
                var rest = chunk.AsSpan(i + 1);
                if (rest.StartsWith("/", StringComparison.Ordinal))
                {
                    depth--;
                }
                else if (!rest.StartsWith("!", StringComparison.Ordinal)) // skip comments/doctype
                {
                    tagCount++;
                    depth++;
                    // Detect <style
                    if (rest.StartsWith("style", StringComparison.OrdinalIgnoreCase))
                        inStyle = true;
                }
                // Detect </style
                if (rest.StartsWith("/style", StringComparison.OrdinalIgnoreCase))
                    inStyle = false;
            }
            else if (inStyle && chunk[i] == '{')
            {
                styleRules++;
            }
        }
    }

    /// <summary>
    /// Computes a 0–1 repetition score on the rolling chunk window.
    /// Joins all chunks, splits into 5-grams of words, checks for duplicates.
    /// </summary>
    private static double ComputeRepetitionScore(Queue<string> recentChunks)
    {
        if (recentChunks.Count < 10) return 0;
        var text = string.Concat(recentChunks);
        var words = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 10) return 0;

        var ngrams = new HashSet<string>();
        var duplicates = 0;
        const int n = 5;
        for (var i = 0; i <= words.Length - n; i++)
        {
            var gram = string.Join(' ', words, i, n);
            if (!ngrams.Add(gram)) duplicates++;
        }
        var total = words.Length - n + 1;
        return total > 0 ? Math.Round((double)duplicates / total, 2) : 0;
    }
}
