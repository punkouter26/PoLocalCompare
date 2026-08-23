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

            // Value = ELO earned per dollar of API spend, with the divisor floored so a
            // "$0.0001 per duel" model doesn't yield a 15-million Value that swamps every
            // useful comparison. The floor matches the precision we render at (4 d.p., i.e.
            // 0.0001) — anything cheaper is effectively free in this UI. Null when there is
            // no average cost at all (Local/Ollama / unpriced remote, same convention as the
            // AvgApiCostPerDuel column it sits next to).
            const double minCostFloor = 0.0001;
            double? value = avgCost.HasValue
                ? Math.Round(Math.Round(model.CurrentElo, 1) / Math.Max(avgCost.Value, minCostFloor), 2)
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
                Value = value,
                OutputQualityAvg = modelResults.Count > 0
                    ? modelResults.Average(r => r.OutputQualityScore)
                    : null,
                // Only runs that actually produced a token. A failure records a short duration,
                // so counting one would make crashing look like the fastest possible start.
                AvgFirstTokenMs = modelResults.Any(r => !r.IsFailure && r.WarmUpDurationMs > 0)
                    ? Math.Round(modelResults
                        .Where(r => !r.IsFailure && r.WarmUpDurationMs > 0)
                        .Average(r => (double)r.WarmUpDurationMs))
                    : null,
                EloSparkline = sparkline,
            };
        })).ToList();

        // Sort: any "Value" branch must respect the same nullable convention as the Cost branch
        // (priced rows first, then null-rows tail — not floating to whichever ELO they happen
        // to carry, which is meaningless against a score unit the missing row can't compute).
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
            // Ascending: unlike every other dimension here, lower is better.
            : string.Equals(sortBy, "Speed", StringComparison.OrdinalIgnoreCase)
                ? rows
                    .OrderByDescending(x => x.AvgFirstTokenMs.HasValue)
                    .ThenBy(x => x.AvgFirstTokenMs ?? double.MaxValue)
                    .ThenByDescending(x => x.CurrentElo)
                    .ToList()
            : string.Equals(sortBy, "Value", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.Value.HasValue)
                .ThenByDescending(x => x.Value ?? double.MinValue) // highest ELO/$ first
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
                Value = entry.Value,
                OutputQualityAvg = entry.OutputQualityAvg,
                EloSparkline = entry.EloSparkline,
            })
            .ToList();
    }
}