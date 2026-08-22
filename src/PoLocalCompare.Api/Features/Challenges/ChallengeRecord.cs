// GoF: Entity (immutable)
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Challenges;

/// <summary>
/// One model's attempt at one challenge budget.
/// </summary>
/// <remarks>
/// Written per side when a challenge duel is adjudicated, and partitioned by model — the same
/// shape as <see cref="EloRecord"/>, and for the same reason. The challenge leaderboard asks
/// "how often does this model come in under budget", which is a per-model question; answering it
/// by scanning duels for a stamped constraint would mean reading the entire duel history to
/// build one small table.
/// </remarks>
public sealed class ChallengeRecord
{
    public ModelId ModelId { get; init; }
    public string TimestampKey { get; init; }
    public DuelId DuelId { get; init; }

    public ChallengeKind Kind { get; init; }
    public double Threshold { get; init; }

    /// <summary>What this model actually measured. Null when it produced no usable run.</summary>
    public double? Measured { get; init; }

    /// <summary>Whether <see cref="Measured"/> came in at or under <see cref="Threshold"/>.</summary>
    public bool Met { get; init; }

    /// <summary>Whether this model won the duel, however it was decided.</summary>
    public bool Won { get; init; }

    public ModelId OpponentModelId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }

    public ChallengeRecord(
        ModelId modelId,
        DuelId duelId,
        ChallengeKind kind,
        double threshold,
        double? measured,
        bool met,
        bool won,
        ModelId opponentModelId)
    {
        ModelId = modelId;
        DuelId = duelId;
        Kind = kind;
        Threshold = threshold;
        Measured = measured;
        Met = met;
        Won = won;
        OpponentModelId = opponentModelId;
        RecordedAt = DateTimeOffset.UtcNow;
        // Inverted ticks then the duel id: descending time order from a plain partition scan,
        // and unique even when two challenges are adjudicated within the same tick.
        TimestampKey = $"{long.MaxValue - RecordedAt.Ticks:D19}_{duelId}";
    }

    /// <summary>Parameterless constructor for Azure Table Storage deserialization.</summary>
    public ChallengeRecord()
    {
        TimestampKey = string.Empty;
    }
}
