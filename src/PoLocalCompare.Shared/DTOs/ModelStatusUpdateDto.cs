using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>SignalR message shape for processing-phase status updates.</summary>
public sealed class ModelStatusUpdateDto
{
    public string DuelId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty; // "Left" | "Right"
    public DuelStatus Status { get; init; }
    public long ElapsedMs { get; init; }
    public int TokenCount { get; init; }
    public double? TokenVelocity { get; init; }
    /// <summary>Highest tok/s seen so far this run.</summary>
    public double? PeakTokenVelocity { get; init; }
    /// <summary>Milliseconds from request start until the first token arrived (latency / warm-up).</summary>
    public long? WarmUpMs { get; init; }
    /// <summary>True when no new tokens have arrived for >2s during Generating.</summary>
    public bool IsStalled { get; init; }
    // --- HTML content stats (computed from streaming output) ---
    /// <summary>Number of opening HTML tags seen so far.</summary>
    public int? HtmlTagCount { get; init; }
    /// <summary>Current nesting depth of open/unclosed tags.</summary>
    public int? OpenTagDepth { get; init; }
    /// <summary>Number of CSS rule blocks ({) seen inside &lt;style&gt; sections.</summary>
    public int? StyleRuleCount { get; init; }
    /// <summary>0–1 repetition score; &gt;0.3 = possible looping.</summary>
    public double? RepetitionScore { get; init; }
    // --- Local model only ---
    /// <summary>WebLLM prefill throughput in tok/s (prompt processing speed).</summary>
    public double? PrefillSpeedTps { get; init; }
    /// <summary>Whether the model weights were already cached in memory (fast warm-up).</summary>
    public bool? CacheHit { get; init; }
    /// <summary>Optional sub-phase detail, e.g. WebLLM loading progress text.</summary>
    public string? Detail { get; init; }
    /// <summary>Partial HTML generated so far; included every ~25 tokens for live preview.</summary>
    public string? HtmlPreview { get; init; }
}
