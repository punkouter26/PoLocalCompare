using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

public sealed class GetKillListHandler
{
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly IModelRepository _modelRepository;

    public GetKillListHandler(IEloHistoryRepository eloHistoryRepository, IModelRepository modelRepository)
    {
        _eloHistoryRepository = eloHistoryRepository;
        _modelRepository = modelRepository;
    }

    public async Task<IReadOnlyList<HeadToHeadDto>> HandleAsync(ModelId modelId)
    {
        var history = (await _eloHistoryRepository.GetAllByModelAsync(modelId)).ToList();

        var grouped = history
            .GroupBy(x => x.OpponentModelId)
            .Select(group =>
            {
                var last = group.OrderByDescending(x => x.RecordedAt).First();
                var wins = group.Count(x => string.Equals(x.Outcome, "Win", StringComparison.OrdinalIgnoreCase));
                var losses = group.Count(x => string.Equals(x.Outcome, "Loss", StringComparison.OrdinalIgnoreCase));

                return new
                {
                    OpponentModelId = group.Key,
                    Wins = wins,
                    Losses = losses,
                    LastDuelId = last.DuelId,
                    LastDuelAt = last.RecordedAt,
                };
            })
            .OrderByDescending(x => x.LastDuelAt)
            .ToList();

        var allModels = (await _modelRepository.GetAllAsync()).ToDictionary(x => x.ModelId, x => x.DisplayName);

        return grouped.Select(x => new HeadToHeadDto
        {
            OpponentModelId = x.OpponentModelId,
            // Mirror the Archive's "Retired model" placeholder: the catalog entry may have
            // been wiped after the duel finished, and rendering the raw 25-32 char ULID here
            // was a readability bug — both the Leaderboard and Archive use the same fallback.
            OpponentName = allModels.TryGetValue(x.OpponentModelId, out var displayName)
                ? displayName
                : (PoLocalCompare.Api.Features.Duels.DuelModelNames
                    .Resolve(liveDisplayName: null, snapshotName: null, x.OpponentModelId)
                    == x.OpponentModelId
                    ? "Retired model"
                    : x.OpponentModelId),
            Wins = x.Wins,
            Losses = x.Losses,
            LastDuelId = x.LastDuelId,
            LastDuelAt = x.LastDuelAt,
        }).ToList();
    }
}
