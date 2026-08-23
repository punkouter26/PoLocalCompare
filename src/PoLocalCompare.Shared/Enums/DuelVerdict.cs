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
/// </remarks>
public enum DuelVerdict
{
    Pending,
    Left,
    Right,
    Tie
}
