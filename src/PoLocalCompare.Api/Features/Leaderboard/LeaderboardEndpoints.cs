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
        .WithSummary("Returns all models ranked by ELO, output quality or average cost.")
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

        group.MapGet("/{modelId}/profile", async (
            ModelId modelId,
            [FromServices] GetModelProfileHandler handler,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            // Tagged with the leaderboard tag rather than one of its own: every field on this
            // page moves for exactly the same reason the leaderboard does — a verdict — and
            // RecordVerdictHandler already invalidates that tag on the single path ELO moves.
            var profile = await cache.GetOrCreateAsync(
                $"model-profile:{modelId}",
                async _ => await handler.HandleAsync(modelId),
                tags: [CacheTags.Leaderboard],
                cancellationToken: cancellationToken);

            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetModelProfile")
        .WithSummary("Returns one model's standing, rating history, head-to-head record and winning outputs.")
        .Produces<ModelProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        // The /h2h/{a}/{b} endpoint and its handler are gone with the /h2h page. The kill-list
        // above already answers "how do these two compare" for every opponent at once, and it
        // does so without a second model lookup that 404s whenever either id has been retired.
        return app;
    }
}
