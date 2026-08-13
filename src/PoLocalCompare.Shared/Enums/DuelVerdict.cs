namespace PoLocalCompare.Shared.Enums;

/// <summary>
/// Verdict states for a duel.
/// Pending: Results received, awaiting judgment (nobody has decided yet).
/// Left: The left model was picked as winner.
/// Right: The right model was picked as winner.
/// Tie: A judge compared both outputs and found them materially equivalent.
/// Expired: Verdict timeout exceeded (no judgment within configured window).
/// </summary>
/// <remarks>
/// <see cref="Tie"/> is a decision, not the absence of one. The judge prompt has always been
/// able to answer "Tie" (<c>FoundryDuelJudge</c> constrains the reply to A|B|Tie) but the enum
/// had nowhere to put it, so a tie was discarded and the duel stayed <see cref="Pending"/> —
/// indistinguishable in the UI from "never judged" and from "the judge was unreachable". It is
/// a terminal state that moves no ELO: both models bank a duel and a draw, ratings are unchanged.
/// </remarks>
public enum DuelVerdict
{
    Pending,
    Left,
    Right,
    Expired,
    Tie
}