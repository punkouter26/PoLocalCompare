namespace PoLocalCompare.Shared.Enums;

/// <summary>
/// Verdict states for a duel.
/// Pending: Results received, awaiting user judgment.
/// Left: User selected the left model as winner.
/// Right: User selected the right model as winner.
/// Expired: Verdict timeout exceeded (no judgment within configured window).
/// </summary>
public enum DuelVerdict
{
    Pending,
    Left,
    Right,
    Expired
}