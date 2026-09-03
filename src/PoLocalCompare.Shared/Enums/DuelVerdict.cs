namespace PoLocalCompare.Shared.Enums;

/// <summary>
/// Verdict states for a duel.
/// Pending: Results received, awaiting judgment (nobody has decided yet).
/// Left: The left model was picked as winner.
/// Right: The right model was picked as winner.
/// Tie: A judge compared both outputs and found them materially equivalent.
/// </summary>
/// <remarks>
/// <see cref="Tie"/> is a decision, not the absence of one. The judge prompt has always been
/// able to answer "Tie" (<c>FoundryDuelJudge</c> constrains the reply to A|B|Tie) but the enum
/// had nowhere to put it, so a tie was discarded and the duel stayed <see cref="Pending"/> —
/// indistinguishable in the UI from "never judged" and from "the judge was unreachable". It is
/// a terminal state that moves no ELO: both models bank a duel and a draw, ratings are unchanged.
///
/// There used to be a fifth state, <c>Expired</c>, for a duel that went unjudged past a
/// 24-hour deadline. It was removed on 2026-08-23 because nothing could reach it coherently:
/// no sweeper ever set it, the only writer was the verdict handler refusing a late submission,
/// no test covered it, and no view rendered it — so a duel that somehow became Expired showed
/// the Archive's "Judge" button and the Arena's winner prompt, both of which the server would
/// then reject with a message pointing at an "expiration workflow" that did not exist. With the
/// AI judge deciding inside ten seconds it was unreachable in the default configuration anyway.
///
/// Removing it is safe for stored data: the verdict round-trips as a string, and
/// <c>Enum.TryParse</c> falls back to <see cref="Pending"/>, so any legacy "Expired" row simply
/// becomes judgeable again — which is the better outcome for it in any case.
///
/// <see cref="Voided">Voided</see> is the newer sibling and exists precisely because
/// <c>Expired</c> did not: it has two live writers — the startup recovery sweeper (a duel the
/// process died in the middle of, where a model never reported) and the auto-judge's
/// both-models-failed path — so it can never be an unreachable label. Unlike a tie it banks
/// nothing: no duel count, no draw, no history row. There is no evidence to bank.
/// </remarks>
public enum DuelVerdict
{
    Pending,
    Left,
    Right,
    Tie,

    /// <summary>
    /// Terminal with no judgment possible: both models failed to produce output, or the run was
    /// abandoned before every model reported. No ELO, no duel count, no history — and unlike
    /// <see cref="Pending"/> it is finished, so it never shows up as "awaiting judgment".
    /// </summary>
    Voided
}
