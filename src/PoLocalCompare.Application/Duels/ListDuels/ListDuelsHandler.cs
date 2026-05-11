using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Duels.ListDuels;

public sealed class ListDuelsHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IModelRepository _modelRepository;

    public ListDuelsHandler(IDuelRepository duelRepository, IModelRepository modelRepository)
    {
        _duelRepository = duelRepository;
        _modelRepository = modelRepository;
    }

    public async Task<IReadOnlyList<DuelSummaryDto>> HandleAsync(ListDuelsQuery query)
    {
        var duels = await _duelRepository.ListAsync(query.Limit, query.BeforeMonth);

        var result = new List<DuelSummaryDto>();
        foreach (var duel in duels)
        {
            var leftModel = await _modelRepository.GetByIdAsync(duel.LeftModelId);
            var rightModel = await _modelRepository.GetByIdAsync(duel.RightModelId);

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
            });
        }

        return result;
    }
}
