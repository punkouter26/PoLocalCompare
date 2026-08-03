using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class ListDuelsHandler(
    IDuelRepository duelRepository,
    IModelRepository modelRepository,
    IDuelResultRepository duelResultRepository)
{
    public async Task<IReadOnlyList<DuelSummaryDto>> HandleAsync(int limit = 20, string? beforeMonth = null)
    {
        var duels = (await duelRepository.ListAsync(limit, beforeMonth)).ToList();

        // The roster is small and every page re-references the same handful of models, so one
        // GetAllAsync beats two GetByIdAsync per duel. Results live in per-duel partitions and
        // are independent, so they fetch concurrently — but bounded: a page may hold 100 duels
        // and each result can pull a blob, so an unbounded fan-out would be hundreds of calls.
        var modelsByIdTask = modelRepository.GetAllAsync();
        var resultsPerDuel = await StorageConcurrency.ReadAllAsync(
            duels.Count,
            index => duelResultRepository.GetByDuelIdAsync(duels[index].DuelId));
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
                LeftModelName = DuelModelNames.Resolve(leftModel?.DisplayName, duel.LeftModelName, duel.LeftModelId),
                RightModelId = duel.RightModelId,
                RightModelName = DuelModelNames.Resolve(rightModel?.DisplayName, duel.RightModelName, duel.RightModelId),
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
