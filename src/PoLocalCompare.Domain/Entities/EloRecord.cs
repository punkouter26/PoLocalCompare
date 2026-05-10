// GoF: Entity (immutable)
namespace PoLocalCompare.Domain.Entities;

public sealed class EloRecord
{
    public string ModelId { get; init; }
    public string TimestampKey { get; init; }
    public string DuelId { get; init; }
    public double EloAfter { get; init; }
    public double EloBefore { get; init; }
    public double EloShift { get; init; }
    public string Outcome { get; init; }
    public string OpponentModelId { get; init; }
    public double OpponentEloBefore { get; init; }
    public DateTimeOffset RecordedAt { get; init; }

    public EloRecord(
        string modelId,
        string duelId,
        double eloAfter,
        double eloBefore,
        string outcome,
        string opponentModelId,
        double opponentEloBefore)
    {
        ModelId = modelId;
        DuelId = duelId;
        EloAfter = eloAfter;
        EloBefore = eloBefore;
        EloShift = eloAfter - eloBefore;
        Outcome = outcome;
        OpponentModelId = opponentModelId;
        OpponentEloBefore = opponentEloBefore;
        RecordedAt = DateTimeOffset.UtcNow;
        // RowKey: invertedTicks_DuelId gives descending time order
        TimestampKey = $"{long.MaxValue - RecordedAt.Ticks:D19}_{duelId}";
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public EloRecord()
    {
        ModelId = string.Empty;
        TimestampKey = string.Empty;
        DuelId = string.Empty;
        Outcome = string.Empty;
        OpponentModelId = string.Empty;
    }
}
