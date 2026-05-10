// SOLID: Single Responsibility
namespace PoLocalCompare.Application.Duels.CommenceDuel;

public sealed record CommenceDuelCommand(
    string LeftModelId,
    string RightModelId,
    string PromptText);
