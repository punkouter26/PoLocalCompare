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

        // Each model's history and results are independent partition reads. Serially that was 2N
        // round-trips of latency on every cache miss — and there are three cached sort variants,
        // so a cold cache paid it three times over.
        var rows = (await Task.WhenAll(models.Select(async model =>
        {
            var historyTask = eloHistoryRepository.GetLast20Async(model.ModelId);
            var modelResults = (await duelResultRepository.GetByModelIdAsync(model.ModelId)).ToList();
            var sparkline = (await historyTask).Select(x => Math.Round(x.EloAfter, 1)).ToArray();

            return new LeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                CurrentElo = Math.Round(model.CurrentElo, 1),
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                OutputQualityAvg = modelResults.Count > 0
                    ? modelResults.Average(r => r.OutputQualityScore)
                    : null,
                GreenScoreAvg = model.GreenScoreAvg > 0 ? model.GreenScoreAvg : null,
                EloSparkline = sparkline,
            };
        }))).ToList();

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