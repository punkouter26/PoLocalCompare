using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.ListDuels;

public sealed class ListDuelsHandler(
    IDuelRepository duelRepository,
    IModelRepository modelRepository,
    IDuelResultRepository duelResultRepository)
{
    public async Task<IReadOnlyList<DuelSummaryDto>> HandleAsync(ListDuelsQuery query)
    {
        var duels = await duelRepository.ListAsync(query.Limit, query.BeforeMonth);

        var result = new List<DuelSummaryDto>();
        foreach (var duel in duels)
        {
            var leftModel = await modelRepository.GetByIdAsync(duel.LeftModelId);
            var rightModel = await modelRepository.GetByIdAsync(duel.RightModelId);
            var duelResults = (await duelResultRepository.GetByDuelIdAsync(duel.DuelId)).ToList();
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
