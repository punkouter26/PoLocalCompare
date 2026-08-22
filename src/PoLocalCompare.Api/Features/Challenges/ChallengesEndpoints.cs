using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Challenges;

/// <summary>
/// Ranks models by how reliably they come in under a challenge budget.
/// </summary>
/// <remarks>
/// Aggregates <see cref="ChallengeRecord"/> partitions — one per model — rather than scanning
/// duels for a stamped constraint. Filtered to one <see cref="ChallengeKind"/> at a time on
/// purpose: "best" means seconds for one kind and dollars for another, and a mixed table would
/// have a column whose units changed from row to row.
/// </remarks>
public sealed class GetChallengeLeaderboardHandler(
    IModelRepository modelRepository,
    IChallengeRecordRepository challengeRecordRepository)
{
    public async Task<IReadOnlyList<ChallengeLeaderboardEntryDto>> HandleAsync(ChallengeKind kind)
    {
        if (kind == ChallengeKind.None) return [];

        var models = (await modelRepository.GetAllAsync()).ToList();

        // Bounded fan-out over the roster, same as the leaderboard's — one partition read per
        // model, at most a handful in flight.
        var rows = await StorageConcurrency.ReadAllAsync(models.Count, async index =>
        {
            var model = models[index];
            var records = (await challengeRecordRepository.GetAllByModelAsync(model.ModelId))
                .Where(r => r.Kind == kind)
                .ToList();

            // A model that has never attempted this kind is absent rather than a zero row: it
            // has not failed the budget, it has not met it either.
            if (records.Count == 0) return null;

            var met = records.Count(r => r.Met);
            var measured = records.Where(r => r.Measured.HasValue).Select(r => r.Measured!.Value).ToList();

            return new ChallengeLeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                ModelType = model.ModelType,
                Kind = kind,
                Attempts = records.Count,
                Met = met,
                PassRate = met / (double)records.Count,
                Wins = records.Count(r => r.Won),
                // Every kind is a ceiling, so "best" is always the smallest measurement.
                Best = measured.Count > 0 ? measured.Min() : null,
            };
        });

        return rows
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderByDescending(r => r.PassRate)
            // Attempts breaks the tie before Best so a model that passed once at 100% does not
            // outrank one that passed nine times out of nine.
            .ThenByDescending(r => r.Attempts)
            .ThenBy(r => r.Best ?? double.MaxValue)
            .Select((r, index) => new ChallengeLeaderboardEntryDto
            {
                Rank = index + 1,
                ModelId = r.ModelId,
                DisplayName = r.DisplayName,
                ModelType = r.ModelType,
                Kind = r.Kind,
                Attempts = r.Attempts,
                Met = r.Met,
                PassRate = r.PassRate,
                Wins = r.Wins,
                Best = r.Best,
            })
            .ToList();
    }
}

public static class ChallengesEndpoints
{
    public static IEndpointRouteBuilder MapChallengesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/challenges").WithTags("Challenges").RequireAuthorization();

        group.MapGet("/leaderboard", async (
            [FromQuery] ChallengeKind? kind,
            [FromServices] GetChallengeLeaderboardHandler handler,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            var selected = kind ?? ChallengeKind.MaxSeconds;

            // Shares the leaderboard cache tag: these rows move when a verdict lands, which is
            // exactly what RecordVerdictHandler already invalidates on.
            var rows = await cache.GetOrCreateAsync(
                $"challenge-leaderboard:{selected}",
                async _ => (await handler.HandleAsync(selected)).ToArray(),
                tags: [CacheTags.Leaderboard],
                cancellationToken: cancellationToken);

            return Results.Ok(rows);
        })
        .WithName("GetChallengeLeaderboard")
        .WithSummary("Ranks models by how reliably they come in under one kind of challenge budget.")
        .Produces<IReadOnlyList<ChallengeLeaderboardEntryDto>>();

        return app;
    }
}
