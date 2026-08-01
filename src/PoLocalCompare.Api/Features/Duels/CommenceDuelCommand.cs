// SOLID: Single Responsibility
namespace PoLocalCompare.Api.Features.Duels;

public sealed record CommenceDuelCommand(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText)
{
    public const int MaxPromptLength = 10000;
    public const int MinPromptLength = 10;
    public const int DefaultVerdictDeadlineHours = 24;
}