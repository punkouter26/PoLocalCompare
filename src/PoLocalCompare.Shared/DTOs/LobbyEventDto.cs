using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>What happened to a duel, broadcast to every connected client rather than to one duel's group.</summary>
public enum LobbyEventKind
{
    /// <summary>Inference has begun.</summary>
    DuelStarted,
    /// <summary>Both models finished; the duel is now awaiting a verdict.</summary>
    DuelCompleted,
    /// <summary>A verdict landed and ELO moved.</summary>
    VerdictRecorded,
}

/// <summary>
/// One line of the global activity ticker.
/// </summary>
/// <remarks>
/// Deliberately display-shaped: it carries resolved model names rather than ids, because the
/// ticker renders in the nav on every page and must not trigger a lookup per event. It carries
/// no output HTML for the same reason — this is a notification, and anyone who wants the duel
/// follows the id.
/// </remarks>
public sealed class LobbyEventDto
{
    public LobbyEventKind Kind { get; init; }
    public DuelId DuelId { get; init; }
    public string PromptSummary { get; init; } = string.Empty;
    public string LeftModelName { get; init; } = string.Empty;
    public string RightModelName { get; init; } = string.Empty;

    /// <summary>Set only on <see cref="LobbyEventKind.VerdictRecorded"/>.</summary>
    public string? WinnerModelName { get; init; }

    /// <summary>Whether a person or the auto-judge decided it; set only on a verdict event.</summary>
    public VerdictSource? Source { get; init; }

    /// <summary>The winner's rating gain; set only on a verdict event.</summary>
    public double? EloShiftWinner { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
