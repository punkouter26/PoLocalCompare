using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>One scheduled duel in a demo run, resolved server-side so the queue is visible before it starts.</summary>
public sealed class DemoRoundDto
{
    public int Index { get; init; }
    public string PromptId { get; init; } = string.Empty;
    public string PromptTitle { get; init; } = string.Empty;
    public string PromptEmoji { get; init; } = string.Empty;
    public string PromptText { get; init; } = string.Empty;
    public ModelId LeftModelId { get; init; }
    public string LeftModelName { get; init; } = string.Empty;
    public ModelId RightModelId { get; init; }
    public string RightModelName { get; init; } = string.Empty;
}

/// <summary>
/// The full schedule for an unattended demo run.
/// </summary>
/// <remarks>
/// Demo duels are ordinary duels — persisted, judged, and moving ELO exactly like any other —
/// so the plan is returned up front rather than improvised round by round. The page shows what
/// is about to run before anything is written.
/// </remarks>
public sealed class DemoPlanDto
{
    public IReadOnlyList<DemoRoundDto> Rounds { get; init; } = [];

    /// <summary>Remote models eligible to be paired.</summary>
    public int AvailableModels { get; init; }

    /// <summary>Why no plan could be produced; null when <see cref="Rounds"/> is populated.</summary>
    public string? UnavailableReason { get; init; }

    public bool CanRun => Rounds.Count > 0;
}
