using Azure;
using Azure.Data.Tables;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// Development-only tooling. Gated twice on purpose: Program.cs maps these only in Development,
/// and RequireAuthorization puts them behind the same session every other write needs — they were
/// AllowAnonymous until 2026-08-23, which made an unauthenticated table wipe exactly one
/// ASPNETCORE_ENVIRONMENT slip away from live data. Locally the fake-auth header satisfies it.
/// </summary>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        // Wipes duels/results/ELO history and resets every model to 1200.
        app.MapPost("/api/dev/reset", async (TableServiceClient tsc) =>
        {
            foreach (var name in new[] { "Duels", "DuelResults", "EloHistory" })
            {
                var table = tsc.GetTableClient(name);
                await foreach (var entity in table.QueryAsync<TableEntity>())
                {
                    try { await table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey); }
                    catch (RequestFailedException ex) when (ex.Status == 404) { }
                }
            }

            var models = tsc.GetTableClient("Models");
            await foreach (var e in models.QueryAsync<TableEntity>(x => x.PartitionKey == "model"))
            {
                e["CurrentElo"] = 1200.0;
                e["DuelCount"] = 0;
                e["WinCount"] = 0;
                e["DrawCount"] = 0;
                await models.UpsertEntityAsync(e, TableUpdateMode.Replace);
            }
            return Results.Ok(new { reset = true, message = "Duels/results/elo cleared; model ELO reset to 1200" });
        }).RequireAuthorization();

        // Judge-vs-human agreement over the newest human-decided duels. Every sampled duel costs
        // real judge calls, so it is dev-only. Run before and after any judge change.
        app.MapPost("/api/dev/judge-calibration", async (JudgeCalibrationHandler handler, int? take, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(take ?? 20, ct))).RequireAuthorization();

        return app;
    }
}
