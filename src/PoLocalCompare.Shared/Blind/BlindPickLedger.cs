using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Blind;

/// <summary>One duel the user judged while the model names were hidden.</summary>
/// <remarks>
/// Recorded only for duels the user actually voted on. A duel the AI judge decided is not a
/// blind pick — nobody expressed a preference — so it never lands here, which is what keeps
/// <see cref="BlindPickLedger.Tally"/> a record of taste rather than of the judge's opinion.
/// </remarks>
public sealed record BlindVote(
    DuelId DuelId,
    ModelId WinnerModelId,
    string WinnerName,
    ModelId LoserModelId,
    string LoserName,
    DateTimeOffset VotedAt);

/// <summary>Per-model blind record, aggregated across every <see cref="BlindVote"/>.</summary>
public sealed record BlindTallyRow(ModelId ModelId, string DisplayName, int Wins, int Losses)
{
    public int Total => Wins + Losses;

    /// <summary>Share of blind appearances the user picked this model in, 0–1.</summary>
    public double WinRate => Total == 0 ? 0 : Wins / (double)Total;
}

/// <summary>
/// A pairing where the user's blind preference contradicts the leaderboard's ELO ordering.
/// </summary>
public sealed record BlindDivergence(
    ModelId PreferredModelId,
    string PreferredName,
    int PreferredRank,
    ModelId RankedModelId,
    string RankedName,
    int RankedRank,
    int PreferredWins,
    int RankedWins);

/// <summary>
/// Pure aggregation over the user's blind votes.
/// </summary>
/// <remarks>
/// Lives in Shared rather than beside <c>BlindModeService</c> in the Client project so the unit
/// tier can reach it: <c>PoLocalCompare.Unit</c> references only the Api project, and anything
/// pure parked under <c>src/PoLocalCompare.Client/</c> is testable by E2E-UI alone — the one
/// suite CI never runs. The interop wrapper that reads and writes <c>localStorage</c> stays in
/// the Client project, because that genuinely needs the browser.
/// </remarks>
public static class BlindPickLedger
{
    /// <summary>How many votes are kept. Old enough picks stop describing current taste.</summary>
    public const int MaxVotes = 200;

    /// <summary>
    /// Adds a vote, newest first. A repeat vote on a duel already in the ledger replaces the
    /// earlier entry rather than double-counting it — a duel accepts exactly one verdict, so two
    /// entries for one id could only ever be a replayed write.
    /// </summary>
    public static IReadOnlyList<BlindVote> Append(
        IReadOnlyList<BlindVote> existing,
        BlindVote vote,
        int maxVotes = MaxVotes)
    {
        var merged = new List<BlindVote>(existing.Count + 1) { vote };
        merged.AddRange(existing.Where(v => v.DuelId != vote.DuelId));
        return merged.Count > maxVotes ? merged[..maxVotes] : merged;
    }

    /// <summary>
    /// Per-model wins and losses, best-liked first. Ties on wins break on win rate, then on
    /// name, so the order is stable across renders rather than dependent on dictionary order.
    /// </summary>
    public static IReadOnlyList<BlindTallyRow> Tally(IReadOnlyList<BlindVote> votes)
    {
        var wins = new Dictionary<ModelId, int>();
        var losses = new Dictionary<ModelId, int>();
        var names = new Dictionary<ModelId, string>();

        foreach (var vote in votes)
        {
            wins[vote.WinnerModelId] = wins.GetValueOrDefault(vote.WinnerModelId) + 1;
            losses[vote.LoserModelId] = losses.GetValueOrDefault(vote.LoserModelId) + 1;

            // Votes arrive newest-first, so TryAdd keeps the most recent name a model was seen
            // under: a renamed model reads under its current name, not the one it carried first.
            names.TryAdd(vote.WinnerModelId, vote.WinnerName);
            names.TryAdd(vote.LoserModelId, vote.LoserName);
        }

        return names.Keys
            .Select(id => new BlindTallyRow(id, names[id], wins.GetValueOrDefault(id), losses.GetValueOrDefault(id)))
            .OrderByDescending(r => r.Wins)
            .ThenByDescending(r => r.WinRate)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Head-to-head counts from the user's blind votes, keyed on the unordered pair so that
    /// "A beat B" and "B beat A" aggregate into one record.
    /// </summary>
    public static IReadOnlyList<(ModelId A, ModelId B, int AWins, int BWins)> HeadToHead(
        IReadOnlyList<BlindVote> votes)
    {
        var pairs = new Dictionary<(ModelId, ModelId), (int AWins, int BWins)>();

        foreach (var vote in votes)
        {
            var forward = vote.WinnerModelId.CompareTo(vote.LoserModelId) < 0;
            var key = forward
                ? (vote.WinnerModelId, vote.LoserModelId)
                : (vote.LoserModelId, vote.WinnerModelId);

            var current = pairs.GetValueOrDefault(key);
            pairs[key] = forward
                ? (current.AWins + 1, current.BWins)
                : (current.AWins, current.BWins + 1);
        }

        return pairs.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.AWins, kv.Value.BWins)).ToList();
    }

    /// <summary>
    /// Pairings where the user's blind preference runs against the leaderboard. This is the
    /// payoff of blind mode: not "were you right", which has no answer once the user's own vote
    /// becomes the verdict, but "does your taste match the rating you would otherwise defer to".
    /// </summary>
    /// <param name="ranks">
    /// Leaderboard rank per model, 1 = best. Models absent from the map are skipped — an
    /// unranked model has nothing to disagree with.
    /// </param>
    public static IReadOnlyList<BlindDivergence> Divergences(
        IReadOnlyList<BlindVote> votes,
        IReadOnlyDictionary<ModelId, int> ranks)
    {
        var names = new Dictionary<ModelId, string>();
        foreach (var vote in votes)
        {
            names.TryAdd(vote.WinnerModelId, vote.WinnerName);
            names.TryAdd(vote.LoserModelId, vote.LoserName);
        }

        var found = new List<BlindDivergence>();

        foreach (var (a, b, aWins, bWins) in HeadToHead(votes))
        {
            if (aWins == bWins) continue;                       // no preference expressed
            if (!ranks.TryGetValue(a, out var rankA)) continue;
            if (!ranks.TryGetValue(b, out var rankB)) continue;
            if (rankA == rankB) continue;

            var preferredIsA = aWins > bWins;
            var preferred = preferredIsA ? a : b;
            var other = preferredIsA ? b : a;
            var preferredWins = preferredIsA ? aWins : bWins;
            var otherWins = preferredIsA ? bWins : aWins;
            var preferredRank = preferredIsA ? rankA : rankB;
            var otherRank = preferredIsA ? rankB : rankA;

            // A contradiction only when the model the user liked less is the one rated higher.
            if (otherRank >= preferredRank) continue;

            found.Add(new BlindDivergence(
                PreferredModelId: preferred,
                PreferredName: names.GetValueOrDefault(preferred, preferred.Value),
                PreferredRank: preferredRank,
                RankedModelId: other,
                RankedName: names.GetValueOrDefault(other, other.Value),
                RankedRank: otherRank,
                PreferredWins: preferredWins,
                RankedWins: otherWins));
        }

        // Highest-rated contradicted model first, then widest rank gap — the most surprising
        // disagreement is the one worth reading.
        return found
            .OrderBy(d => d.RankedRank)
            .ThenByDescending(d => d.PreferredRank - d.RankedRank)
            .ToList();
    }
}
