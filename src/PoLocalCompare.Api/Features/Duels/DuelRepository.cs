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

    public async Task<Duel?> GetByIdAsync(DuelId duelId)
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
        await _tableClient.UpdateEntityAsync(entity, TableETag.Parse(duel.ETag), TableUpdateMode.Replace);
    }

    public async Task<IEnumerable<Duel>> ListAsync(int limit, string? beforeMonth)
    {
        await EnsureTableAsync();

        limit = Math.Clamp(limit, 1, 100);
        var duels = new List<Duel>();

        // Azure Table Storage doesn't honour cross-partition ordering, and the SDK returns
        // whatever the first partition yields when `maxPerPage` matches the requested limit —
        // so a `limit=20` query previously returned 20 rows from a single partition and never
        // touched the month that actually held the newest duel. Fetch a generous upper bound,
        // sort in memory by the timestamp the page actually renders, and take the top `limit`.
        // The cap is still small (a duel row is ~1 KB), so the memory cost is negligible.
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: string.IsNullOrEmpty(beforeMonth)
                ? null
                : TableClient.CreateQueryFilter($"PartitionKey le {beforeMonth}"),
            maxPerPage: 1000))
        {
            duels.Add(MapToDuel(entity));
        }

        // Sort newest first by the timestamp the page renders. The ULID lexicographic order
        // would also work, but `CompletedAt` is what the Archive shows and people occasionally
        // back-date a record, so displaying the same sort the UI uses keeps page and API in
        // lockstep even when the implementation drifts.
        return duels
            .OrderByDescending(d => d.CompletedAt ?? d.StartedAt)
            .Take(limit);
    }

    private static string GetPartitionKey(DuelId duelId)
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
            ["LeftModelId"] = duel.LeftModelId.Value,
            ["RightModelId"] = duel.RightModelId.Value,
            ["LeftModelName"] = duel.LeftModelName,
            ["RightModelName"] = duel.RightModelName,
            ["StartedAt"] = duel.StartedAt,
            ["CompletedAt"] = duel.CompletedAt,
            ["Verdict"] = duel.Verdict.ToString(),
            ["WinnerModelId"] = duel.WinnerModelId?.Value,
            ["LoserModelId"] = duel.LoserModelId?.Value,
            ["EloShiftWinner"] = duel.EloShiftWinner,
            ["EloShiftLoser"] = duel.EloShiftLoser,
            ["VerdictDeadline"] = duel.VerdictDeadline,
            ["IsPartial"] = duel.IsPartial,
            ["VerdictSource"] = duel.VerdictSource.ToString(),
            ["JudgeRationale"] = duel.JudgeRationale,
            ["JudgeModel"] = duel.JudgeModel,
            ["JudgeStoodDownReason"] = duel.JudgeStoodDownReason,
            ["OwnerId"] = duel.OwnerId,
            ["VerdictBy"] = duel.VerdictBy,
        };
        return entity;
    }

    private static Duel MapToDuel(TableEntity entity)
    {
        var duel = new Duel
        {
            DuelId = DuelId.FromOrDefault(entity.RowKey),
            PromptText = entity.GetString("PromptText") ?? string.Empty,
            PromptFull = entity.GetString("PromptFull") ?? string.Empty,
            LeftModelId = ModelId.FromOrDefault(entity.GetString("LeftModelId")),
            RightModelId = ModelId.FromOrDefault(entity.GetString("RightModelId")),
            // Null on rows written before the snapshot existed; readers fall back to the
            // live catalog and then to a neutral label, so old rows are no worse than before.
            LeftModelName = entity.GetString("LeftModelName"),
            RightModelName = entity.GetString("RightModelName"),
            StartedAt = entity.GetDateTimeOffset("StartedAt") ?? DateTimeOffset.MinValue,
            CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
            Verdict = Enum.TryParse<DuelVerdict>(entity.GetString("Verdict"), out var v) ? v : DuelVerdict.Pending,
            WinnerModelId = ModelId.FromOrNull(entity.GetString("WinnerModelId")),
            LoserModelId = ModelId.FromOrNull(entity.GetString("LoserModelId")),
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
            JudgeStoodDownReason = entity.GetString("JudgeStoodDownReason"),
            // Both nullable so rows written before the schema addition still deserialise.
            OwnerId = entity.GetString("OwnerId"),
            VerdictBy = entity.GetString("VerdictBy"),
            ETag = entity.ETag.ToString(),
        };
        return duel;
    }
}