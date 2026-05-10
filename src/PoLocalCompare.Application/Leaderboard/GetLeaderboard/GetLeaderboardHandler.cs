using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Application.Leaderboard.GetLeaderboard;

public sealed class GetLeaderboardHandler
{
    private readonly IModelRepository _modelRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;

    public GetLeaderboardHandler(IModelRepository modelRepository, IEloHistoryRepository eloHistoryRepository)
    {
        _modelRepository = modelRepository;
        _eloHistoryRepository = eloHistoryRepository;
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>> HandleAsync(GetLeaderboardQuery query)
    {
        var models = (await _modelRepository.GetAllAsync()).ToList();
        var rows = new List<LeaderboardEntryDto>(models.Count);

        foreach (var model in models)
        {
            var history = await _eloHistoryRepository.GetLast20Async(model.ModelId);
            var sparkline = history.Select(x => Math.Round(x.EloAfter, 1)).ToArray();

            rows.Add(new LeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                CurrentElo = Math.Round(model.CurrentElo, 1),
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                GreenScoreAvg = model.GreenScoreAvg > 0 ? Math.Round(model.GreenScoreAvg, 1) : null,
                EloSparkline = sparkline,
            });
        }

        var sorted = string.Equals(query.SortBy, "GreenScore", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.GreenScoreAvg.HasValue)
                .ThenByDescending(x => x.GreenScoreAvg)
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            : rows
                .OrderByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList();

        return sorted
            .Select((entry, index) => new LeaderboardEntryDto
            {
                Rank = index + 1,
                ModelId = entry.ModelId,
                DisplayName = entry.DisplayName,
                CurrentElo = entry.CurrentElo,
                DuelCount = entry.DuelCount,
                WinCount = entry.WinCount,
                GreenScoreAvg = entry.GreenScoreAvg,
                EloSparkline = entry.EloSparkline,
            })
            .ToList();
    }
}
