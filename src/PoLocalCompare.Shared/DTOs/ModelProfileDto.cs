using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>One point on a model's rating curve.</summary>
/// <remarks>
/// Carries the opponent and outcome as well as the number, because a rating chart with no
/// causes on it is just a squiggle — the interesting question about a drop is always who it
/// was against.
/// </remarks>
public sealed class EloPointDto
{
    public DateTimeOffset At { get; init; }
    public double Elo { get; init; }
    public double Shift { get; init; }

    /// <summary>"Win", "Loss" or "Draw", as recorded on the history row.</summary>
    public string Outcome { get; init; } = string.Empty;
    public ModelId OpponentModelId { get; init; }
    public string OpponentName { get; init; } = string.Empty;
    public DuelId DuelId { get; init; }
}

/// <summary>A duel this model won, with the artifact it won with.</summary>
/// <remarks>
/// The HTML is the point: everything else about a model is a number, and the whole app exists
/// because the thing being compared is something you look at. Bounded hard by
/// <see cref="ModelProfileDto.GalleryLimit"/> — each item is a full document that a viewport
/// will render.
/// </remarks>
public sealed class WinningOutputDto
{
    public DuelId DuelId { get; init; }
    public string PromptSummary { get; init; } = string.Empty;
    public string OpponentName { get; init; } = string.Empty;
    public DateTimeOffset WonAt { get; init; }
    public string HtmlOutputRaw { get; init; } = string.Empty;
}

/// <summary>
/// Everything the model page shows: standing, rating history, head-to-head record, running
/// costs, and a gallery of what it actually produced.
/// </summary>
public sealed class ModelProfileDto
{
    /// <summary>Hard cap on gallery items — each one carries a full HTML document.</summary>
    public const int GalleryLimit = 6;

    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }

    /// <summary>Leaderboard position, 1 = best. Zero when the model is not ranked yet.</summary>
    public int Rank { get; init; }

    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }
    public int DrawCount { get; init; }

    /// <summary>Derived rather than stored, so it cannot drift from the other three.</summary>
    public int LossCount => Math.Max(0, DuelCount - WinCount - DrawCount);

    public double WinRate { get; init; }
    public double? OutputQualityAvg { get; init; }

    /// <summary>Mean tokens/second across every result this model has recorded.</summary>
    public double? AvgTokenVelocity { get; init; }

    public double? AvgApiCostPerDuel { get; init; }
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }
    public string? ApiEndpointRef { get; init; }

    /// <summary>Chronological, oldest first.</summary>
    public IReadOnlyList<EloPointDto> EloHistory { get; init; } = [];

    /// <summary>Head-to-head against every opponent this model has met.</summary>
    public IReadOnlyList<HeadToHeadDto> KillList { get; init; } = [];

    public IReadOnlyList<WinningOutputDto> WinningOutputs { get; init; } = [];
}
