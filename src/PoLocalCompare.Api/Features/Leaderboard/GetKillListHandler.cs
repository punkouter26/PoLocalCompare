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
                var draws = group.Count(x => string.Equals(x.Outcome, "Draw", StringComparison.OrdinalIgnoreCase));

                return new
                {
                    OpponentModelId = group.Key,
                    Wins = wins,
                    Losses = losses,
                    Draws = draws,
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
            // One shared resolver, not a local reimplementation of it: the catalog entry may
            // have been wiped after the duel finished, and rendering the raw ULID here was a
            // readability bug. There is no snapshot name on a history row, so a miss on the
            // catalog falls straight through to the placeholder.
            OpponentName = allModels.TryGetValue(x.OpponentModelId, out var displayName)
                ? displayName
                : ModelDisplayName.ResolveForDisplay(null, null, x.OpponentModelId),
            Wins = x.Wins,
            Losses = x.Losses,
            Draws = x.Draws,
            TotalDuels = x.Wins + x.Losses + x.Draws,
            LastDuelId = x.LastDuelId,
            LastDuelAt = x.LastDuelAt,
        }).ToList();
    }
}
