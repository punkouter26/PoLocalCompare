using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>One past meeting between the pair, summarised for the head-to-head timeline.</summary>
public sealed class HeadToHeadDuelDto
{
    public DuelId DuelId { get; init; }
    public string PromptSummary { get; init; } = string.Empty;
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>Null when the duel is still unjudged.</summary>
    public ModelId? WinnerModelId { get; init; }
    public VerdictSource? Source { get; init; }
    public double? EloShiftWinner { get; init; }

    public double? TokenVelocityA { get; init; }
    public double? TokenVelocityB { get; init; }
    public int? QualityA { get; init; }
    public int? QualityB { get; init; }

    /// <summary>Tokens per watt-hour. Only ever present for models whose TDP is known.</summary>
    public double? GreenScoreA { get; init; }
    public double? GreenScoreB { get; init; }
}

/// <summary>Aggregate performance for one side of a head-to-head, over the sampled duels only.</summary>
public sealed class HeadToHeadSideDto
{
    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }

    /// <summary>Wins against this specific opponent, not overall.</summary>
    public int Wins { get; init; }

    /// <summary>Mean rating change per meeting with this opponent; negative when losing the matchup.</summary>
    public double AvgEloShift { get; init; }

    public double? AvgTokenVelocity { get; init; }
    public double? AvgQuality { get; init; }
    public double? AvgGreenScore { get; init; }

    /// <summary>Recent overall rating trend — the same series the leaderboard sparkline uses.</summary>
    public double[]? EloSparkline { get; init; }
}

/// <summary>
/// The full record between two specific models.
/// </summary>
/// <remarks>
/// Aggregates are computed over <see cref="RecentDuels"/> rather than over the whole history:
/// the win/loss record comes from the complete ELO history, but per-duel telemetry lives in a
/// partition per duel, so averaging every meeting ever would mean an unbounded fan-out. The
/// sample size is reported so the numbers are not read as lifetime figures.
/// </remarks>
public sealed class HeadToHeadDetailDto
{
    public HeadToHeadSideDto A { get; init; } = new();
    public HeadToHeadSideDto B { get; init; } = new();

    /// <summary>Judged meetings found in the ELO history.</summary>
    public int TotalDuels { get; init; }

    /// <summary>How many meetings the telemetry averages were computed from.</summary>
    public int SampledDuels { get; init; }

    public DateTimeOffset? LastDuelAt { get; init; }

    public IReadOnlyList<HeadToHeadDuelDto> RecentDuels { get; init; } = [];

    public bool HasMet => TotalDuels > 0;
}
