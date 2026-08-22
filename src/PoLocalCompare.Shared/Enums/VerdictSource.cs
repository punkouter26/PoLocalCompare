namespace PoLocalCompare.Shared.Enums;

/// <summary>
/// Who decided a duel. Recorded on every verdict so an LLM-judged leaderboard can be told
/// apart from a human-judged one after the fact — the two are different signals and blending
/// them irreversibly would make the ELO column uninterpretable.
/// </summary>
public enum VerdictSource
{
    /// <summary>A person picked the winner in the Arena.</summary>
    Human,

    /// <summary>The auto-judge decided because no human picked within the grace window.</summary>
    Ai,

    /// <summary>
    /// A challenge budget decided it: one model stayed inside the ceiling and the other did not,
    /// so the match was forfeited rather than judged on the merits.
    /// </summary>
    /// <remarks>
    /// Its own value rather than folded into <see cref="Ai"/> because it is a different signal
    /// again — nothing looked at the outputs. A leaderboard that blended "wrote the better page"
    /// with "was the only one under five seconds" would be uninterpretable in exactly the way
    /// this enum exists to prevent.
    /// </remarks>
    Constraint
}
