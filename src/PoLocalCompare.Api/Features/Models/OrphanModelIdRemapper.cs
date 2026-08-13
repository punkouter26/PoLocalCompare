using Azure;
using Azure.Data.Tables;

namespace PoLocalCompare.Api.Features.Models;

internal static partial class RemapLog
{
    [LoggerMessage(EventId = 1310, Level = LogLevel.Information,
        Message = "Model-id remap: {OrphanId} (\"{DisplayName}\") → {CanonicalId}.")]
    public static partial void Mapped(ILogger logger, string orphanId, string displayName, string canonicalId);

    [LoggerMessage(EventId = 1311, Level = LogLevel.Warning,
        Message = "Model-id remap: {OrphanId} (\"{DisplayName}\") has no catalog entry with that name; its duels are left as they are.")]
    public static partial void Unmatched(ILogger logger, string orphanId, string displayName);

    [LoggerMessage(EventId = 1312, Level = LogLevel.Information,
        Message = "Model-id remap complete: {Duels} duels, {Results} results, {History} history rows rewritten across {Models} ids; {Recomputed} model aggregates recomputed.")]
    public static partial void Done(ILogger logger, int duels, int results, int history, int models, int recomputed);
}

/// <summary>
/// Repoints duel history at the current model catalog.
/// </summary>
/// <remarks>
/// The catalog has been re-keyed at least once — retired seed ids, and API-registered models
/// that came back under fresh ULIDs after a storage wipe. Duels, results and ELO history all
/// store the id that was current when they were written, so after a re-key they point at
/// models that no longer exist. What that looked like: the kill list rendered every opponent
/// as "Retired model", the Archive showed two different ids under one display name as
/// "Phi-4 vs Phi-4", and a model's rating was split across two identities.
///
/// Orphans are matched to the catalog by the display-name snapshot each duel already stores,
/// which is why <c>Duel.LeftModelName</c>/<c>RightModelName</c> exist. An orphan whose name
/// matches nothing is left untouched and logged rather than guessed at.
///
/// Idempotent: a second run finds no orphans and does nothing. ELO history moves partition
/// (its partition key *is* the model id), so those rows are re-inserted then deleted — in that
/// order, so an interrupted run duplicates a row rather than losing one.
/// </remarks>
public sealed class OrphanModelIdRemapper(TableServiceClient tableServiceClient, ILogger<OrphanModelIdRemapper> logger)
{
    public sealed record Report(
        int OrphanIdsFound,
        int OrphanIdsMapped,
        int DuelRowsRewritten,
        int ResultRowsRewritten,
        int HistoryRowsRewritten,
        int ModelsRecomputed,
        IReadOnlyList<string> Unmatched);

    public async Task<Report> RunAsync(CancellationToken cancellationToken = default)
    {
        var duels = tableServiceClient.GetTableClient("Duels");
        var results = tableServiceClient.GetTableClient("DuelResults");
        var history = tableServiceClient.GetTableClient("EloHistory");
        var models = tableServiceClient.GetTableClient("Models");

        foreach (var table in new[] { duels, results, history, models })
            await table.CreateIfNotExistsAsync(cancellationToken);

        // ── Catalog: display name → current id ────────────────────────────────
        var canonicalByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entity in models.QueryAsync<TableEntity>(x => x.PartitionKey == "model", cancellationToken: cancellationToken))
        {
            catalogIds.Add(entity.RowKey);
            var name = entity.GetString("DisplayName");
            // First writer wins: if two catalog rows share a display name the mapping is
            // ambiguous, and picking arbitrarily on every run would make this non-deterministic.
            if (!string.IsNullOrWhiteSpace(name))
                canonicalByName.TryAdd(name.Trim(), entity.RowKey);
        }

        // ── Find orphans, naming them from the snapshot the duel already carries ──
        var duelRows = new List<TableEntity>();
        await foreach (var entity in duels.QueryAsync<TableEntity>(cancellationToken: cancellationToken))
            duelRows.Add(entity);

        var orphanNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var duel in duelRows)
        {
            Note(duel.GetString("LeftModelId"), duel.GetString("LeftModelName"));
            Note(duel.GetString("RightModelId"), duel.GetString("RightModelName"));
        }

        void Note(string? id, string? snapshotName)
        {
            if (string.IsNullOrWhiteSpace(id) || catalogIds.Contains(id)) return;
            // A snapshot equal to the id is the "unresolved" sentinel, not a name.
            if (string.IsNullOrWhiteSpace(snapshotName) || string.Equals(snapshotName.Trim(), id, StringComparison.OrdinalIgnoreCase))
                return;
            orphanNames.TryAdd(id, snapshotName.Trim());
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmatched = new List<string>();
        foreach (var (orphanId, name) in orphanNames)
        {
            if (canonicalByName.TryGetValue(name, out var canonicalId) && canonicalId != orphanId)
            {
                map[orphanId] = canonicalId;
                RemapLog.Mapped(logger, orphanId, name, canonicalId);
            }
            else if (!canonicalByName.ContainsKey(name))
            {
                unmatched.Add($"{orphanId} (\"{name}\")");
                RemapLog.Unmatched(logger, orphanId, name);
            }
        }

        if (map.Count == 0)
        {
            RemapLog.Done(logger, 0, 0, 0, 0, 0);
            return new Report(orphanNames.Count, 0, 0, 0, 0, 0, unmatched);
        }

        string? Mapped(string? id) => id is not null && map.TryGetValue(id, out var to) ? to : null;

        // ── Duels: four id columns, same row ──────────────────────────────────
        var duelsRewritten = 0;
        foreach (var duel in duelRows)
        {
            var changed = false;
            foreach (var column in new[] { "LeftModelId", "RightModelId", "WinnerModelId", "LoserModelId" })
            {
                if (Mapped(duel.GetString(column)) is not { } to) continue;
                duel[column] = to;
                changed = true;
            }

            if (!changed) continue;
            await duels.UpdateEntityAsync(duel, duel.ETag, TableUpdateMode.Replace, cancellationToken);
            duelsRewritten++;
        }

        // ── Duel results: the model id is the row key, so these are move-and-delete ──
        var resultsRewritten = 0;
        var resultRows = new List<TableEntity>();
        await foreach (var entity in results.QueryAsync<TableEntity>(cancellationToken: cancellationToken))
            resultRows.Add(entity);

        foreach (var row in resultRows)
        {
            if (Mapped(row.RowKey) is not { } to) continue;

            var moved = new TableEntity(row) { PartitionKey = row.PartitionKey, RowKey = to };
            moved.ETag = default;
            await results.UpsertEntityAsync(moved, TableUpdateMode.Replace, cancellationToken);
            await DeleteQuietlyAsync(results, row.PartitionKey, row.RowKey, cancellationToken);
            resultsRewritten++;
        }

        // ── ELO history: partition key is the model id; opponent id is a column ──
        var historyRewritten = 0;
        var historyRows = new List<TableEntity>();
        await foreach (var entity in history.QueryAsync<TableEntity>(cancellationToken: cancellationToken))
            historyRows.Add(entity);

        foreach (var row in historyRows)
        {
            var newPartition = Mapped(row.PartitionKey);
            var newOpponent = Mapped(row.GetString("OpponentModelId"));
            if (newPartition is null && newOpponent is null) continue;

            var moved = new TableEntity(row)
            {
                PartitionKey = newPartition ?? row.PartitionKey,
                RowKey = row.RowKey,
            };
            moved.ETag = default;
            if (newOpponent is not null) moved["OpponentModelId"] = newOpponent;

            // Insert first, delete second: an interrupted run leaves a duplicate the next run
            // overwrites, rather than a hole no run can recover.
            await history.UpsertEntityAsync(moved, TableUpdateMode.Replace, cancellationToken);
            if (newPartition is not null)
                await DeleteQuietlyAsync(history, row.PartitionKey, row.RowKey, cancellationToken);
            historyRewritten++;
        }

        var recomputed = await RecomputeModelAggregatesAsync(models, history, cancellationToken);

        RemapLog.Done(logger, duelsRewritten, resultsRewritten, historyRewritten, map.Count, recomputed);
        return new Report(orphanNames.Count, map.Count, duelsRewritten, resultsRewritten, historyRewritten, recomputed, unmatched);
    }

    /// <summary>
    /// Rebuilds each model's rating and record from its (now correctly attributed) history.
    /// </summary>
    /// <remarks>
    /// Remapping moves history rows onto the canonical model, but that model's own
    /// <c>CurrentElo</c>, <c>DuelCount</c>, <c>WinCount</c> and <c>DrawCount</c> were only ever
    /// incremented for duels it was credited with at the time. Leaving them alone would show a
    /// model with twelve inherited duels and a record of 0/0.
    /// </remarks>
    private static async Task<int> RecomputeModelAggregatesAsync(
        TableClient models,
        TableClient history,
        CancellationToken cancellationToken)
    {
        var recomputed = 0;
        await foreach (var model in models.QueryAsync<TableEntity>(x => x.PartitionKey == "model", cancellationToken: cancellationToken))
        {
            EloRecordTally tally = default;
            await foreach (var row in history.QueryAsync<TableEntity>(
                filter: TableClient.CreateQueryFilter($"PartitionKey eq {model.RowKey}"),
                cancellationToken: cancellationToken))
            {
                tally.Duels++;
                switch (row.GetString("Outcome"))
                {
                    case "Win": tally.Wins++; break;
                    case "Draw": tally.Draws++; break;
                }

                // Latest row wins; RowKey is inverted ticks, so "smallest" is newest.
                if (tally.NewestKey is null || string.CompareOrdinal(row.RowKey, tally.NewestKey) < 0)
                {
                    tally.NewestKey = row.RowKey;
                    tally.Elo = row.GetDouble("EloAfter") ?? 1200;
                }
            }

            if (tally.Duels == 0) continue;

            model["CurrentElo"] = tally.Elo;
            model["DuelCount"] = tally.Duels;
            model["WinCount"] = tally.Wins;
            model["DrawCount"] = tally.Draws;
            await models.UpdateEntityAsync(model, model.ETag, TableUpdateMode.Replace, cancellationToken);
            recomputed++;
        }

        return recomputed;
    }

    private struct EloRecordTally
    {
        public int Duels;
        public int Wins;
        public int Draws;
        public double Elo;
        public string? NewestKey;
    }

    private static async Task DeleteQuietlyAsync(
        TableClient table,
        string partitionKey,
        string rowKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await table.DeleteEntityAsync(partitionKey, rowKey, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already gone — a previous run got this far.
        }
    }
}
