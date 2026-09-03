using System.Diagnostics;
using System.IO;
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
///
/// Hot path: at ~120 tok/s the previous implementation allocated two <see cref="JsonDocument"/>
/// per frame. Every allocation on the hot path costs the GC another collection and the duel
/// another stop-the-world pause; the readers below reuse a single per-frame UTF-8 byte slice and
/// parse with <see cref="Utf8JsonReader"/> once per frame, allocating nothing for normal
/// delta frames. The metadata path only fires on terminal frames.
/// </remarks>
internal static class SseChatStreamReader
{
    /// <summary>Live preview is emitted at most this often, in milliseconds.</summary>
    private const long CallbackThrottleMs = 500;

    /// <summary>A preview accompanies the callback only once this many tokens have accrued since the last one.</summary>
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
        var state = new FrameState();
        state.Counters = new HtmlStreamCounters();
        state.LastCallbackAt = -CallbackThrottleMs; // trigger first callback immediately
        state.LastPreviewTokenCount = -PreviewEveryNTokens; // ...and a preview with it
        state.FinishReason = null;
        state.ProviderCompletionTokens = null;
        state.ProviderPromptTokens = null;
        state.ProviderReasoningTokens = null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            // Reuse one rented buffer across frames. Largest frame we've seen in production is
            // ~10 KB; 32 KB is comfortable headroom. Larger frames grow the array once.
            var buffer = new byte[32 * 1024];

            int frameStart = 0;
            int frameEnd = 0;
            int writePos = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Compact: discard already-parsed bytes.
                if (frameStart > 0)
                {
                    var remaining = frameEnd - frameStart;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, frameStart, buffer, 0, remaining);
                    frameStart = 0;
                    frameEnd = remaining;
                    writePos = remaining;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(writePos), cancellationToken);
                if (read == 0) break; // EOF
                writePos += read;

                frameEnd = 0;
                while (true)
                {
                    var nl = IndexOfByte(buffer, (byte)'\n', frameEnd, writePos);
                    if (nl < 0)
                    {
                        frameEnd = writePos;
                        break;
                    }

                    var lineEnd = nl > frameEnd && buffer[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                    var lineLen = lineEnd - frameEnd;

                    if (lineLen > 0 && StartsWithDataPrefix(buffer, frameEnd, lineLen))
                    {
                        var payloadStart = frameEnd + 6;
                        var payloadLen = lineLen - 6;
                        if (payloadLen == 6 && IsDoneMarker(buffer, payloadStart))
                        {
                            frameEnd = nl + 1;
                            goto endOfStream;
                        }

                        await ParseFrame(
                            buffer, payloadStart, payloadLen,
                            onTokenUpdate, cancellationToken,
                            state, sw);
                    }

                    frameEnd = nl + 1;
                }
            }
        endOfStream:;
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

        var html = state.Sb.ToString();
        result.HtmlOutputRaw = html;
        result.HtmlOutputSizeBytes = Encoding.UTF8.GetByteCount(html);
        result.TokenCount = state.ProviderCompletionTokens ?? state.TokenCount;
        if (state.ProviderPromptTokens is { } p) result.PromptTokenCount = p;
        if (state.ProviderReasoningTokens is { } r) result.ReasoningTokenCount = r;
        result.FinishReason = state.FinishReason;
        result.WasTruncated = string.Equals(state.FinishReason, "length", StringComparison.OrdinalIgnoreCase);
        result.WarmUpDurationMs = state.FirstTokenMs ?? warmUpMs;
        result.TotalDurationMs = sw.ElapsedMilliseconds;
        result.GenerationDurationMs = Math.Max(0L, result.TotalDurationMs - result.WarmUpDurationMs);
        result.TokenVelocity = result.GenerationDurationMs > 0
            ? Math.Round(state.TokenCount / (result.GenerationDurationMs / 1000.0), 1)
            : 0;

        if (string.IsNullOrWhiteSpace(html))
        {
            result.IsFailure = true;
            result.FailureReason = string.IsNullOrWhiteSpace(state.FinishReason)
                ? "Inference completed without output."
                : $"Inference completed without output (finish reason: {state.FinishReason}).";
        }

        return null;
    }

    /// <summary>
    /// Mutable accumulator shared across frames. A class (not a struct) because async methods
    /// cannot have <c>ref</c> parameters, and we want one state object threaded through every
    /// per-frame call so the throttle counters cannot drift apart.
    /// </summary>
    private sealed class FrameState
    {
        public readonly StringBuilder Sb = new();
        public HtmlStreamCounters Counters = new();
        public int TokenCount;
        public long? FirstTokenMs;
        public long LastCallbackAt;
        public int LastPreviewTokenCount;
        public string? FinishReason;
        public int? ProviderCompletionTokens;
        public int? ProviderPromptTokens;
        public int? ProviderReasoningTokens;
    }

    /// <summary>Per-frame work that mutates the accumulators. Allocation-free for delta frames.</summary>
    /// <remarks>
    /// Takes the live <see cref="Stopwatch"/> by ref-state rather than by parameter so the call
    /// site stays in the inner while loop without a closure allocation per frame.
    /// </remarks>
    private static async Task ParseFrame(
        byte[] buffer, int payloadStart, int payloadLen,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken,
        FrameState state,
        Stopwatch sw)
    {
        var token = TryReadContentDelta(buffer.AsSpan(payloadStart, payloadLen));

        if (token is not null)
        {
            state.Sb.Append(token);
            state.TokenCount++;

            var elapsed = sw.ElapsedMilliseconds;
            state.FirstTokenMs ??= elapsed;

            state.Counters.Accumulate(token);

            if (elapsed - state.LastCallbackAt >= CallbackThrottleMs)
            {
                state.LastCallbackAt = elapsed;

                string? preview = null;
                if (state.TokenCount - state.LastPreviewTokenCount >= PreviewEveryNTokens)
                {
                    state.LastPreviewTokenCount = state.TokenCount;
                    preview = state.Sb.ToString(0, Math.Min(PreviewMaxChars, state.Sb.Length));
                }

                await onTokenUpdate(state.TokenCount, elapsed, state.Counters.ToStats(preview));
            }
        }
        else
        {
            // No `delta.content` ⇒ likely the terminal frame (usage + finish_reason).
            TryReadTerminalMetadata(
                buffer.AsSpan(payloadStart, payloadLen),
                state);
        }
    }

    /// <summary>Pulls <c>choices[0].delta.content</c> out of one SSE payload.</summary>
    /// <remarks>
    /// Returns null for keep-alives, role-only deltas and unparseable frames, all of which are
    /// normal mid-stream and must not abort the read. Allocation budget: one string per non-empty
    /// delta; nothing for the rest.
    /// </remarks>
    private static string? TryReadContentDelta(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            // Look for { "choices": [ { "delta": { "content": "..." } } ] } in a single pass:
            // we walk the property names, skip past `choices`, then read into `delta`, then read
            // `content`. Anything else is irrelevant.
            var state = 0; // 0 = before choices, 1 = inside choices, 2 = inside delta, 3 = content read
            while (state < 3 && reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueSpan.SequenceEqual("choices"u8)) state = 1;
                    else if (state == 2 && reader.ValueSpan.SequenceEqual("content"u8))
                    {
                        // Advance past content to the string token.
                        if (!reader.Read()) return null;
                        if (reader.TokenType == JsonTokenType.String)
                        {
                            var s = reader.GetString();
                            state = 3;
                            return s;
                        }
                        return null;
                    }
                }
                else if (reader.TokenType == JsonTokenType.EndArray && state == 1)
                {
                    // Past choices without ever finding a delta.content — keep-alive or role frame.
                    return null;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed frame — skip it, the stream is still good.
        }
        return null;
    }

    /// <summary>
    /// Reads <c>finish_reason</c>, <c>usage.prompt_tokens</c>, <c>usage.completion_tokens</c>,
    /// and <c>usage.completion_tokens_details.reasoning_tokens</c> from a terminal frame.
    /// </summary>
    private static void TryReadTerminalMetadata(
        ReadOnlySpan<byte> payload,
        FrameState state)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();

                switch (name)
                {
                    case "choices":
                        // Look for finish_reason inside choices[0].
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                        {
                            reader.Skip();
                            break;
                        }
                        if (!reader.Read()) return;
                        while (reader.TokenType != JsonTokenType.EndObject && reader.Read())
                        {
                            if (reader.TokenType == JsonTokenType.PropertyName &&
                                reader.ValueSpan.SequenceEqual("finish_reason"u8) &&
                                reader.Read() && reader.TokenType == JsonTokenType.String)
                            {
                                state.FinishReason = reader.GetString();
                            }
                        }
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) reader.Skip();
                        break;

                    case "usage":
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); break; }
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            var usageName = reader.GetString();
                            switch (usageName)
                            {
                                case "prompt_tokens":
                                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var pt))
                                        state.ProviderPromptTokens = pt;
                                    break;
                                case "completion_tokens":
                                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ct))
                                        state.ProviderCompletionTokens = ct;
                                    break;
                                case "completion_tokens_details":
                                    if (!reader.Read()) return;
                                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                                    {
                                        if (reader.TokenType == JsonTokenType.PropertyName &&
                                            reader.ValueSpan.SequenceEqual("reasoning_tokens"u8) &&
                                            reader.Read() && reader.TokenType == JsonTokenType.Number &&
                                            reader.TryGetInt32(out var rt))
                                        {
                                            state.ProviderReasoningTokens = rt;
                                        }
                                    }
                                    break;
                                default:
                                    reader.Skip();
                                    break;
                            }
                        }
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed frame — skip it, the stream is still good.
        }
    }

    private static int IndexOfByte(byte[] buffer, byte target, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (buffer[i] == target) return i;
        }
        return -1;
    }

    private static bool StartsWithDataPrefix(byte[] buffer, int start, int length)
    {
        if (length < 6) return false;
        return buffer[start] == (byte)'d'
            && buffer[start + 1] == (byte)'a'
            && buffer[start + 2] == (byte)'t'
            && buffer[start + 3] == (byte)'a'
            && buffer[start + 4] == (byte)':'
            && buffer[start + 5] == (byte)' ';
    }

    private static bool IsDoneMarker(byte[] buffer, int start)
    {
        // "[DONE]" is six bytes; the caller has already stripped "data: " from the start.
        return start + 6 <= buffer.Length
            && buffer[start] == '['
            && buffer[start + 1] == 'D'
            && buffer[start + 2] == 'O'
            && buffer[start + 3] == 'N'
            && buffer[start + 4] == 'E'
            && buffer[start + 5] == ']';
    }
}
