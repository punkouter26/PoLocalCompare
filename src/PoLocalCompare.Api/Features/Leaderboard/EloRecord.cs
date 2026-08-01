// GoF: Entity (immutable)
namespace PoLocalCompare.Api.Features.Leaderboard;

public sealed class EloRecord
{
    public ModelId ModelId { get; init; }
    public string TimestampKey { get; init; }
    public DuelId DuelId { get; init; }
    public double EloAfter { get; init; }
    public double EloBefore { get; init; }
    public double EloShift { get; init; }
    public string Outcome { get; init; }
    public ModelId OpponentModelId { get; init; }
    public double OpponentEloBefore { get; init; }
    public DateTimeOffset RecordedAt { get; init; }

    public EloRecord(
        ModelId modelId,
        DuelId duelId,
        double eloAfter,
        double eloBefore,
        string outcome,
        ModelId opponentModelId,
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
        TimestampKey = string.Empty;
        Outcome = string.Empty;
    }
}
