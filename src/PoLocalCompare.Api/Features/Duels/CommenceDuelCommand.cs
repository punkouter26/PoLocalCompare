// SOLID: Single Responsibility
namespace PoLocalCompare.Api.Features.Duels;

/// <param name="AutoJudgeDelaySecondsOverride">
/// Replaces <c>AiJudge:DelaySeconds</c> for this duel only. Demo mode passes 0 so the judge
/// decides the instant a duel finishes; leaving it null keeps the configured grace window, which
/// is what a person judging by hand needs. It is never persisted — it governs this execution,
/// not the duel record.
/// </param>
public sealed record CommenceDuelCommand(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText,
    int? AutoJudgeDelaySecondsOverride = null)
{
    public const int MaxPromptLength = 10000;
    public const int MinPromptLength = 10;
    public const int DefaultVerdictDeadlineHours = 24;
}