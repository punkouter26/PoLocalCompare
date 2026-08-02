using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

public static class LeaderboardEndpoints
{
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
                tags: [CacheTags.Leaderboard],
                cancellationToken: cancellationToken);
            return Results.Ok(rows);
        })
        .WithName("GetLeaderboard")
        .WithSummary("Returns all models ranked by ELO or Green Score.")
        .Produces<IReadOnlyList<LeaderboardEntryDto>>();

        group.MapGet("/{modelId}/killlist", async (
            ModelId modelId,
            [FromServices] GetKillListHandler handler) =>
        {
            var rows = await handler.HandleAsync(modelId);
            return Results.Ok(rows);
        })
        .WithName("GetKillList")
        .WithSummary("Returns aggregated head-to-head records for a model.")
        .Produces<IReadOnlyList<HeadToHeadDto>>();

        group.MapGet("/h2h/{modelIdA}/{modelIdB}", async (
            ModelId modelIdA,
            ModelId modelIdB,
            [FromServices] GetHeadToHeadHandler handler) =>
        {
            var detail = await handler.HandleAsync(modelIdA, modelIdB);
            return detail is null
                ? Results.NotFound(new { error = "One or both models were not found, or they are the same model." })
                : Results.Ok(detail);
        })
        .WithName("GetHeadToHead")
        .WithSummary("Returns the full record between two specific models.")
        .Produces<HeadToHeadDetailDto>()
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
