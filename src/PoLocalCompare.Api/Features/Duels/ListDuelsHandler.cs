using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class ListDuelsHandler(
    IDuelRepository duelRepository,
    IModelRepository modelRepository,
    IDuelResultRepository duelResultRepository)
{
    public async Task<IReadOnlyList<DuelSummaryDto>> HandleAsync(ListDuelsQuery query)
    {
        var duels = (await duelRepository.ListAsync(query.Limit, query.BeforeMonth)).ToList();

        // The roster is small and every page re-references the same handful of models, so one
        // GetAllAsync beats two GetByIdAsync per duel. Results live in per-duel partitions and
        // are independent, so they fetch concurrently rather than one duel at a time.
        var modelsByIdTask = modelRepository.GetAllAsync();
        var resultsPerDuel = await Task.WhenAll(
            duels.Select(duel => duelResultRepository.GetByDuelIdAsync(duel.DuelId)));
        var modelsById = (await modelsByIdTask).ToDictionary(model => model.ModelId);

        var result = new List<DuelSummaryDto>(duels.Count);
        for (var index = 0; index < duels.Count; index++)
        {
            var duel = duels[index];
            modelsById.TryGetValue(duel.LeftModelId, out var leftModel);
            modelsById.TryGetValue(duel.RightModelId, out var rightModel);
            var duelResults = resultsPerDuel[index].ToList();
            var leftResult = duelResults.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
            var rightResult = duelResults.FirstOrDefault(r => r.ModelId == duel.RightModelId);
            var qualitySamples = duelResults.Select(r => r.OutputQualityScore).ToList();

            result.Add(new DuelSummaryDto
            {
                DuelId = duel.DuelId,
                PromptSummary = duel.PromptText.Length > 80
                    ? duel.PromptText[..80] + "…"
                    : duel.PromptText,
                LeftModelId = duel.LeftModelId,
                LeftModelName = leftModel?.DisplayName ?? duel.LeftModelId,
                RightModelId = duel.RightModelId,
                RightModelName = rightModel?.DisplayName ?? duel.RightModelId,
                StartedAt = duel.StartedAt,
                CompletedAt = duel.CompletedAt,
                Verdict = (DuelVerdict)duel.Verdict,
                WinnerModelId = duel.WinnerModelId,
                LeftOutputQualityScore = leftResult?.OutputQualityScore,
                RightOutputQualityScore = rightResult?.OutputQualityScore,
                AvgOutputQualityScore = qualitySamples.Count > 0 ? qualitySamples.Average() : null,
            });
        }

        return result;
    }
}
