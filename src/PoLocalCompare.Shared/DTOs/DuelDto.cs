using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelDto
{
    public string DuelId { get; init; } = string.Empty;
    public string PromptText { get; init; } = string.Empty;
    public string PromptFull { get; init; } = string.Empty;
    public string LeftModelId { get; init; } = string.Empty;
    public string RightModelId { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DuelVerdict Verdict { get; init; }
    public string? WinnerModelId { get; init; }
    public string? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public int TimeLimitSeconds { get; init; }
    public IReadOnlyList<DuelResultDto> Results { get; init; } = [];
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; init; }

    /// <summary>Whether a person or the auto-judge decided this duel.</summary>
    public VerdictSource VerdictSource { get; init; }

    /// <summary>The auto-judge's stated reason, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeRationale { get; init; }

    /// <summary>
    /// Seconds the Arena waits for a human pick before the auto-judge decides; 0 means the
    /// auto-judge is off. The window is measured from <see cref="CompletedAt"/>, not from when
    /// the client read this — a page opened late must resume the clock, not restart it.
    /// </summary>
    public int AutoJudgeDelaySeconds { get; init; }
}