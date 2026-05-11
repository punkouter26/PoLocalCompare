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
}