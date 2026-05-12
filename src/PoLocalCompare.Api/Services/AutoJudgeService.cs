using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Application.Duels.RecordVerdict;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Services;

/// <summary>
/// Uses GPT-4.1 Nano to judge a completed duel when the user has not picked a winner.
/// </summary>
public sealed class AutoJudgeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AutoJudgeService> _logger;

    public AutoJudgeService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AutoJudgeService> logger)
    {
        _scopeFactory      = scopeFactory;
        _configuration     = configuration;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;
    }

    /// <summary>Returns null if duel not found. Throws InvalidOperationException if verdict already recorded
    /// or results are not yet available.</summary>
    public async Task<VerdictResponseDto?> JudgeAsync(string duelId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var duelRepo       = scope.ServiceProvider.GetRequiredService<IDuelRepository>();
        var duelResultRepo = scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();
        var verdictHandler = scope.ServiceProvider.GetRequiredService<RecordVerdictHandler>();

        var duel = await duelRepo.GetByIdAsync(duelId);
        if (duel is null) return null;

        if (duel.Verdict != DuelVerdict.Pending)
            throw new InvalidOperationException("Verdict has already been recorded for this duel.");

        var leftResult  = await duelResultRepo.GetAsync(duelId, duel.LeftModelId);
        var rightResult = await duelResultRepo.GetAsync(duelId, duel.RightModelId);

        if (leftResult is null || rightResult is null)
            throw new InvalidOperationException("Duel results are not yet available — models may still be generating.");

        var verdict = await CallGptJudgeAsync(
            duel.PromptText,
            leftResult.HtmlOutputRaw,
            rightResult.HtmlOutputRaw,
            cancellationToken);

        _logger.LogInformation("Auto-judge for duel {DuelId} verdict: {Verdict}", duelId, verdict);

        return await verdictHandler.HandleAsync(new RecordVerdictCommand(duelId, verdict));
    }

    private async Task<DuelVerdict> CallGptJudgeAsync(
        string promptText,
        string? leftHtml,
        string? rightHtml,
        CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
        var apiKey   = _configuration["AzureAiFoundry:ApiKey"];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("AzureAiFoundry config missing — auto-judge defaults to Left.");
            return DuelVerdict.Left;
        }

        var url = $"{endpoint}/openai/deployments/gpt-4.1-nano/chat/completions?api-version=2024-08-01-preview";

        // Truncate HTML to stay within token limits (~3000 chars each side)
        var leftSnippet  = Truncate(leftHtml,  3000);
        var rightSnippet = Truncate(rightHtml, 3000);

        var judgePrompt = $"""
            Prompt given to both AI models: "{promptText}"

            === LEFT OUTPUT ===
            {leftSnippet}

            === RIGHT OUTPUT ===
            {rightSnippet}

            Which output better fulfills the prompt as a complete, correct, and visually appealing HTML page?
            Reply with exactly one word: LEFT or RIGHT.
            """;

        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are an impartial judge evaluating HTML outputs from AI models. Be concise." },
                new { role = "user",   content = judgePrompt },
            },
            max_tokens  = 5,
            temperature = 0.0,
        };

        using var http    = _httpClientFactory.CreateClient("Foundry");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        _logger.LogInformation("Auto-judge: Invoking Foundry GPT-4.1-nano for verdict…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            sw.Stop();
            _logger.LogInformation("Auto-judge: Foundry call completed in {ElapsedMs}ms with status {Status}", sw.ElapsedMilliseconds, response.StatusCode);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Auto-judge call failed ({Status}) — defaulting to Left.", response.StatusCode);
                return DuelVerdict.Left;
            }

            var json    = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "LEFT";

            return content.Trim().StartsWith("RIGHT", StringComparison.OrdinalIgnoreCase)
                ? DuelVerdict.Right
                : DuelVerdict.Left;
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Auto-judge: Foundry call timed out or was cancelled after {ElapsedMs}ms — defaulting to Left.", sw.ElapsedMilliseconds);
            return DuelVerdict.Left;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Auto-judge: Foundry call failed after {ElapsedMs}ms — defaulting to Left.", sw.ElapsedMilliseconds);
            return DuelVerdict.Left;
        }
    }

    private static string Truncate(string? html, int maxChars)
    {
        if (string.IsNullOrEmpty(html)) return "(no output)";
        return html.Length <= maxChars ? html : html[..maxChars] + "…";
    }
}
