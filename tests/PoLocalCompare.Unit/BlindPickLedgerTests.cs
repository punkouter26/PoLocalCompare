using PoLocalCompare.Shared.Blind;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

public class BlindPickLedgerTests
{
    private static readonly ModelId Fast = ModelId.From("model-fast");
    private static readonly ModelId Slow = ModelId.From("model-slow");
    private static readonly ModelId Third = ModelId.From("model-third");

    private static BlindVote Vote(string duelId, ModelId winner, ModelId loser) =>
        new(DuelId.From(duelId),
            winner, Name(winner),
            loser, Name(loser),
            DateTimeOffset.UnixEpoch);

    private static string Name(ModelId id) => id.Value.Replace("model-", "Model ", StringComparison.Ordinal);

    // ── Append ────────────────────────────────────────────────────────────

    [Fact]
    public void Append_PutsTheNewestVoteFirst()
    {
        var first = Vote("duel-1", Fast, Slow);
        var second = Vote("duel-2", Slow, Fast);

        var result = BlindPickLedger.Append([first], second);

        Assert.Equal(2, result.Count);
        Assert.Equal(second.DuelId, result[0].DuelId);
    }

    /// <summary>
    /// A duel accepts exactly one verdict, so two entries for one duel could only ever be a
    /// replayed write — or the user re-voting after an optimistic rollback. Either way the
    /// later entry is the real preference and must not be counted twice.
    /// </summary>
    [Fact]
    public void Append_ReplacesAnEarlierVoteOnTheSameDuel()
    {
        var original = Vote("duel-1", Fast, Slow);
        var corrected = Vote("duel-1", Slow, Fast);

        var result = BlindPickLedger.Append([original], corrected);

        Assert.Single(result);
        Assert.Equal(Slow, result[0].WinnerModelId);
    }

    [Fact]
    public void Append_TrimsToTheCap_DroppingTheOldest()
    {
        IReadOnlyList<BlindVote> ledger = [];
        for (var i = 0; i < 5; i++)
            ledger = BlindPickLedger.Append(ledger, Vote($"duel-{i}", Fast, Slow), maxVotes: 3);

        Assert.Equal(3, ledger.Count);
        Assert.Equal(DuelId.From("duel-4"), ledger[0].DuelId);
        Assert.DoesNotContain(ledger, v => v.DuelId == DuelId.From("duel-0"));
    }

    // ── Tally ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tally_CountsWinsAndLossesPerModel()
    {
        var rows = BlindPickLedger.Tally([
            Vote("d1", Fast, Slow),
            Vote("d2", Fast, Slow),
            Vote("d3", Slow, Fast),
        ]);

        var fast = Assert.Single(rows, r => r.ModelId == Fast);
        Assert.Equal(2, fast.Wins);
        Assert.Equal(1, fast.Losses);
        Assert.Equal(3, fast.Total);

        var slow = Assert.Single(rows, r => r.ModelId == Slow);
        Assert.Equal(1, slow.Wins);
        Assert.Equal(2, slow.Losses);
    }

    [Fact]
    public void Tally_OrdersByWinsDescending()
    {
        var rows = BlindPickLedger.Tally([
            Vote("d1", Fast, Slow),
            Vote("d2", Fast, Slow),
            Vote("d3", Slow, Fast),
        ]);

        Assert.Equal(Fast, rows[0].ModelId);
    }

    [Fact]
    public void Tally_OfNothingIsEmpty_NotAZeroRow()
    {
        Assert.Empty(BlindPickLedger.Tally([]));
    }

    [Theory]
    [InlineData(2, 0, 1.0)]
    [InlineData(1, 1, 0.5)]
    [InlineData(0, 2, 0.0)]
    public void WinRate_IsWinsOverTotal(int wins, int losses, double expected)
    {
        var row = new BlindTallyRow(Fast, "Model Fast", wins, losses);
        Assert.Equal(expected, row.WinRate, precision: 5);
    }

    /// <summary>A model with no appearances must not divide by zero.</summary>
    [Fact]
    public void WinRate_OfAnUnplayedModelIsZero()
    {
        Assert.Equal(0, new BlindTallyRow(Fast, "Model Fast", 0, 0).WinRate);
    }

    // ── HeadToHead ────────────────────────────────────────────────────────

    /// <summary>
    /// The pair key is ordered, so "Fast beat Slow" and "Slow beat Fast" have to aggregate into
    /// one record rather than two mirror-image ones that each look like a clean sweep.
    /// </summary>
    [Fact]
    public void HeadToHead_AggregatesBothDirectionsIntoOnePair()
    {
        var pairs = BlindPickLedger.HeadToHead([
            Vote("d1", Fast, Slow),
            Vote("d2", Slow, Fast),
            Vote("d3", Fast, Slow),
        ]);

        var pair = Assert.Single(pairs);
        var fastWins = pair.A == Fast ? pair.AWins : pair.BWins;
        var slowWins = pair.A == Fast ? pair.BWins : pair.AWins;

        Assert.Equal(2, fastWins);
        Assert.Equal(1, slowWins);
    }

    // ── Divergences ───────────────────────────────────────────────────────

    /// <summary>
    /// The payoff case: the user blind-preferred the model the leaderboard rates lower.
    /// </summary>
    [Fact]
    public void Divergences_ReportsAPreferenceThatContradictsTheRanking()
    {
        var ranks = new Dictionary<ModelId, int> { [Fast] = 1, [Slow] = 6 };

        var found = BlindPickLedger.Divergences([
            Vote("d1", Slow, Fast),
            Vote("d2", Slow, Fast),
            Vote("d3", Fast, Slow),
        ], ranks);

        var d = Assert.Single(found);
        Assert.Equal(Slow, d.PreferredModelId);
        Assert.Equal(6, d.PreferredRank);
        Assert.Equal(Fast, d.RankedModelId);
        Assert.Equal(1, d.RankedRank);
        Assert.Equal(2, d.PreferredWins);
        Assert.Equal(1, d.RankedWins);
    }

    /// <summary>Agreeing with the leaderboard is not a divergence.</summary>
    [Fact]
    public void Divergences_IsSilentWhenThePreferenceMatchesTheRanking()
    {
        var ranks = new Dictionary<ModelId, int> { [Fast] = 1, [Slow] = 6 };

        Assert.Empty(BlindPickLedger.Divergences([Vote("d1", Fast, Slow)], ranks));
    }

    /// <summary>An even split expresses no preference, so there is nothing to contradict.</summary>
    [Fact]
    public void Divergences_IgnoresAnEvenHeadToHead()
    {
        var ranks = new Dictionary<ModelId, int> { [Fast] = 1, [Slow] = 6 };

        Assert.Empty(BlindPickLedger.Divergences([
            Vote("d1", Slow, Fast),
            Vote("d2", Fast, Slow),
        ], ranks));
    }

    /// <summary>
    /// An unranked model has nothing to disagree with — a model that has never been judged
    /// outside blind mode carries no leaderboard position at all.
    /// </summary>
    [Fact]
    public void Divergences_SkipsPairsWhereEitherModelIsUnranked()
    {
        var ranks = new Dictionary<ModelId, int> { [Fast] = 1 };

        Assert.Empty(BlindPickLedger.Divergences([Vote("d1", Slow, Fast)], ranks));
    }

    [Fact]
    public void Divergences_OrdersTheHighestRatedContradictionFirst()
    {
        var ranks = new Dictionary<ModelId, int> { [Fast] = 1, [Third] = 3, [Slow] = 9 };

        var found = BlindPickLedger.Divergences([
            Vote("d1", Slow, Third),   // beat the #3 model
            Vote("d2", Slow, Fast),    // beat the #1 model
        ], ranks);

        Assert.Equal(2, found.Count);
        Assert.Equal(Fast, found[0].RankedModelId);
        Assert.Equal(Third, found[1].RankedModelId);
    }
}
