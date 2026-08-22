// GoF: Repository pattern
using System.Collections.Concurrent;
using Azure.Data.Tables;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Challenges;

public interface IChallengeRecordRepository
{
    Task SaveAsync(ChallengeRecord record);
    Task<IEnumerable<ChallengeRecord>> GetAllByModelAsync(ModelId modelId);
}

public sealed class ChallengeRecordRepository : IChallengeRecordRepository
{
    private const string TableName = "ChallengeRecords";

    /// <inheritdoc cref="DuelRepository"/>
    private static readonly ConcurrentDictionary<string, Task> TableEnsured = new();

    private readonly TableClient _tableClient;

    public ChallengeRecordRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    private Task EnsureTableAsync() =>
        TableEnsured.GetOrAdd(_tableClient.Uri.ToString(), _ => _tableClient.CreateIfNotExistsAsync());

    public async Task SaveAsync(ChallengeRecord record)
    {
        await EnsureTableAsync();

        try
        {
            await _tableClient.AddEntityAsync(MapToEntity(record));
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent append (standards §5.5): this attempt already landed. Matters here
            // because a re-queued adjudication would otherwise double-count one attempt.
        }
    }

    public async Task<IEnumerable<ChallengeRecord>> GetAllByModelAsync(ModelId modelId)
    {
        await EnsureTableAsync();

        var records = new List<ChallengeRecord>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {modelId.Value}")))
        {
            records.Add(MapToRecord(entity));
        }

        return records;
    }

    private static TableEntity MapToEntity(ChallengeRecord record) =>
        new(record.ModelId, record.TimestampKey)
        {
            ["DuelId"] = record.DuelId.Value,
            ["Kind"] = record.Kind.ToString(),
            ["Threshold"] = record.Threshold,
            ["Measured"] = record.Measured,
            ["Met"] = record.Met,
            ["Won"] = record.Won,
            ["OpponentModelId"] = record.OpponentModelId.Value,
            ["RecordedAt"] = record.RecordedAt,
        };

    private static ChallengeRecord MapToRecord(TableEntity entity) =>
        new()
        {
            ModelId = ModelId.FromOrDefault(entity.PartitionKey),
            TimestampKey = entity.RowKey,
            DuelId = DuelId.FromOrDefault(entity.GetString("DuelId")),
            Kind = Enum.TryParse<ChallengeKind>(entity.GetString("Kind"), out var kind) ? kind : ChallengeKind.None,
            Threshold = entity.GetDouble("Threshold") ?? 0,
            Measured = entity.GetDouble("Measured"),
            Met = entity.GetBoolean("Met") ?? false,
            Won = entity.GetBoolean("Won") ?? false,
            OpponentModelId = ModelId.FromOrDefault(entity.GetString("OpponentModelId")),
            RecordedAt = entity.GetDateTimeOffset("RecordedAt") ?? DateTimeOffset.MinValue,
        };
}
