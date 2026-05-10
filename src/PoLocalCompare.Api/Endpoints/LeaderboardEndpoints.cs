using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Application.Leaderboard.GetKillList;
using PoLocalCompare.Application.Leaderboard.GetLeaderboard;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Endpoints;

public static class LeaderboardEndpoints
{
    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leaderboard").WithTags("Leaderboard");

        group.MapGet("/", async (
            [FromQuery] string? sortBy,
            [FromServices] GetLeaderboardHandler handler) =>
        {
            var rows = await handler.HandleAsync(new GetLeaderboardQuery(sortBy ?? "Elo"));
            return Results.Ok(rows);
        })
        .WithName("GetLeaderboard")
        .WithSummary("Returns all models ranked by ELO or Green Score.")
        .Produces<IReadOnlyList<LeaderboardEntryDto>>();

        group.MapGet("/{modelId}/killlist", async (
            string modelId,
            [FromServices] GetKillListHandler handler) =>
        {
            var rows = await handler.HandleAsync(new GetKillListQuery(modelId));
            return Results.Ok(rows);
        })
        .WithName("GetKillList")
        .WithSummary("Returns aggregated head-to-head records for a model.")
        .Produces<IReadOnlyList<HeadToHeadDto>>();

        return app;
    }
}
