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

            // Reuse one buffer across frames. Largest frame we've seen in production is
            // ~10 KB; 32 KB is comfortable headroom. Larger frames grow the array.
            var buffer = new byte[32 * 1024];

            // Bytes in [scanPos, writePos) have arrived but are not yet parsed: a read appends
            // at writePos, the scanner advances scanPos past each complete line, and whatever
            // partial line is left gets compacted back to index 0 before the next read.
            //
            // Both counters have to move. An earlier version reset the scan to 0 after every
            // read and never advanced the compaction cursor, so each read re-parsed every frame
            // already seen — duplicating output — and never freed buffer space, which meant a
            // response longer than the buffer ended up reading into a zero-length destination
            // and stopping silently at 32 KB as if the stream had reached EOF.
            int scanPos = 0;
            int writePos = 0;

            while (true)
            {
                // Throw rather than exit the loop quietly. Cancellation here is the duel
                // watchdog or a user abort, and falling out of the loop would land on the
                // empty-output path below and report a timeout as "Inference completed without
                // output" — a model failure, attributed to the wrong thing.
                cancellationToken.ThrowIfCancellationRequested();

                // Compact: discard already-parsed bytes.
                if (scanPos > 0)
                {
                    var remaining = writePos - scanPos;
                    if (remaining > 0)
                        Buffer.BlockCopy(buffer, scanPos, buffer, 0, remaining);
                    scanPos = 0;
                    writePos = remaining;
                }

                // A single frame larger than the whole buffer leaves nowhere to read into, and
                // a zero-length read is indistinguishable from EOF. Grow instead of truncating.
                if (writePos == buffer.Length)
                    Array.Resize(ref buffer, buffer.Length * 2);

                var read = await stream.ReadAsync(buffer.AsMemory(writePos), cancellationToken);
                if (read == 0) break; // EOF
                writePos += read;

                while (true)
                {
                    var nl = IndexOfByte(buffer, (byte)'\n', scanPos, writePos);
                    if (nl < 0) break; // partial line — wait for the rest of it

                    var lineEnd = nl > scanPos && buffer[nl - 1] == (byte)'\r' ? nl - 1 : nl;
                    var lineLen = lineEnd - scanPos;

                    if (lineLen > 0 && StartsWithDataPrefix(buffer, scanPos, lineLen))
                    {
                        var payloadStart = scanPos + 6;
                        var payloadLen = lineLen - 6;
                        if (payloadLen == 6 && IsDoneMarker(buffer, payloadStart))
                        {
                            scanPos = nl + 1;
                            goto endOfStream;
                        }

                        await ParseFrame(
                            buffer, payloadStart, payloadLen,
                            onTokenUpdate, cancellationToken,
                            state, sw);
                    }

                    scanPos = nl + 1;
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
            // walk to `choices`, then into the first element's `delta`, then read `content`.
            //
            // Both containers are tracked by their own depth rather than by a flat scan of
            // property names. A frame carries sibling objects at the same level as `delta`
            // (`content_filter_results`, `logprobs`, and whatever a provider adds next), so a
            // bare name match can land on the wrong `content`; and only the `choices` array's
            // own EndArray means "no delta here", not a nested one. An earlier version tracked
            // this with an int state that nothing ever advanced past `choices` — the content
            // branch was unreachable, so every delta read as null and every remote duel
            // finished with empty output and no error.
            var choicesDepth = -1;

            while (reader.Read())
            {
                if (choicesDepth >= 0
                    && reader.TokenType == JsonTokenType.EndArray
                    && reader.CurrentDepth == choicesDepth)
                {
                    // Past choices without ever finding a delta — keep-alive or terminal frame.
                    return null;
                }

                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (choicesDepth < 0 && reader.ValueSpan.SequenceEqual("choices"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
                    choicesDepth = reader.CurrentDepth;
                }
                else if (choicesDepth >= 0 && reader.ValueSpan.SequenceEqual("delta"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
                    var deltaDepth = reader.CurrentDepth;

                    while (reader.Read())
                    {
                        // Role-only delta, a refusal, or a terminal `"delta":{}`: no visible token.
                        if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == deltaDepth)
                            return null;

                        if (reader.TokenType == JsonTokenType.PropertyName
                            && reader.CurrentDepth == deltaDepth + 1
                            && reader.ValueSpan.SequenceEqual("content"u8))
                        {
                            // Advance past content to the string token.
                            if (!reader.Read()) return null;
                            return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                        }
                    }

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
    /// <remarks>
    /// Depth-anchored for the same reason as <see cref="TryReadContentDelta"/>. Walking the
    /// token stream flat looks equivalent and is not: the terminal frame's <c>choices[0]</c>
    /// opens with <c>"content_filter_results":{}</c>, and a scan that stops at the first
    /// EndObject it sees stops on *that* one — before ever reaching <c>finish_reason</c>. The
    /// usage frame fails the mirror way, its empty <c>choices</c> array letting the scan run on
    /// and swallow the <c>usage</c> object it was looking for. Both left every duel with a null
    /// finish reason and no provider token counts, which is invisible until something downstream
    /// (WasTruncated, cost, tok/s) quietly reads zero.
    /// </remarks>
    private static void TryReadTerminalMetadata(
        ReadOnlySpan<byte> payload,
        FrameState state)
    {
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
            var rootDepth = reader.CurrentDepth;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == rootDepth)
                    return;

                // Only the frame's own top-level keys are interesting; the depth check is what
                // keeps a nested `usage` or `choices` from being mistaken for the real one.
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != rootDepth + 1)
                    continue;

                if (reader.ValueSpan.SequenceEqual("choices"u8))
                {
                    if (!reader.Read()) return;
                    if (reader.TokenType == JsonTokenType.StartArray) ReadFinishReason(ref reader, state);
                }
                else if (reader.ValueSpan.SequenceEqual("usage"u8))
                {
                    // `"usage":null` on every non-terminal frame — only the object form counts.
                    if (!reader.Read()) return;
                    if (reader.TokenType == JsonTokenType.StartObject) ReadUsage(ref reader, state);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed frame — skip it, the stream is still good.
        }
    }

    /// <summary>Reads <c>choices[].finish_reason</c>; the reader starts on the array's StartArray.</summary>
    private static void ReadFinishReason(ref Utf8JsonReader reader, FrameState state)
    {
        var arrayDepth = reader.CurrentDepth;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == arrayDepth)
                return;

            // Depth + 2 is an element's own key: choices[N] sits one level in, its keys another.
            if (reader.TokenType == JsonTokenType.PropertyName
                && reader.CurrentDepth == arrayDepth + 2
                && reader.ValueSpan.SequenceEqual("finish_reason"u8))
            {
                if (!reader.Read()) return;
                if (reader.TokenType == JsonTokenType.String) state.FinishReason = reader.GetString();
            }
        }
    }

    /// <summary>
    /// Reads <c>prompt_tokens</c>, <c>completion_tokens</c> and
    /// <c>completion_tokens_details.reasoning_tokens</c>; the reader starts on usage's StartObject.
    /// </summary>
    private static void ReadUsage(ref Utf8JsonReader reader, FrameState state)
    {
        var usageDepth = reader.CurrentDepth;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == usageDepth)
                return;

            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.CurrentDepth == usageDepth + 1)
            {
                if (reader.ValueSpan.SequenceEqual("prompt_tokens"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var pt))
                        state.ProviderPromptTokens = pt;
                }
                else if (reader.ValueSpan.SequenceEqual("completion_tokens"u8))
                {
                    if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ct))
                        state.ProviderCompletionTokens = ct;
                }
            }
            // One level deeper: inside completion_tokens_details.
            else if (reader.CurrentDepth == usageDepth + 2
                     && reader.ValueSpan.SequenceEqual("reasoning_tokens"u8))
            {
                if (reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var rt))
                    state.ProviderReasoningTokens = rt;
            }
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
