// SOLID: Single Responsibility
namespace PoLocalCompare.Api.Features.Duels;

public sealed record CommenceDuelCommand(
    string LeftModelId,
    string RightModelId,
    string PromptText)
{
    public const int MaxPromptLength = 10000;
    public const int MinPromptLength = 10;
    public const int DefaultVerdictDeadlineHours = 24;

    public string LeftModelId { get; init; } = LeftModelId;
    public string RightModelId { get; init; } = RightModelId;
    public string PromptText { get; init; } = PromptText;
}