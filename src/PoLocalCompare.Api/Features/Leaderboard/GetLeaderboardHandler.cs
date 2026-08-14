using PoLocalCompare.Api.Common.Domain;
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

        // Each model's history and results are independent reads, so they overlap rather than
        // costing 2N serial round-trips per cache miss (paid three times over on a cold cache —
        // there are three cached sort variants). Bounded, because a model's result set can pull
        // a blob per oversized output and the roster grows without a ceiling.
        var rows = (await StorageConcurrency.ReadAllAsync(models.Count, async index =>
        {
            var model = models[index];
            var historyTask = eloHistoryRepository.GetLast20Async(model.ModelId);
            var modelResults = (await duelResultRepository.GetByModelIdAsync(model.ModelId)).ToList();
            var sparkline = (await historyTask).Select(x => Math.Round(x.EloAfter, 1)).ToArray();

            // Avg API cost per duel. Only priced duels contribute; unpriced (local/Ollama,
            // or priced-model duels from before a rate was assigned) are excluded from the
            // sum AND the count, so a model that has run only $0.0001 duels doesn't average
            // down to $0.00005 just because we threw in five freebies.
            var pricedResults = modelResults.Where(r => r.ApiCostUsd.HasValue).ToList();
            double? avgCost = pricedResults.Count > 0
                ? pricedResults.Average(r => r.ApiCostUsd!.Value)
                : null;

            return new LeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                ModelType = model.ModelType,
                CurrentElo = Math.Round(model.CurrentElo, 1),
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                WinRate = WinRateCalculator.Calculate(model.WinCount, model.DuelCount),
                DrawCount = model.DrawCount,
                InputTokenPricePerMillion = model.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = model.OutputTokenPricePerMillion,
                AvgApiCostPerDuel = avgCost,
                OutputQualityAvg = modelResults.Count > 0
                    ? modelResults.Average(r => r.OutputQualityScore)
                    : null,
                EloSparkline = sparkline,
            };
        })).ToList();

        var sorted = string.Equals(sortBy, "Quality", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.OutputQualityAvg.HasValue)
                .ThenByDescending(x => x.OutputQualityAvg)
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            : string.Equals(sortBy, "Cost", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.AvgApiCostPerDuel.HasValue)
                .ThenBy(x => x.AvgApiCostPerDuel ?? double.MaxValue) // cheapest first when sorting
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
                ModelType = entry.ModelType,
                CurrentElo = entry.CurrentElo,
                DuelCount = entry.DuelCount,
                WinCount = entry.WinCount,
                WinRate = entry.WinRate,
                DrawCount = entry.DrawCount,
                InputTokenPricePerMillion = entry.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = entry.OutputTokenPricePerMillion,
                AvgApiCostPerDuel = entry.AvgApiCostPerDuel,
                OutputQualityAvg = entry.OutputQualityAvg,
                EloSparkline = entry.EloSparkline,
            })
            .ToList();
    }
}