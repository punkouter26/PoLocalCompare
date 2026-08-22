namespace PoLocalCompare.Shared.Enums;

/// <summary>
/// The budget a challenge duel is fought under.
/// </summary>
/// <remarks>
/// Every one of these is measured from a value the app already records on every duel result —
/// duration, API cost, token count — so a challenge adds a rule, not a new measurement. That
/// matters because the rule has to be checkable after the fact from the stored row alone.
/// </remarks>
public enum ChallengeKind
{
    /// <summary>Not a challenge duel. The default, so an ordinary duel reads back correctly.</summary>
    None = 0,

    /// <summary>Wall-clock seconds from start to last token, against <c>TotalDurationMs</c>.</summary>
    MaxSeconds = 1,

    /// <summary>US dollars of API spend, against <c>ApiCostUsd</c>. A free local model always passes.</summary>
    MaxCostUsd = 2,

    /// <summary>Completion tokens, against <c>TokenCount</c> — a budget on verbosity.</summary>
    MaxTokens = 3,
}
