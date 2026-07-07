using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Application.Leaderboard.GetLeaderboard;
using PoLocalCompare.Application.Leaderboard.GetKillList;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Endpoints;

public static class LeaderboardEndpoints
{
    /// <summary>Cache tag invalidated whenever a verdict updates ELO (see DuelsEndpoints verdict handler).</summary>
    internal const string LeaderboardCacheTag = "leaderboard";

    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leaderboard").WithTags("Leaderboard").RequireAuthorization();

        group.MapGet("/", async (
            [FromQuery] string? sortBy,
            [FromServices] GetLeaderboardHandler handler,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            var sort = sortBy ?? "Elo";
            var rows = await cache.GetOrCreateAsync(
                $"leaderboard:{sort}",
                async ct => (await handler.HandleAsync(sort)).ToArray(),
                tags: [LeaderboardCacheTag],
                cancellationToken: cancellationToken);
            return Results.Ok(rows);
        })
        .WithName("GetLeaderboard")
        .WithSummary("Returns all models ranked by ELO or Green Score.")
        .Produces<IReadOnlyList<LeaderboardEntryDto>>();

        group.MapGet("/{modelId}/killlist", async (
            string modelId,
            [FromServices] GetKillListHandler handler) =>
        {
            var rows = await handler.HandleAsync(modelId);
            return Results.Ok(rows);
        })
        .WithName("GetKillList")
        .WithSummary("Returns aggregated head-to-head records for a model.")
        .Produces<IReadOnlyList<HeadToHeadDto>>();

        return app;
    }
}
