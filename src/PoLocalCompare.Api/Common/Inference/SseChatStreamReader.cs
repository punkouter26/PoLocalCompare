using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Reads an OpenAI-compatible streaming chat completion (<c>data: </c> SSE lines) to completion,
/// accumulating the HTML output, token count, live HTML counters and throttled progress callbacks,
/// then stamps the timing/telemetry fields on the <see cref="DuelResult"/>.
/// </summary>
/// <remarks>
/// Every <see cref="IRemoteInferenceProxy"/> talks the same OpenAI SSE dialect once the request is
/// away, so only request construction and error semantics actually vary by provider. This type owns
/// the identical half; the proxies keep the half that differs. It is deliberately a plain static
/// helper rather than a service — it holds no state between calls and takes the already-connected
/// response, which keeps each proxy in charge of its own auth, retry and logging policy.
/// </remarks>
internal static class SseChatStreamReader
{
    /// <summary>Live preview is emitted at most this often, in milliseconds.</summary>
    private const long CallbackThrottleMs = 500;

    /// <summary>A preview accompanies the callback only every Nth token.</summary>
    private const int PreviewEveryNTokens = 25;

    /// <summary>Longest partial document sent to the client mid-stream.</summary>
    private const int PreviewMaxChars = 5000;

    /// <summary>
    /// Consumes <paramref name="response"/> and fills <paramref name="result"/>.
    /// </summary>
    /// <param name="warmUpMs">
    /// Time-to-first-byte, used as the warm-up figure when the stream yields no token at all.
    /// </param>
    /// <returns>
    /// <c>null</c> when the stream completed; otherwise the exception that ended it. The failure
    /// fields on <paramref name="result"/> are already set — the caller only chooses how to log.
    /// </returns>
    internal static async Task<Exception?> ReadIntoAsync(
        HttpResponseMessage response,
        DuelResult result,
        Stopwatch sw,
        long warmUpMs,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        int tokenCount = 0;
        long? firstTokenMs = null; // time-to-first-token (actual warm-up per PRD)
        var counters = new HtmlStreamCounters();
        long lastCallbackAt = -CallbackThrottleMs; // trigger first callback immediately
        string? finishReason = null;
        int? providerCompletionTokens = null;

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

                TryReadCompletionMetadata(data, result, ref finishReason, ref providerCompletionTokens);
                var token = TryReadDelta(data);
                if (token is null) continue;

                sb.Append(token);
                tokenCount++;

                var elapsed = sw.ElapsedMilliseconds;
                firstTokenMs ??= elapsed;

                // Update HTML stats incrementally
                counters.Accumulate(token);

                if (elapsed - lastCallbackAt >= CallbackThrottleMs)
                {
                    lastCallbackAt = elapsed;
                    // ToString(0, n) copies only the prefix; ToString()[..n] materialised the
                    // whole accumulated document first, twice a second, just to slice it.
                    string? preview = tokenCount % PreviewEveryNTokens == 0
                        ? sb.ToString(0, Math.Min(PreviewMaxChars, sb.Length))
                        : null;
                    await onTokenUpdate(tokenCount, elapsed, counters.ToStats(preview));
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            result.IsFailure = true;
            result.FailureReason = "Inference cancelled (timeout or user abort).";
            return ex;
        }
        catch (Exception ex)
        {
            result.IsFailure = true;
            result.FailureReason = $"Stream read error: {ex.Message}";
            return ex;
        }

        sw.Stop();

        // Normalization + density/size are applied centrally by DuelResultEnricher.
        var html = sb.ToString();
        result.HtmlOutputRaw = html;
        result.HtmlOutputSizeBytes = Encoding.UTF8.GetByteCount(html);
        result.TokenCount = providerCompletionTokens ?? tokenCount;
        result.FinishReason = finishReason;
        result.WasTruncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
        result.WarmUpDurationMs = firstTokenMs ?? warmUpMs; // first-token latency
        result.TotalDurationMs = sw.ElapsedMilliseconds;
        result.GenerationDurationMs = Math.Max(0L, result.TotalDurationMs - result.WarmUpDurationMs);
        result.TokenVelocity = result.GenerationDurationMs > 0
            ? Math.Round(tokenCount / (result.GenerationDurationMs / 1000.0), 1)
            : 0;

        if (string.IsNullOrWhiteSpace(html))
        {
            result.IsFailure = true;
            result.FailureReason = string.IsNullOrWhiteSpace(finishReason)
                ? "Inference completed without output."
                : $"Inference completed without output (finish reason: {finishReason}).";
        }

        return null;
    }

    private static void TryReadCompletionMetadata(
        string data,
        DuelResult result,
        ref string? finishReason,
        ref int? providerCompletionTokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                finishReason = reason.GetString();
            }

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;
            if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var promptCount))
                result.PromptTokenCount = promptCount;
            if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var completionCount))
                providerCompletionTokens = completionCount;
            if (usage.TryGetProperty("completion_tokens_details", out var details) &&
                details.TryGetProperty("reasoning_tokens", out var reasoningTokens) && reasoningTokens.TryGetInt32(out var reasoningCount))
            {
                result.ReasoningTokenCount = reasoningCount;
            }
        }
        catch (JsonException)
        {
            // The delta parser handles malformed provider frames the same way.
        }
    }

    /// <summary>
    /// Pulls <c>choices[0].delta.content</c> out of one SSE payload.
    /// Returns null for keep-alives, role-only deltas and unparseable frames, all of which are
    /// normal mid-stream and must not abort the read.
    /// </summary>
    private static string? TryReadDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                delta.TryGetProperty("content", out var contentEl) &&
                contentEl.ValueKind == JsonValueKind.String)
            {
                return contentEl.GetString();
            }
        }
        catch (JsonException)
        {
            // Malformed frame — skip it, the stream is still good.
        }

        return null;
    }
}
