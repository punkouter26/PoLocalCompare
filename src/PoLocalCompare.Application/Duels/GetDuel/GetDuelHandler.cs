using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.GetDuel;

public sealed class GetDuelHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IDuelResultRepository _duelResultRepository;
    private readonly IModelRepository _modelRepository;

    public GetDuelHandler(
        IDuelRepository duelRepository,
        IDuelResultRepository duelResultRepository,
        IModelRepository modelRepository)
    {
        _duelRepository = duelRepository;
        _duelResultRepository = duelResultRepository;
        _modelRepository = modelRepository;
    }

    public async Task<DuelDto?> HandleAsync(GetDuelQuery query)
    {
        var duel = await _duelRepository.GetByIdAsync(query.DuelId);
        if (duel is null) return null;

        var results = await _duelResultRepository.GetByDuelIdAsync(query.DuelId);

        var resultDtos = new List<DuelResultDto>();
        foreach (var r in results)
        {
            var model = await _modelRepository.GetByIdAsync(r.ModelId);
            resultDtos.Add(new DuelResultDto
            {
                ModelId = r.ModelId,
                ModelName = model?.DisplayName ?? r.ModelId,
                WarmUpDurationMs = r.WarmUpDurationMs,
                GenerationDurationMs = r.GenerationDurationMs,
                TotalDurationMs = r.TotalDurationMs,
                TokenCount = r.TokenCount,
                TokenVelocity = r.TokenVelocity,
                HtmlOutputRaw = r.HtmlOutputRaw,
                HtmlOutputSizeBytes = r.HtmlOutputSizeBytes,
                CharacterDensityRatio = r.CharacterDensityRatio,
                OutputQualityScore = r.OutputQualityScore,
                IsFailure = r.IsFailure,
                FailureReason = r.FailureReason,
                EnergyWh = r.EnergyWh,
                EnergyCostUsd = r.EnergyCostUsd,
                ApiCostUsd = r.ApiCostUsd,
                GreenScore = r.GreenScore,
            });
        }

        return new DuelDto
        {
            DuelId = duel.DuelId,
            PromptText = duel.PromptText,
            PromptFull = duel.PromptFull,
            LeftModelId = duel.LeftModelId,
            RightModelId = duel.RightModelId,
            StartedAt = duel.StartedAt,
            CompletedAt = duel.CompletedAt,
            Verdict = (DuelVerdict)duel.Verdict,
            WinnerModelId = duel.WinnerModelId,
            LoserModelId = duel.LoserModelId,
            EloShiftWinner = duel.EloShiftWinner,
            EloShiftLoser = duel.EloShiftLoser,
            TimeLimitSeconds = 300,
            Results = resultDtos,
        };
    }
}
