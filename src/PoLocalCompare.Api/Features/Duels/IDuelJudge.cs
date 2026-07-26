// SOLID: Dependency Inversion — the auto-judge orchestration does not know how judging happens
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <param name="Verdict">Always <see cref="DuelVerdict.Left"/> or <see cref="DuelVerdict.Right"/>.</param>
/// <param name="Rationale">One sentence, shown in the Arena and stored on the duel.</param>
public sealed record JudgeDecision(DuelVerdict Verdict, string Rationale);

/// <summary>
/// Decides which of two outputs follows the prompt more accurately.
/// </summary>
public interface IDuelJudge
{
    /// <summary>
    /// Returns null when no decision could be reached — a transport failure, an unparseable
    /// reply, or a genuinely undecidable pairing. Callers must leave the duel Pending in that
    /// case rather than guessing; a coin-flip verdict would move ELO on no evidence.
    /// </summary>
    Task<JudgeDecision?> JudgeAsync(
        string promptFull,
        string leftOutput,
        string rightOutput,
        CancellationToken cancellationToken);
}
