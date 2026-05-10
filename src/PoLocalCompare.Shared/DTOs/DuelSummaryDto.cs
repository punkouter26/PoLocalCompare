using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelSummaryDto
{
    public string DuelId { get; init; } = string.Empty;
    public string PromptSummary { get; init; } = string.Empty;
    public string LeftModelId { get; init; } = string.Empty;
    public string LeftModelName { get; init; } = string.Empty;
    public string RightModelId { get; init; } = string.Empty;
    public string RightModelName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DuelVerdict Verdict { get; init; }
    public string? WinnerModelId { get; init; }
}
