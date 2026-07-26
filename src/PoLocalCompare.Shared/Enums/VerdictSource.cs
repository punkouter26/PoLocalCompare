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
    Ai
}
