using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Leaderboard;

/// <summary>
/// Builds the full record between two specific models.
/// </summary>
/// <remarks>
/// The win/loss record is exact — it comes from A's complete ELO history filtered to B. The
/// telemetry averages are not: per-duel results live in a partition each, so they are sampled
/// from the most recent <see cref="SampleSize"/> meetings and the sample size travels with the
/// response rather than being presented as a lifetime average.
/// </remarks>
public sealed class GetHeadToHeadHandler(
    IEloHistoryRepository eloHistoryRepository,
    IModelRepository modelRepository,
    IDuelRepository duelRepository,
    IDuelResultRepository duelResultRepository)
{
    /// <summary>Meetings pulled for the timeline and the telemetry averages.</summary>
    public const int SampleSize = 10;

    public async Task<HeadToHeadDetailDto?> HandleAsync(ModelId modelIdA, ModelId modelIdB)
    {
        if (modelIdA == modelIdB) return null;

        var modelA = await modelRepository.GetByIdAsync(modelIdA);
        var modelB = await modelRepository.GetByIdAsync(modelIdB);
        if (modelA is null || modelB is null) return null;

        // A's history already names the opponent on every row, so one partition scan answers
        // the whole record — reading B's history too would just be the mirror of this.
        var meetings = (await eloHistoryRepository.GetAllByModelAsync(modelIdA))
            .Where(record => record.OpponentModelId == modelIdB)
            .OrderByDescending(record => record.RecordedAt)
            .ToList();

        var winsA = meetings.Count(m => string.Equals(m.Outcome, "Win", StringComparison.OrdinalIgnoreCase));
        var winsB = meetings.Count(m => string.Equals(m.Outcome, "Loss", StringComparison.OrdinalIgnoreCase));

        var sample = meetings.Take(SampleSize).ToList();
        var recentDuels = await LoadRecentDuelsAsync(sample, modelIdA, modelIdB);

        var sparklineTaskA = eloHistoryRepository.GetLast20Async(modelIdA);
        var sparklineB = (await eloHistoryRepository.GetLast20Async(modelIdB)).Select(r => r.EloAfter).ToArray();
        var sparklineA = (await sparklineTaskA).Select(r => r.EloAfter).ToArray();

        return new HeadToHeadDetailDto
        {
            A = BuildSide(modelA, winsA, meetings, isSideA: true, recentDuels, sparklineA),
            B = BuildSide(modelB, winsB, meetings, isSideA: false, recentDuels, sparklineB),
            TotalDuels = meetings.Count,
            SampledDuels = recentDuels.Count,
            LastDuelAt = meetings.Count > 0 ? meetings[0].RecordedAt : null,
            RecentDuels = recentDuels,
        };
    }

    private async Task<List<HeadToHeadDuelDto>> LoadRecentDuelsAsync(
        List<EloRecord> sample,
        ModelId modelIdA,
        ModelId modelIdB)
    {
        if (sample.Count == 0) return [];

        // Bounded fan-out (standards §5.5): each duel and its results sit in their own
        // partition, and SampleSize caps how many of each this can ever request.
        var duels = await StorageConcurrency.ReadAllAsync(
            sample.Count,
            index => duelRepository.GetByIdAsync(sample[index].DuelId));

        var resultSets = await StorageConcurrency.ReadAllAsync(
            sample.Count,
            index => duelResultRepository.GetByDuelIdAsync(sample[index].DuelId));

        var rows = new List<HeadToHeadDuelDto>(sample.Count);

        for (var index = 0; index < sample.Count; index++)
        {
            var duel = duels[index];
            if (duel is null) continue;

            var results = resultSets[index].ToList();
            var resultA = results.FirstOrDefault(r => r.ModelId == modelIdA);
            var resultB = results.FirstOrDefault(r => r.ModelId == modelIdB);

            rows.Add(new HeadToHeadDuelDto
            {
                DuelId = duel.DuelId,
                PromptSummary = duel.PromptText.Length > 70
                    ? duel.PromptText[..70] + "…"
                    : duel.PromptText,
                OccurredAt = sample[index].RecordedAt,
                WinnerModelId = duel.WinnerModelId,
                Source = duel.VerdictSource,
                EloShiftWinner = duel.EloShiftWinner,
                TokenVelocityA = resultA?.TokenVelocity,
                TokenVelocityB = resultB?.TokenVelocity,
                QualityA = resultA?.OutputQualityScore,
                QualityB = resultB?.OutputQualityScore,
                GreenScoreA = resultA?.GreenScore,
                GreenScoreB = resultB?.GreenScore,
            });
        }

        return rows;
    }

    private static HeadToHeadSideDto BuildSide(
        Model model,
        int wins,
        List<EloRecord> meetings,
        bool isSideA,
        List<HeadToHeadDuelDto> recentDuels,
        double[] sparkline)
    {
        // Every history row is written from A's perspective, so B's shift is the negation of
        // whatever A recorded for that meeting.
        var shifts = meetings.Select(m => isSideA ? m.EloShift : -m.EloShift).ToList();

        var velocities = recentDuels
            .Select(d => isSideA ? d.TokenVelocityA : d.TokenVelocityB)
            .Where(v => v is > 0)
            .Select(v => v!.Value)
            .ToList();

        var qualities = recentDuels
            .Select(d => isSideA ? d.QualityA : d.QualityB)
            .Where(q => q.HasValue)
            .Select(q => (double)q!.Value)
            .ToList();

        // Green Score only exists where TDP is known, so this stays null for remote models
        // rather than averaging a partial sample into something that looks complete.
        var greenScores = recentDuels
            .Select(d => isSideA ? d.GreenScoreA : d.GreenScoreB)
            .Where(g => g is > 0)
            .Select(g => g!.Value)
            .ToList();

        return new HeadToHeadSideDto
        {
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            ModelType = model.ModelType,
            CurrentElo = model.CurrentElo,
            Wins = wins,
            AvgEloShift = shifts.Count > 0 ? Math.Round(shifts.Average(), 2) : 0,
            AvgTokenVelocity = velocities.Count > 0 ? Math.Round(velocities.Average(), 1) : null,
            AvgQuality = qualities.Count > 0 ? Math.Round(qualities.Average(), 1) : null,
            AvgGreenScore = greenScores.Count > 0 ? Math.Round(greenScores.Average(), 1) : null,
            EloSparkline = sparkline.Length > 0 ? sparkline : null,
        };
    }
}
