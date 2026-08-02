// GoF: Strategy — the judging rule is swappable behind IDuelJudge
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoLocalCompare.Api.Common.Inference;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

internal static partial class JudgeLog
{
    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning, Message = "Judge call failed: {Reason}")]
    public static partial void CallFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Judge reply could not be parsed: {Reply}")]
    public static partial void Unparseable(ILogger logger, string reply);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Judge picked {Winner} (presented as {Slot}): {Rationale}")]
    public static partial void Decided(ILogger logger, string winner, string slot, string rationale);
}

/// <summary>
/// Judges a duel with an Azure AI Foundry chat model.
/// </summary>
/// <remarks>
/// LLM judges carry a well-documented position bias — a measurable preference for whichever
/// answer is shown first, independent of content. The two outputs are therefore assigned to
/// slots A and B by a coin flip per duel and mapped back afterwards, so the bias lands on Left
/// and Right with equal probability instead of systematically favouring Left.
/// Length and self-preference bias are not corrected for; the prompt below at least tells the
/// judge not to reward length for its own sake.
/// </remarks>
public sealed class FoundryDuelJudge : IDuelJudge
{
    private const string SystemPrompt =
        "You are judging which of two HTML documents better fulfils a user's request. " +
        "Judge only how completely and accurately each one implements what the request asked for: " +
        "every element and behaviour that was requested, correct and renderable HTML, and nothing " +
        "invented that was not asked for. Do not reward length, verbosity, or visual flourish for " +
        "its own sake — a shorter document that does everything asked beats a longer one that does not. " +
        "Reply with a single JSON object and nothing else: " +
        "{\"winner\":\"A\" or \"B\",\"reason\":\"one sentence, at most 200 characters\"}. " +
        "You must pick one. There is no tie.";

    /// <summary>
    /// Token budget for the judge's reply. The reply itself is one small JSON object, but the
    /// default deployment (gpt-5.4-nano) is a reasoning model, and on those this budget is
    /// <c>max_completion_tokens</c> — reasoning tokens are drawn from it before any content is
    /// emitted. Too small a budget returns a well-formed response with empty content, which
    /// would read as "the judge could not decide" and silently leave every duel Pending.
    /// </summary>
    private const int ReplyTokenBudget = 2000;

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly AutoJudgeOptions _options;
    private readonly ILogger<FoundryDuelJudge> _logger;

    public FoundryDuelJudge(
        HttpClient http,
        IConfiguration configuration,
        IOptions<AutoJudgeOptions> options,
        ILogger<FoundryDuelJudge> logger)
    {
        _http = http;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JudgeDecision?> JudgeAsync(
        string promptFull,
        string leftOutput,
        string rightOutput,
        CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/');
        var apiKey = _configuration["AzureAiFoundry:ApiKey"];
        var deployment = _options.Deployment;

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
        {
            JudgeLog.CallFailed(_logger, "AzureAiFoundry:Endpoint, AzureAiFoundry:ApiKey or AiJudge:Deployment is not configured.");
            return null;
        }

        // Coin flip decides which side is presented first — see the position-bias note above.
        var leftIsA = Random.Shared.Next(2) == 0;
        var slotA = Truncate(leftIsA ? leftOutput : rightOutput);
        var slotB = Truncate(leftIsA ? rightOutput : leftOutput);

        var userContent = new StringBuilder()
            .Append("REQUEST:\n").Append(promptFull).Append("\n\n")
            .Append("DOCUMENT A:\n").Append(slotA).Append("\n\n")
            .Append("DOCUMENT B:\n").Append(slotB)
            .ToString();

        var messages = new[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user", content = userContent }
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)));

        // Foundry's 429 carries its own retry hint. We don't read it here — the typed client has
        // already exhausted its fast-retry policy — but we surface the header value all the way
        // back via JudgeRateLimitedException so AutoJudge can schedule itself for the right window.
        (System.Net.HttpStatusCode Status, string Body, string? RetryAfter) response;
        try
        {
            // Same two-endpoint dance as FoundryInferenceProxy — see FoundryChatRequest.DeploymentUrl.
            response = await PostAsync(FoundryChatRequest.DeploymentUrl(endpoint, deployment), apiKey,
                FoundryChatRequest.Build(deployment, messages, ReplyTokenBudget, 0.0, stream: false, includeModelField: false),
                timeout.Token);

            if (response.Status == System.Net.HttpStatusCode.NotFound)
            {
                response = await PostAsync(FoundryChatRequest.ModelInferenceUrl(endpoint), apiKey,
                    FoundryChatRequest.Build(deployment, messages, ReplyTokenBudget, 0.0, stream: false, includeModelField: true),
                    timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            JudgeLog.CallFailed(_logger, $"timed out after {_options.TimeoutSeconds}s");
            return null;
        }
        catch (Exception ex)
        {
            JudgeLog.CallFailed(_logger, ex.Message);
            return null;
        }

        var (status, payload, retryAfter) = response;
        if (status == (System.Net.HttpStatusCode)429)
        {
            var hint = ParseRetryAfter(retryAfter);
            JudgeLog.CallFailed(_logger, $"HTTP 429 (retry after {hint.TotalSeconds:F0}s)");
            throw new JudgeRateLimitedException(hint, "HTTP 429 from judge endpoint");
        }
        if (status != System.Net.HttpStatusCode.OK)
        {
            JudgeLog.CallFailed(_logger, $"HTTP {(int)status}: {Clip(payload, 300)}");
            return null;
        }

        var replyText = ExtractContent(payload) ?? string.Empty;

        var parsed = ParseReply(replyText);
        if (parsed is null)
        {
            JudgeLog.Unparseable(_logger, Clip(replyText, 300));
            return null;
        }

        var (slot, reason) = parsed.Value;
        var verdict = (slot == 'A') == leftIsA ? DuelVerdict.Left : DuelVerdict.Right;
        JudgeLog.Decided(_logger, verdict.ToString(), slot.ToString(), reason);

        return new JudgeDecision(verdict, reason);
    }

    private async Task<(System.Net.HttpStatusCode Status, string Body, string? RetryAfter)> PostAsync(
        string url,
        string apiKey,
        Dictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var status = response.StatusCode;
        string? retryAfterHeader = null;
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            retryAfterHeader = values.FirstOrDefault();
        }

        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);
        return (status, bodyText, retryAfterHeader);
    }

    /// <summary>
    /// RFC 7231 §7.1.3 says Retry-After is either a delta-seconds integer or an HTTP-date.
    /// Real Foundry replies send the integer form; we still parse both defensively, fall back to
    /// a one-minute window so a missing/malformed header yields a sensible "try again shortly".
    /// </summary>
    private static TimeSpan ParseRetryAfter(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return TimeSpan.FromSeconds(60);
        var trimmed = header.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            // Clamp to a minute ceiling so an adversarial 86400 cannot park the demo for a day.
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
        }
        if (DateTimeOffset.TryParse(trimmed, out var when))
        {
            var delta = when - DateTimeOffset.UtcNow;
            return delta <= TimeSpan.Zero ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(Math.Min(delta.TotalSeconds, 60));
        }
        return TimeSpan.FromSeconds(60);
    }

    private string Truncate(string html)
    {
        var max = Math.Max(500, _options.MaxOutputChars);
        if (string.IsNullOrEmpty(html)) return "(this model produced no output)";
        return html.Length <= max ? html : Clip(html, max) + "\n… (truncated)";
    }

    /// <summary>
    /// Truncates without splitting a surrogate pair. The rationale is persisted to Table Storage
    /// and served as JSON; cutting an astral character (an emoji in the judge's reason, say) in
    /// half would store a lone surrogate that is not valid UTF-16 and breaks serialization.
    /// </summary>
    private static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut];
    }

    /// <summary>Pulls choices[0].message.content out of a non-streaming completion response.</summary>
    private static string? ExtractContent(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;
            if (!choices[0].TryGetProperty("message", out var message)) return null;
            return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads {"winner":"A|B","reason":"…"} out of the reply. Models wrap JSON in prose or code
    /// fences often enough that scanning for the outermost braces is worth it; anything that
    /// still does not parse into a valid winner returns null so the caller leaves the duel Pending.
    /// </summary>
    private static (char Slot, string Reason)? ParseReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("winner", out var winnerEl)) return null;

            var winner = winnerEl.GetString()?.Trim().ToUpperInvariant();
            if (winner is not ("A" or "B")) return null;

            var reason = doc.RootElement.TryGetProperty("reason", out var reasonEl)
                ? reasonEl.GetString()?.Trim()
                : null;

            return (winner[0], Clip(string.IsNullOrWhiteSpace(reason) ? "No reason given." : reason, 300));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
