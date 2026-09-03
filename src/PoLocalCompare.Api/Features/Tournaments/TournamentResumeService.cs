using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>
/// Resumes brackets that outlived the process.
/// </summary>
/// <remarks>
/// The runner has always been restart-safe by design — it holds no state, every step re-reads
/// the tournament, and replayed steps are no-ops — but nothing ever *started* it again after a
/// restart: <c>RunAsync</c> was only ever enqueued by the create endpoint. A host restart
/// (deploy, crash, scale event) left <c>Running</c> brackets spinning on the page forever, with
/// their page claiming "every match is judged automatically" while nothing judged anything.
///
/// This hosted service closes that loop: on startup it finds every non-terminal bracket and
/// hands it back to the runner. Each run is detached on the thread pool so a slow bracket does
/// not delay startup; the runner's own guards make a concurrent re-run impossible from here
/// (one process, one resume) and harmless if it ever happens (duel ids are persisted before
/// execution, and a re-read skips matches that already have one).
/// </remarks>
public sealed class TournamentResumeService(
    IServiceScopeFactory scopeFactory,
    TournamentRunner runner,
    ILogger<TournamentResumeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITournamentRepository>();

            // Recent window only: a bracket older than the retention window on this page is
            // either finished or already abandoned; ListRecentAsync is the same read the
            // tournament page itself uses, so resume coverage can never drift from what the
            // UI believes exists.
            var resumable = (await repository.ListRecentAsync(25))
                .Where(t => t.Status is TournamentStatus.Pending or TournamentStatus.Running)
                .ToList();

            if (resumable.Count == 0) return;

            logger.LogInformation(
                "Resuming {Count} non-terminal tournament(s) after startup.", resumable.Count);

            foreach (var tournament in resumable)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await runner.RunAsync(tournament.TournamentId, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Resumed tournament {TournamentId} failed.", tournament.TournamentId);
                    }
                }, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            // Startup must never fail because resume could not run: an unrestored bracket is
            // no worse than the pre-resume status quo.
            logger.LogError(ex, "Tournament resume scan failed.");
        }
    }
}
