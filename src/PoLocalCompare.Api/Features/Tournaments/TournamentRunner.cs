using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>
/// Plays a bracket from the draw to the final, one match at a time.
/// </summary>
/// <remarks>
/// Server-side rather than driven from the page, which is the whole difference between this and
/// demo mode: a bracket is eight models and up to seven duels, and it has to survive the tab
/// being closed. The runner owns no state — every step re-reads the tournament and writes the
/// result back — so a process restart mid-bracket resumes rather than losing the run, and
/// <see cref="Tournament.RecordWinner"/> refusing to decide an already-decided match makes a
/// replayed step a no-op rather than a double advance.
///
/// Matches are run strictly in sequence. Round 1 could in principle run in parallel, but the
/// duel queue is shared with everything else the app is doing and eight concurrent inference
/// jobs against one Foundry deployment is how you find its rate limit rather than a winner.
/// </remarks>
public sealed class TournamentRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<TournamentRunner> logger)
{
    /// <summary>
    /// Bracket matches are judged with no grace window: nobody is voting on a run designed to
    /// proceed on its own, and a per-match human countdown would stall the whole bracket on an
    /// unattended tab. Same reasoning — and same value — as demo mode's.
    /// </summary>
    private const int AutoJudgeDelaySeconds = 0;

    /// <summary>How long a single match may take before the run gives up on it.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMinutes(12);

    /// <summary>Gap between verdict polls while a match is running.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the bracket to completion. Every failure path ends with the tournament marked
    /// Complete or Abandoned — a run that stops silently would leave the page spinning forever.
    /// </summary>
    public async Task RunAsync(TournamentId tournamentId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ITournamentRepository>();

                var tournament = await repository.GetByIdAsync(tournamentId);
                if (tournament is null)
                {
                    logger.LogWarning("Tournament {TournamentId} vanished mid-run.", tournamentId);
                    return;
                }

                if (tournament.Status is TournamentStatus.Complete or TournamentStatus.Abandoned)
                    return;

                var match = tournament.NextPlayable();
                if (match is null)
                {
                    // Nothing playable and no champion: a match failed and the bracket cannot be
                    // seeded any further. Bracket rounds depend on each other, so there is no
                    // way to skip past it.
                    tournament.Abandon("A match could not be decided, so the bracket could not be completed.");
                    await repository.UpdateAsync(tournament);
                    return;
                }

                await PlayMatchAsync(scope.ServiceProvider, repository, tournament, match, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down. The tournament stays as it is on disk and resumes if re-run.
            logger.LogInformation("Tournament {TournamentId} run cancelled.", tournamentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tournament {TournamentId} run failed.", tournamentId);
            await MarkAbandonedAsync(tournamentId, $"The run failed: {ex.Message}");
        }
    }

    private async Task PlayMatchAsync(
        IServiceProvider services,
        ITournamentRepository repository,
        Tournament tournament,
        TournamentMatch match,
        CancellationToken cancellationToken)
    {
        var duelRepository = services.GetRequiredService<IDuelRepository>();

        // Start the match if it has not been started. A tournament re-read after a restart can
        // find a match that already has a duel — that one is waited on rather than re-run.
        if (match.DuelId is null)
        {
            var commence = services.GetRequiredService<CommenceDuelHandler>();
            var execution = services.GetRequiredService<DuelExecutionService>();

            var dto = await commence.HandleAsync(new CommenceDuelCommand(
                match.SlotAModelId,
                match.SlotBModelId,
                tournament.PromptText,
                AutoJudgeDelaySeconds,
                tournament.OwnerId));

            match.DuelId = dto.DuelId;
            if (tournament.Status == TournamentStatus.Pending)
                tournament.Status = TournamentStatus.Running;

            await repository.UpdateAsync(tournament);
            await execution.EnqueueAsync(dto.DuelId, AutoJudgeDelaySeconds);

            logger.LogInformation(
                "Tournament {TournamentId} round {Round} match {Index}: {Left} vs {Right} as duel {DuelId}.",
                tournament.TournamentId, match.Round, match.Index, match.SlotAName, match.SlotBName, dto.DuelId);
        }

        var duel = await WaitForVerdictAsync(duelRepository, match.DuelId.Value, cancellationToken);

        if (duel is null)
        {
            match.FailureReason = "The match did not finish in time.";
            tournament.Abandon($"{match.SlotAName} vs {match.SlotBName} did not finish in time.");
            await repository.UpdateAsync(tournament);
            return;
        }

        switch (duel.Verdict)
        {
            case DuelVerdict.Left or DuelVerdict.Right when duel.WinnerModelId is { } winnerId:
            {
                var winnerName = winnerId == match.SlotAModelId ? match.SlotAName : match.SlotBName;
                tournament.RecordWinner(match.Round, match.Index, winnerId, winnerName);
                break;
            }

            case DuelVerdict.Tie:
            {
                // The better seed advances. See Tournament.SeedTieBreak for why this is a
                // tie-break rather than a coin toss or an abandoned bracket.
                var (winnerId, winnerName) = Tournament.SeedTieBreak(match);
                tournament.RecordWinner(match.Round, match.Index, winnerId, winnerName, wonOnSeedTieBreak: true);
                break;
            }

            default:
            {
                // Pending after the wait, or Expired: the judge stood down and there is no
                // evidence to advance on. The bracket stops rather than picking a winner.
                match.FailureReason = duel.JudgeStoodDownReason
                    ?? "The judge could not decide this match.";
                tournament.Abandon($"{match.SlotAName} vs {match.SlotBName} could not be judged.");
                break;
            }
        }

        await repository.UpdateAsync(tournament);
    }

    /// <summary>
    /// Polls the duel until it carries a terminal verdict. Returns null on timeout.
    /// </summary>
    /// <remarks>
    /// Polling rather than subscribing to <see cref="DuelHub"/>: the hub broadcasts to connected
    /// clients, and the point of running the bracket server-side is that there may be none. A
    /// tie is terminal and carries no winner id, so it has to pass this gate on the verdict alone.
    /// </remarks>
    private static async Task<Duel?> WaitForVerdictAsync(
        IDuelRepository duelRepository,
        DuelId duelId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MatchTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var duel = await duelRepository.GetByIdAsync(duelId);
            if (duel is not null && duel.Verdict != DuelVerdict.Pending)
                return duel;

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private async Task MarkAbandonedAsync(TournamentId tournamentId, string reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITournamentRepository>();

            var tournament = await repository.GetByIdAsync(tournamentId);
            if (tournament is null) return;

            tournament.Abandon(reason);
            await repository.UpdateAsync(tournament);
        }
        catch (Exception ex)
        {
            // Last-ditch: the run has already failed, and failing to record that must not
            // replace the original error in the log.
            logger.LogError(ex, "Could not mark tournament {TournamentId} abandoned.", tournamentId);
        }
    }
}
