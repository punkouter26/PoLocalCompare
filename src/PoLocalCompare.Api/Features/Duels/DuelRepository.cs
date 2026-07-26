// GoF: Repository pattern
using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
using NUlid;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelRepository : IDuelRepository
{
    private const string TableName = "Duels";

    /// <summary>
    /// One create-check per table endpoint for the life of the process. The table is already
    /// provisioned at startup (AzuriteSetup in dev, the storage bootstrap in Production); doing
    /// it per operation cost an extra round-trip on every duel read, write and list.
    /// Keyed by endpoint so a test host pointed at a different Azurite instance still ensures its own.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task> TableEnsured = new();

    private readonly TableClient _tableClient;

    public DuelRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    private Task EnsureTableAsync() =>
        TableEnsured.GetOrAdd(_tableClient.Uri.ToString(), _ => _tableClient.CreateIfNotExistsAsync());

    public async Task<Duel?> GetByIdAsync(string duelId)
    {
        await EnsureTableAsync();

        // PartitionKey is YYYYMM derived from ULID timestamp
        var partitionKey = GetPartitionKey(duelId);
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(partitionKey, duelId);
            return MapToDuel(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // If the ULID-derived partition key doesn't work, search all partitions
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(e => e.RowKey == duelId))
            {
                return MapToDuel(entity);
            }
            return null;
        }
    }

    public async Task SaveAsync(Duel duel)
    {
        await EnsureTableAsync();

        var entity = MapToEntity(duel);
        try
        {
            await _tableClient.AddEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent create (standards §5.5): the duel already exists, e.g. a retried request.
        }
    }

    public async Task UpdateAsync(Duel duel)
    {
        await EnsureTableAsync();

        var entity = MapToEntity(duel);
        // ETag-conditional replace (standards §5.5): a concurrent writer surfaces as 412 instead of a lost update.
        var etag = string.IsNullOrEmpty(duel.ETag) ? ETag.All : new ETag(duel.ETag);
        await _tableClient.UpdateEntityAsync(entity, etag, TableUpdateMode.Replace);
    }

    public async Task<IEnumerable<Duel>> ListAsync(int limit, string? beforeMonth)
    {
        await EnsureTableAsync();

        limit = Math.Clamp(limit, 1, 100);
        var duels = new List<Duel>();

        // Query partitions from most recent month backward
        var startMonth = beforeMonth ?? DateTimeOffset.UtcNow.ToString("yyyyMM");

        // Azure Table Storage doesn't support cross-partition ordering, so fetch per-partition
        // and aggregate in memory for this small-scale tool
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: string.IsNullOrEmpty(beforeMonth)
                ? null
                : $"PartitionKey le '{beforeMonth}'",
            maxPerPage: limit))
        {
            duels.Add(MapToDuel(entity));
            if (duels.Count >= limit) break;
        }

        // Sort newest first (ULID RowKey is lexicographically time-ordered)
        return duels.OrderByDescending(d => d.DuelId).Take(limit);
    }

    private static string GetPartitionKey(string duelId)
    {
        try
        {
            var ulid = Ulid.Parse(duelId);
            return ulid.Time.ToString("yyyyMM");
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToString("yyyyMM");
        }
    }

    private TableEntity MapToEntity(Duel duel)
    {
        var partitionKey = GetPartitionKey(duel.DuelId);
        var entity = new TableEntity(partitionKey, duel.DuelId)
        {
            ["PromptText"] = duel.PromptText,
            ["PromptFull"] = duel.PromptFull,
            ["LeftModelId"] = duel.LeftModelId,
            ["RightModelId"] = duel.RightModelId,
            ["StartedAt"] = duel.StartedAt,
            ["CompletedAt"] = duel.CompletedAt,
            ["Verdict"] = duel.Verdict.ToString(),
            ["WinnerModelId"] = duel.WinnerModelId,
            ["LoserModelId"] = duel.LoserModelId,
            ["EloShiftWinner"] = duel.EloShiftWinner,
            ["EloShiftLoser"] = duel.EloShiftLoser,
            ["VerdictDeadline"] = duel.VerdictDeadline,
            ["IsPartial"] = duel.IsPartial,
            ["VerdictSource"] = duel.VerdictSource.ToString(),
            ["JudgeRationale"] = duel.JudgeRationale,
            ["JudgeModel"] = duel.JudgeModel,
        };
        return entity;
    }

    private static Duel MapToDuel(TableEntity entity)
    {
        var duel = new Duel
        {
            DuelId = entity.RowKey,
            PromptText = entity.GetString("PromptText") ?? string.Empty,
            PromptFull = entity.GetString("PromptFull") ?? string.Empty,
            LeftModelId = entity.GetString("LeftModelId") ?? string.Empty,
            RightModelId = entity.GetString("RightModelId") ?? string.Empty,
            StartedAt = entity.GetDateTimeOffset("StartedAt") ?? DateTimeOffset.MinValue,
            CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
            Verdict = Enum.TryParse<DuelVerdict>(entity.GetString("Verdict"), out var v) ? v : DuelVerdict.Pending,
            WinnerModelId = entity.GetString("WinnerModelId"),
            LoserModelId = entity.GetString("LoserModelId"),
            EloShiftWinner = entity.GetDouble("EloShiftWinner"),
            EloShiftLoser = entity.GetDouble("EloShiftLoser"),
            VerdictDeadline = entity.GetDateTimeOffset("VerdictDeadline") ?? DateTimeOffset.MinValue,
            IsPartial = entity.GetBoolean("IsPartial") ?? false,
            // Rows written before the auto-judge existed have no VerdictSource — they were
            // all human decisions, which is what the Human fallback says.
            VerdictSource = Enum.TryParse<VerdictSource>(entity.GetString("VerdictSource"), out var vs)
                ? vs
                : VerdictSource.Human,
            JudgeRationale = entity.GetString("JudgeRationale"),
            JudgeModel = entity.GetString("JudgeModel"),
            ETag = entity.ETag.ToString(),
        };
        return duel;
    }
}