using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Uses GPT-5.4 Nano to judge a completed duel when the user has not picked a winner.
/// </summary>
public sealed class AutoJudgeService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<AutoJudgeService> logger)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<AutoJudgeService> _logger = logger;

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

        // A judge outage must NOT fabricate a winner — that would corrupt ELO. Leave the duel
        // Pending and signal the caller (→ HTTP 503) so the UI falls back to manual judging.
        if (verdict is null)
        {
            _logger.LogWarning("Auto-judge could not determine a verdict for duel {DuelId}; leaving it pending for manual judgment.", duelId);
            throw new AutoJudgeUnavailableException("Auto-judge is currently unavailable. Please pick a winner manually.");
        }

        _logger.LogInformation("Auto-judge for duel {DuelId} verdict: {Verdict}", duelId, verdict.Value);

        return await verdictHandler.HandleAsync(new RecordVerdictCommand(duelId, verdict.Value));
    }

    private async Task<DuelVerdict?> CallGptJudgeAsync(
        string promptText,
        string? leftHtml,
        string? rightHtml,
        CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
        var apiKey   = _configuration["AzureAiFoundry:ApiKey"];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("AzureAiFoundry config missing — auto-judge unavailable.");
            return null;
        }

        var url = $"{endpoint}/openai/deployments/gpt-5.4-nano/chat/completions?api-version=2024-08-01-preview";

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

        _logger.LogInformation("Auto-judge: Invoking Foundry GPT-5.4-nano for verdict…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            sw.Stop();
            _logger.LogInformation("Auto-judge: Foundry call completed in {ElapsedMs}ms with status {Status}", sw.ElapsedMilliseconds, response.StatusCode);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Auto-judge call failed ({Status}) — verdict unavailable.", response.StatusCode);
                return null;
            }

            var json    = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Auto-judge: Foundry returned an empty verdict — verdict unavailable.");
                return null;
            }

            return content.Trim().StartsWith("RIGHT", StringComparison.OrdinalIgnoreCase)
                ? DuelVerdict.Right
                : DuelVerdict.Left;
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Auto-judge: Foundry call timed out or was cancelled after {ElapsedMs}ms — verdict unavailable.", sw.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Auto-judge: Foundry call failed after {ElapsedMs}ms — verdict unavailable.", sw.ElapsedMilliseconds);
            return null;
        }
    }

    private static string Truncate(string? html, int maxChars)
    {
        if (string.IsNullOrEmpty(html)) return "(no output)";
        return html.Length <= maxChars ? html : html[..maxChars] + "…";
    }
}

/// <summary>Thrown when the auto-judge model cannot be reached or returns no usable verdict.
/// Signals the caller to leave the duel pending rather than fabricate a winner.</summary>
public sealed class AutoJudgeUnavailableException(string message) : Exception(message);
