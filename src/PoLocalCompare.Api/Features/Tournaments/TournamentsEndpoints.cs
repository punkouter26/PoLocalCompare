using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Api.Auth;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Tournaments;

public static class TournamentsEndpoints
{
    /// <param name="allowAnonymousWrites">
    /// Mirrors the duels slice: creating a tournament creates duels, so it sits behind the same
    /// gate rather than a looser one of its own.
    /// </param>
    public static IEndpointRouteBuilder MapTournamentsEndpoints(
        this IEndpointRouteBuilder app,
        bool allowAnonymousWrites)
    {
        var group = app.MapGroup("/api/tournaments").WithTags("Tournaments").RequireAuthorization();

        static RouteHandlerBuilder OpenIf(bool allow, RouteHandlerBuilder builder) =>
            allow ? builder.AllowAnonymous() : builder;

        OpenIf(allowAnonymousWrites, group.MapPost("/", async (
            [FromBody] CreateTournamentRequest request,
            HttpContext httpContext,
            [FromServices] CreateTournamentHandler handler,
            [FromServices] TournamentRunner runner,
            [FromServices] IBackgroundTaskQueue taskQueue) =>
        {
            try
            {
                var actor = IdentityResolver.ResolveActor(httpContext.User);

                var dto = await handler.HandleAsync(
                    request.ModelIds ?? [],
                    request.PromptText ?? string.Empty,
                    actor);

                // Queued rather than awaited: a bracket is up to seven duels and takes minutes.
                // The response carries the drawn bracket so the page can render it immediately
                // and then watch it fill in.
                taskQueue.QueueBackgroundWork(ct => runner.RunAsync(dto.TournamentId, ct));

                return Results.Created($"/api/tournaments/{dto.TournamentId}", dto);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [ex.ParamName ?? "request"] = [ex.Message],
                });
            }
        }))
        .WithName("CreateTournament")
        .WithSummary("Draws a seeded bracket and starts running it.")
        .Produces<TournamentDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        group.MapGet("/entrants", async ([FromServices] CreateTournamentHandler handler) =>
            Results.Ok(await handler.ListEntrantsAsync()))
        .WithName("ListTournamentEntrants")
        .WithSummary("Lists the models eligible to enter a bracket, strongest first.")
        .Produces<IReadOnlyList<TournamentEntrantDto>>();

        group.MapGet("/{tournamentId}", async (
            TournamentId tournamentId,
            [FromServices] ITournamentRepository repository) =>
        {
            var tournament = await repository.GetByIdAsync(tournamentId);
            return tournament is null ? Results.NotFound() : Results.Ok(tournament.ToDto());
        })
        .WithName("GetTournament")
        .WithSummary("Returns one bracket and the state of every match in it.")
        .Produces<TournamentDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", async (
            [FromQuery] int? limit,
            [FromServices] ITournamentRepository repository) =>
        {
            var tournaments = await repository.ListRecentAsync(Math.Clamp(limit ?? 10, 1, 50));
            return Results.Ok(tournaments.Select(t => t.ToDto()).ToList());
        })
        .WithName("ListTournaments")
        .WithSummary("Returns recent bracket runs, newest first.")
        .Produces<IReadOnlyList<TournamentDto>>();

        return app;
    }
}

/// <param name="ModelIds">
/// The field, in any order — the handler seeds it by rating. Must hold 2, 4 or 8 distinct ids.
/// </param>
/// <param name="PromptText">The single prompt every match in the bracket receives.</param>
public sealed record CreateTournamentRequest(
    IReadOnlyList<ModelId>? ModelIds,
    string? PromptText);
