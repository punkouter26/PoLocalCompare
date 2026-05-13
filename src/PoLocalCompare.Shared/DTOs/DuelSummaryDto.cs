using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelSummaryDto
{
    public string DuelId { get; init; } = string.Empty;
    public string PromptText { get; init; } = string.Empty;
    public string PromptSummary { get; init; } = string.Empty;
    public string LeftModelId { get; init; } = string.Empty;
    public string LeftModelName { get; init; } = string.Empty;
    public string RightModelId { get; init; } = string.Empty;
    public string RightModelName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DuelVerdict Verdict { get; init; }
    public string? WinnerModelId { get; init; }
    public string? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public int? LeftOutputQualityScore { get; init; }
    public int? RightOutputQualityScore { get; init; }
    public double? AvgOutputQualityScore { get; init; }
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; init; }
}