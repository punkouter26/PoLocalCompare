using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class ListDuelsHandler(
    IDuelRepository duelRepository,
    IModelRepository modelRepository,
    IDuelResultRepository duelResultRepository)
{
    /// <param name="before">Keyset cursor: the oldest duel id the caller already has. Only strictly older duels come back.</param>
    /// <param name="verdicts">When non-empty, only duels with one of these verdicts — applied before the limit, not after.</param>
    public async Task<IReadOnlyList<DuelSummaryDto>> HandleAsync(
        int limit = 20,
        DuelId? before = null,
        IReadOnlyCollection<DuelVerdict>? verdicts = null)
    {
        var duels = (await duelRepository.ListAsync(limit, beforeMonth: null, before, verdicts)).ToList();

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
                // The whole prompt, so the Archive's re-run starts the same duel rather than one
                // prompted with the 80-character summary below.
                PromptText = duel.PromptText,
                PromptSummary = duel.PromptText.Length > 80
                    ? duel.PromptText[..80] + "…"
                    : duel.PromptText,
                LeftModelId = duel.LeftModelId,
                LeftModelName = ModelDisplayName.Resolve(leftModel?.DisplayName, duel.LeftModelName, duel.LeftModelId),
                RightModelId = duel.RightModelId,
                RightModelName = ModelDisplayName.Resolve(rightModel?.DisplayName, duel.RightModelName, duel.RightModelId),
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
