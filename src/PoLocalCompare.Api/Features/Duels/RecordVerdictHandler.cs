// SOLID: Single Responsibility — verdict recording coordinates ELO + persistence only
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class RecordVerdictHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IModelRepository _modelRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly HybridCache? _cache;
    private readonly double _kFactor;

    /// <param name="cache">
    /// Optional so pure-logic unit tests can construct the handler without a cache.
    /// When supplied (always, under DI) the leaderboard is invalidated here rather than at
    /// the HTTP endpoint — <see cref="AutoJudge"/> calls this handler directly, bypassing the
    /// endpoint, and would otherwise leave a stale leaderboard behind.
    /// </param>
    public RecordVerdictHandler(
        IDuelRepository duelRepository,
        IModelRepository modelRepository,
        IEloHistoryRepository eloHistoryRepository,
        double kFactor = 32.0,
        HybridCache? cache = null)
    {
        _duelRepository = duelRepository;
        _modelRepository = modelRepository;
        _eloHistoryRepository = eloHistoryRepository;
        _kFactor = kFactor;
        _cache = cache;
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

        var leftModel = await _modelRepository.GetByIdAsync(duel.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.LeftModelId}' not found.");
        var rightModel = await _modelRepository.GetByIdAsync(duel.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.RightModelId}' not found.");

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

        return new VerdictResponseDto
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
    }
}