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
}