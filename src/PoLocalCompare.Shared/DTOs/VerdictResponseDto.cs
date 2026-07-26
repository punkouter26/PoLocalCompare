using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class VerdictResponseDto
{
    public string DuelId { get; init; } = string.Empty;
    public DuelVerdict Verdict { get; init; }
    public string? WinnerModelId { get; init; }
    public string? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public double? WinnerEloAfter { get; init; }
    public double? LoserEloAfter { get; init; }

    /// <summary>Whether a person or the auto-judge decided this duel.</summary>
    public VerdictSource Source { get; init; }

    /// <summary>The auto-judge's stated reason, when <see cref="Source"/> is Ai.</summary>
    public string? JudgeRationale { get; init; }
}