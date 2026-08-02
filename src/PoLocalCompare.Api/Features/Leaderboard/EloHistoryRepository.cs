// GoF: Repository pattern
using Azure.Data.Tables;

namespace PoLocalCompare.Api.Features.Leaderboard;

public sealed class EloHistoryRepository : IEloHistoryRepository
{
    private const string TableName = "EloHistory";

    private readonly TableClient _tableClient;

    public EloHistoryRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    public async Task SaveAsync(EloRecord record)
    {
        var entity = MapToEntity(record);
        try
        {
            await _tableClient.AddEntityAsync(entity);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent append (standards §5.5): the history point already landed.
        }
    }

    /// <summary>Returns the most recent 20 ELO records for sparkline display (inverted-tick RowKey gives descending order).</summary>
    public async Task<IEnumerable<EloRecord>> GetLast20Async(ModelId modelId)
    {
        var records = new List<EloRecord>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {modelId.Value}"),
            maxPerPage: 20))
        {
            records.Add(MapToRecord(entity));
            if (records.Count >= 20) break;
        }
        // Records come in inverted-tick (descending) order; reverse for chronological sparkline
        records.Reverse();
        return records;
    }

    /// <summary>Returns all ELO records for a model (full partition scan, used by Kill List aggregation).</summary>
    public async Task<IEnumerable<EloRecord>> GetAllByModelAsync(ModelId modelId)
    {
        var records = new List<EloRecord>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {modelId.Value}")))
        {
            records.Add(MapToRecord(entity));
        }
        return records;
    }

    private static TableEntity MapToEntity(EloRecord record)
    {
        return new TableEntity(record.ModelId, record.TimestampKey)
        {
            ["DuelId"] = record.DuelId.Value,
            ["EloAfter"] = record.EloAfter,
            ["EloBefore"] = record.EloBefore,
            ["EloShift"] = record.EloShift,
            ["Outcome"] = record.Outcome,
            ["OpponentModelId"] = record.OpponentModelId.Value,
            ["OpponentEloBefore"] = record.OpponentEloBefore,
            ["RecordedAt"] = record.RecordedAt
        };
    }

    private static EloRecord MapToRecord(TableEntity entity)
    {
        return new EloRecord
        {
            ModelId = ModelId.FromOrDefault(entity.PartitionKey),
            TimestampKey = entity.RowKey,
            DuelId = DuelId.FromOrDefault(entity.GetString("DuelId")),
            EloAfter = entity.GetDouble("EloAfter") ?? 0,
            EloBefore = entity.GetDouble("EloBefore") ?? 0,
            EloShift = entity.GetDouble("EloShift") ?? 0,
            Outcome = entity.GetString("Outcome") ?? string.Empty,
            OpponentModelId = ModelId.FromOrDefault(entity.GetString("OpponentModelId")),
            OpponentEloBefore = entity.GetDouble("OpponentEloBefore") ?? 0,
            RecordedAt = entity.GetDateTimeOffset("RecordedAt") ?? DateTimeOffset.MinValue
        };
    }
}
