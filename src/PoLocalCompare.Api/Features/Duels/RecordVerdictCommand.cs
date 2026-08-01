using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <param name="Source">
/// Defaults to Human so every existing caller keeps its meaning; only the auto-judge passes Ai.
/// </param>
public sealed record RecordVerdictCommand(
    DuelId DuelId,
    DuelVerdict Verdict,
    VerdictSource Source = VerdictSource.Human,
    string? JudgeRationale = null,
    string? JudgeModel = null);