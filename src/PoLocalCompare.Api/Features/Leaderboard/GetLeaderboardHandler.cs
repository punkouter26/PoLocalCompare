using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

public sealed class GetLeaderboardHandler(
    IModelRepository modelRepository,
    IEloHistoryRepository eloHistoryRepository,
    IDuelResultRepository duelResultRepository)
{
    public async Task<IReadOnlyList<LeaderboardEntryDto>> HandleAsync(string sortBy = "Elo")
    {
        var models = (await modelRepository.GetAllAsync()).ToList();
        var rows = new List<LeaderboardEntryDto>(models.Count);

        foreach (var model in models)
        {
            var history = await eloHistoryRepository.GetLast20Async(model.ModelId);
            var sparkline = history.Select(x => Math.Round(x.EloAfter, 1)).ToArray();
            var modelResults = (await duelResultRepository.GetByModelIdAsync(model.ModelId)).ToList();
            var qualityAvg = modelResults.Count > 0
                ? modelResults.Average(r => r.OutputQualityScore)
                : (double?)null;

            rows.Add(new LeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                CurrentElo = Math.Round(model.CurrentElo, 1),
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                OutputQualityAvg = qualityAvg,
                GreenScoreAvg = model.GreenScoreAvg > 0 ? model.GreenScoreAvg : null,
                EloSparkline = sparkline,
            });
        }

        var sorted = string.Equals(sortBy, "GreenScore", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.GreenScoreAvg.HasValue)
                .ThenByDescending(x => x.GreenScoreAvg)
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            : string.Equals(sortBy, "Quality", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.OutputQualityAvg.HasValue)
                .ThenByDescending(x => x.OutputQualityAvg)
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
                OutputQualityAvg = entry.OutputQualityAvg,
                GreenScoreAvg = entry.GreenScoreAvg,
                EloSparkline = entry.EloSparkline,
            })
            .ToList();
    }
}