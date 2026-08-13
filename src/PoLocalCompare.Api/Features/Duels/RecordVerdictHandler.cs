// SOLID: Single Responsibility — verdict recording coordinates ELO + persistence only
using Azure;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class RecordVerdictHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IModelRepository _modelRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly IDuelResultRepository? _duelResultRepository;
    private readonly HybridCache? _cache;
    private readonly LobbyNotifier? _lobby;
    private readonly double _kFactor;

    /// <param name="cache">
    /// Optional so pure-logic unit tests can construct the handler without a cache.
    /// When supplied (always, under DI) the leaderboard is invalidated here rather than at
    /// the HTTP endpoint — <see cref="AutoJudge"/> calls this handler directly, bypassing the
    /// endpoint, and would otherwise leave a stale leaderboard behind.
    /// </param>
    /// <param name="lobby">
    /// Optional for the same reason. Announcing from here rather than from the two callers is
    /// what makes the activity ticker complete: this handler is the only path by which ELO
    /// moves, so a verdict that reaches storage cannot fail to reach the ticker.
    /// </param>
    /// <param name="duelResultRepository">
    /// Optional for the same reason as the two above. Supplied under DI, where it enforces the
    /// no-evidence rule described on <see cref="HandleAsync"/>.
    /// </param>
    public RecordVerdictHandler(
        IDuelRepository duelRepository,
        IModelRepository modelRepository,
        IEloHistoryRepository eloHistoryRepository,
        double kFactor = 32.0,
        HybridCache? cache = null,
        LobbyNotifier? lobby = null,
        IDuelResultRepository? duelResultRepository = null)
    {
        _duelRepository = duelRepository;
        _modelRepository = modelRepository;
        _eloHistoryRepository = eloHistoryRepository;
        _kFactor = kFactor;
        _cache = cache;
        _lobby = lobby;
        _duelResultRepository = duelResultRepository;
    }

    /// <summary>
    /// <see cref="HandleAsync"/>, retried once when it loses an optimistic-concurrency race.
    /// </summary>
    /// <remarks>
    /// ELO moves only through this handler but it now has two callers — the verdict endpoint and
    /// <see cref="AutoJudge"/> — which both have to survive a 412 the same way. Keeping the retry
    /// here means a third verdict writer inherits it instead of pasting the policy a third time.
    /// </remarks>
    public async Task<VerdictResponseDto?> HandleWithRetryAsync(RecordVerdictCommand command)
    {
        try
        {
            return await HandleAsync(command);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Lost an optimistic-concurrency race (standards §5.5) — HandleAsync re-reads
            // everything, so one retry resolves against the fresh state.
            return await HandleAsync(command);
        }
    }

    /// <summary>
    /// Returns null if duel not found.
    /// Throws <see cref="InvalidOperationException"/> if verdict already recorded (caller maps to 409).
    /// Throws <see cref="ArgumentException"/> if verdict is Pending or Expired (caller maps to 422).
    /// </summary>
    public async Task<VerdictResponseDto?> HandleAsync(RecordVerdictCommand command)
    {
        if (command.Verdict == DuelVerdict.Pending)
            throw new ArgumentException("Verdict cannot be Pending.", nameof(command));

        if (command.Verdict == DuelVerdict.Expired)
            throw new ArgumentException("Verdict cannot be Expired — use the expiration workflow instead.", nameof(command));

        var duel = await _duelRepository.GetByIdAsync(command.DuelId);
        if (duel is null) return null;

        if (duel.Verdict == DuelVerdict.Expired)
            throw new InvalidOperationException("This duel has expired and cannot accept a verdict.");

        if (duel.Verdict != DuelVerdict.Pending)
            throw new InvalidOperationException("Verdict has already been recorded for this duel.");

        // Check deadline
        if (duel.IsExpired)
        {
            duel.Verdict = DuelVerdict.Expired;
            await _duelRepository.UpdateAsync(duel);
            throw new InvalidOperationException("This duel has expired and cannot accept a verdict.");
        }

        await GuardAgainstNoEvidenceAsync(duel);

        var leftModel = await _modelRepository.GetByIdAsync(duel.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.LeftModelId}' not found.");
        var rightModel = await _modelRepository.GetByIdAsync(duel.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.RightModelId}' not found.");

        if (command.Verdict == DuelVerdict.Tie)
            return await RecordTieAsync(command, duel, leftModel, rightModel);

        // Determine winner/loser based on verdict (Left or Right)
        Model winner;
        Model loser;

        if (command.Verdict == DuelVerdict.Left)
        {
            winner = leftModel;
            loser = rightModel;
        }
        else
        {
            winner = rightModel;
            loser = leftModel;
        }

        var winnerEloBefore = winner.CurrentElo;
        var loserEloBefore = loser.CurrentElo;

        // Winner = outcome 1.0, Loser = outcome 0.0
        var (newWinnerElo, newLoserElo) = EloCalculator.Calculate(
            winnerEloBefore,
            loserEloBefore,
            _kFactor,
            outcomeA: 1.0);

        var eloShiftWinner = Math.Round(newWinnerElo - winnerEloBefore, 1);
        var eloShiftLoser = Math.Round(newLoserElo - loserEloBefore, 1);

        // Update winner model
        winner.CurrentElo = newWinnerElo;
        winner.DuelCount++;
        winner.WinCount++;
        await _modelRepository.UpdateAsync(winner);

        // Update loser model
        loser.CurrentElo = newLoserElo;
        loser.DuelCount++;
        await _modelRepository.UpdateAsync(loser);

        // Persist duel verdict
        duel.Verdict = command.Verdict;
        duel.VerdictSource = command.Source;
        duel.JudgeRationale = command.JudgeRationale;
        duel.JudgeModel = command.JudgeModel;
        duel.WinnerModelId = winner.ModelId;
        duel.LoserModelId = loser.ModelId;
        duel.EloShiftWinner = eloShiftWinner;
        duel.EloShiftLoser = eloShiftLoser;
        duel.CompletedAt ??= DateTimeOffset.UtcNow;
        await _duelRepository.UpdateAsync(duel);

        // Persist EloRecord snapshots for both models
        var winnerRecord = new EloRecord(
            winner.ModelId,
            command.DuelId,
            eloAfter: newWinnerElo,
            eloBefore: winnerEloBefore,
            outcome: "Win",
            opponentModelId: loser.ModelId,
            opponentEloBefore: loserEloBefore);

        var loserRecord = new EloRecord(
            loser.ModelId,
            command.DuelId,
            eloAfter: newLoserElo,
            eloBefore: loserEloBefore,
            outcome: "Loss",
            opponentModelId: winner.ModelId,
            opponentEloBefore: winnerEloBefore);

        await _eloHistoryRepository.SaveAsync(winnerRecord);
        await _eloHistoryRepository.SaveAsync(loserRecord);

        // ELO moved ⇒ every cached leaderboard projection is stale.
        if (_cache is not null)
            await _cache.RemoveByTagAsync(CacheTags.Leaderboard);

        var response = new VerdictResponseDto
        {
            DuelId = command.DuelId,
            Verdict = command.Verdict,
            WinnerModelId = winner.ModelId,
            LoserModelId = loser.ModelId,
            EloShiftWinner = eloShiftWinner,
            EloShiftLoser = eloShiftLoser,
            WinnerEloAfter = newWinnerElo,
            LoserEloAfter = newLoserElo,
            Source = command.Source,
            JudgeRationale = command.JudgeRationale,
        };

        // Announced last, after everything durable has landed. LobbyNotifier swallows its own
        // failures, so a broken ticker cannot undo a recorded verdict.
        if (_lobby is not null)
        {
            await _lobby.VerdictRecordedAsync(
                duel, leftModel.DisplayName, rightModel.DisplayName, winner.DisplayName, response);
        }

        return response;
    }

    /// <summary>
    /// Records a judged draw: both models bank a duel and a draw, neither rating moves.
    /// </summary>
    /// <remarks>
    /// A tie is evidence — the judge read both outputs and found them equivalent — so it is a
    /// terminal verdict rather than a return to Pending. It still moves no ELO, which keeps the
    /// "ratings never move on no evidence" invariant intact from the other direction: equal
    /// evidence is not a reason to separate two models. History rows are written for both sides
    /// with <c>eloAfter == eloBefore</c> so the sparkline shows a flat step rather than a gap,
    /// and so the head-to-head record can count the meeting at all.
    /// </remarks>
    private async Task<VerdictResponseDto?> RecordTieAsync(
        RecordVerdictCommand command,
        Duel duel,
        Model leftModel,
        Model rightModel)
    {
        var leftElo = leftModel.CurrentElo;
        var rightElo = rightModel.CurrentElo;

        leftModel.DuelCount++;
        leftModel.DrawCount++;
        await _modelRepository.UpdateAsync(leftModel);

        rightModel.DuelCount++;
        rightModel.DrawCount++;
        await _modelRepository.UpdateAsync(rightModel);

        // No winner, no loser, no shift. The nullable winner/loser fields on the duel stay null
        // so nothing downstream can mistake one side for having won.
        duel.Verdict = DuelVerdict.Tie;
        duel.VerdictSource = command.Source;
        duel.JudgeRationale = command.JudgeRationale;
        duel.JudgeModel = command.JudgeModel;
        duel.WinnerModelId = null;
        duel.LoserModelId = null;
        duel.EloShiftWinner = 0;
        duel.EloShiftLoser = 0;
        duel.CompletedAt ??= DateTimeOffset.UtcNow;
        await _duelRepository.UpdateAsync(duel);

        await _eloHistoryRepository.SaveAsync(new EloRecord(
            leftModel.ModelId, command.DuelId,
            eloAfter: leftElo, eloBefore: leftElo,
            outcome: "Draw",
            opponentModelId: rightModel.ModelId,
            opponentEloBefore: rightElo));

        await _eloHistoryRepository.SaveAsync(new EloRecord(
            rightModel.ModelId, command.DuelId,
            eloAfter: rightElo, eloBefore: rightElo,
            outcome: "Draw",
            opponentModelId: leftModel.ModelId,
            opponentEloBefore: leftElo));

        // Duel counts and the W/L column changed even though ELO did not, so the cached
        // leaderboard projections are stale exactly as they are for a decisive verdict.
        if (_cache is not null)
            await _cache.RemoveByTagAsync(CacheTags.Leaderboard);

        var response = new VerdictResponseDto
        {
            DuelId = command.DuelId,
            Verdict = DuelVerdict.Tie,
            WinnerModelId = null,
            LoserModelId = null,
            EloShiftWinner = 0,
            EloShiftLoser = 0,
            WinnerEloAfter = null,
            LoserEloAfter = null,
            Source = command.Source,
            JudgeRationale = command.JudgeRationale,
        };

        if (_lobby is not null)
        {
            await _lobby.VerdictRecordedAsync(
                duel, leftModel.DisplayName, rightModel.DisplayName, winnerModelName: null, response);
        }

        return response;
    }

    /// <summary>
    /// Refuses a verdict when both models failed, so ELO cannot move on no evidence.
    /// </summary>
    /// <remarks>
    /// <see cref="AutoJudge"/> already stands down in this case, but it is not the only writer:
    /// the verdict endpoint reaches this handler directly, and the Arena's "both sides failed"
    /// check was client-side only. A duel with both results failed had therefore been recorded
    /// as a win with a ±16 ELO swing, which is exactly what this handler being the single gate
    /// is supposed to prevent.
    ///
    /// Deliberately narrow: it fires only when both sides have a stored result AND both are
    /// failures. A duel with no results yet is a different situation (nothing has run) and is
    /// left alone, which also keeps fixtures that record a verdict without seeding results
    /// working.
    /// </remarks>
    private async Task GuardAgainstNoEvidenceAsync(Duel duel)
    {
        if (_duelResultRepository is null) return;

        var results = await _duelResultRepository.GetByDuelIdAsync(duel.DuelId);
        var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
        var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

        if (left is null || right is null) return;

        if (left.IsFailure && right.IsFailure)
        {
            throw new ArgumentException(
                "Both models failed, so there is nothing to judge. Retry the duel instead.",
                nameof(duel));
        }
    }
}