using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Streaming telemetry for one side of a duel. Both sides of the Arena used to carry the
/// same five parallel fields (<c>_leftStatus</c> / <c>_rightStatus</c>, etc.) — a row of
/// declarations that took 10 lines, plus a dozen update sites that wrote one side or the
/// other based on a <c>Side == "Left"</c> branch. Grouping the fields per side shrinks the
/// declaration and lets an update site pick its side with a single index.
/// </summary>
/// <remarks>
/// Mutable by design: status updates arrive many times per second and a per-update
/// allocation would be churn for nothing. The shape is the streaming subset only —
/// static per-duel state (which side runs in the browser, the previous status, the
/// "last token at" timing) stays on the page because they are read in different places
/// than the live telemetry.
/// </remarks>
public sealed class SideMetrics
{
    public DuelStatus Status { get; set; } = DuelStatus.Initializing;

    public int TokenCount { get; set; }

    public double? TokenVelocity { get; set; }

    public double? PeakVelocity { get; set; }

    /// <summary>Bounded list of recent tok/s samples for the sparkline.</summary>
    public List<double> VelocityHistory { get; } = new();

    public long? WarmUpMs { get; set; }

    public string? StallDetail { get; set; }
}

/// <summary>Which side of the duel a piece of state belongs to.</summary>
public enum Side
{
    Left,
    Right,
}
