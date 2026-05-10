// SOLID: Single Responsibility — verdict recording coordinates ELO + persistence only
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Domain.Services;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using DomainDuelVerdict = PoLocalCompare.Domain.Enums.DuelVerdict;

namespace PoLocalCompare.Application.Duels.RecordVerdict;

public sealed class RecordVerdictHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IModelRepository _modelRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly double _kFactor;

    public RecordVerdictHandler(
        IDuelRepository duelRepository,
        IModelRepository modelRepository,
        IEloHistoryRepository eloHistoryRepository,
        double kFactor = 32.0)
    {
        _duelRepository = duelRepository;
        _modelRepository = modelRepository;
        _eloHistoryRepository = eloHistoryRepository;
        _kFactor = kFactor;
    }

    /// <summary>
    /// Returns null if duel not found.
    /// Throws <see cref="InvalidOperationException"/> if verdict already recorded (caller maps to 409).
    /// Throws <see cref="ArgumentException"/> if verdict is Pending (caller maps to 422).
    /// </summary>
    public async Task<VerdictResponseDto?> HandleAsync(RecordVerdictCommand command)
    {
        if (command.Verdict == DuelVerdict.Pending)
            throw new ArgumentException("Verdict cannot be Pending.", nameof(command));

        var duel = await _duelRepository.GetByIdAsync(command.DuelId);
        if (duel is null) return null;

        if (duel.Verdict != DomainDuelVerdict.Pending)
            throw new InvalidOperationException("Verdict has already been recorded for this duel.");

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
        duel.Verdict = (DomainDuelVerdict)command.Verdict;
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
        };
    }
}
