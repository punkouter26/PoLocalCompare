using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

/// <summary>
/// The moments on a model's rating curve worth pointing at: its peak, its biggest upset wins,
/// and its longest winning run.
/// </summary>
/// <remarks>
/// Computed from the history rows rather than from <see cref="EloPointDto"/>, because an upset
/// is defined against the ratings <em>before</em> the duel — <c>EloBefore</c> and
/// <c>OpponentEloBefore</c> — which only the stored row carries. Judged on those pre-duel
/// numbers, not on current ratings: beating a model that was 1400 at the time is an upset even
/// if that model has since collapsed to 1100.
/// </remarks>
public static class ProfileHighlights
{
    /// <summary>How many upsets the profile names. More than a handful stops being a highlight.</summary>
    public const int UpsetLimit = 3;

    /// <summary>
    /// Below this gap a "higher-rated" opponent is a coin flip, not an upset — two models a
    /// point apart are the same rating as far as the curve can tell. 25 points is a 54% favourite.
    /// </summary>
    public const double MinimumUpsetGap = 25;

    /// <param name="history">This model's rows, in any order.</param>
    /// <param name="opponentName">Display name for an opponent id (a retired one included).</param>
    public static ProfileHighlightsDto Compute(
        IReadOnlyCollection<EloRecord> history,
        Func<ModelId, string> opponentName)
    {
        if (history.Count == 0) return new ProfileHighlightsDto();

        var chronological = history.OrderBy(h => h.RecordedAt).ToList();

        var peak = chronological.MaxBy(h => h.EloAfter)!;

        var upsets = chronological
            .Where(h => IsWin(h) && h.OpponentEloBefore - h.EloBefore >= MinimumUpsetGap)
            .OrderByDescending(h => h.OpponentEloBefore - h.EloBefore)
            .ThenByDescending(h => h.RecordedAt)
            .Take(UpsetLimit)
            .Select(h => new UpsetDto
            {
                DuelId = h.DuelId,
                OpponentModelId = h.OpponentModelId,
                OpponentName = opponentName(h.OpponentModelId),
                RatingGap = Math.Round(h.OpponentEloBefore - h.EloBefore, 1),
                Shift = Math.Round(h.EloShift, 1),
                At = h.RecordedAt,
            })
            .ToList();

        // A draw ends a streak: "longest winning run" means consecutive wins.
        int best = 0, current = 0;
        foreach (var row in chronological)
        {
            current = IsWin(row) ? current + 1 : 0;
            best = Math.Max(best, current);
        }

        return new ProfileHighlightsDto
        {
            PeakElo = Math.Round(peak.EloAfter, 1),
            PeakAt = peak.RecordedAt,
            PeakDuelId = peak.DuelId,
            Upsets = upsets,
            LongestWinStreak = best,
        };
    }

    private static bool IsWin(EloRecord row) =>
        string.Equals(row.Outcome, "Win", StringComparison.OrdinalIgnoreCase);
}
