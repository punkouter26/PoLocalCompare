// SOLID: Dependency Inversion — the auto-judge orchestration does not know how judging happens
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Judging;

/// <param name="Verdict"><see cref="DuelVerdict.Left"/>, <see cref="DuelVerdict.Right"/>, <see cref="DuelVerdict.Tie"/>, or <see cref="DuelVerdict.Voided"/>.</param>
/// <param name="Rationale">One sentence, shown in the Arena and stored on the duel.</param>
/// <param name="IsWalkover">
/// True when the decision is mechanical — an opponent that produced nothing, or a duel voided
/// for missing results — rather than the output of an LLM comparison. The auto-judge uses it to
/// stamp <see cref="VerdictSource.Constraint"/> and a null judge model, so a forfeit can never
/// masquerade in analytics as "the AI judge read both pages and preferred this one".
/// </param>
public sealed record JudgeDecision(DuelVerdict Verdict, string Rationale, bool IsWalkover = false);

/// <summary>
/// Raised when a judge call hit a recoverable upstream failure — the most common case is an
/// HTTP 429 with a <c>Retry-After</c> header, but the same signal fits an empty response from
/// a temporarily-throttled deployment. Callers may choose to retry after <see cref="RetryAfter"/>
/// has elapsed; the judge itself has already exhausted its own fast retry policy.
/// </summary>
/// <remarks>
/// Distinct from a returned <c>null</c> decision, which signals "no evidence, do not move ELO".
/// A <see cref="JudgeRateLimitedException"/> is "evidence pending, please try again shortly".
/// AutoJudge translates the former into "leave the duel Pending"; the latter into "re-queue
/// after the requested delay".
/// </remarks>
public sealed class JudgeRateLimitedException : Exception
{
    public JudgeRateLimitedException(TimeSpan retryAfter, string reason)
        : base($"Judge rate-limited: {reason}")
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Suggested wait before the next attempt. Zero or negative means "no hint".</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>
/// Decides which of two outputs follows the prompt more accurately.
/// </summary>
public interface IDuelJudge
{
    /// <summary>
    /// Returns null when no decision could be reached on the merits — an unparseable reply,
    /// or a genuinely undecidable pairing. Callers must leave the duel Pending in that case
    /// rather than guessing; a coin-flip verdict would move ELO on no evidence.
    /// </summary>
    /// <exception cref="JudgeRateLimitedException">
    /// Thrown when the upstream rate-limited the request. Distinct from a null return:
    /// a null says "no evidence", a rate limit says "evidence pending, please retry".
    /// </exception>
    Task<JudgeDecision?> JudgeAsync(
        string promptFull,
        string leftOutput,
        string rightOutput,
        CancellationToken cancellationToken);
}
