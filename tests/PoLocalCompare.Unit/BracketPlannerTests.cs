using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Unit;

public class BracketPlannerTests
{
    /// <summary>Contenders named by seed, strongest first — "S1" is the top seed.</summary>
    private static List<BracketSlot> Contenders(int size) =>
        Enumerable.Range(1, size)
            .Select(n => new BracketSlot(ModelId.From($"model-{n}"), $"S{n}"))
            .ToList();

    // ── Sizes ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    public void RoundCount_IsLogTwoOfTheField(int size, int expected)
    {
        Assert.Equal(expected, BracketPlanner.RoundCount(size));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(16)]
    public void UnsupportedSizes_AreRejected(int size)
    {
        Assert.False(BracketPlanner.IsSupportedSize(size));
        Assert.Throws<ArgumentOutOfRangeException>(() => BracketPlanner.RoundCount(size));
    }

    /// <summary>The last round is "Final" whatever the bracket size, because it counts back.</summary>
    [Theory]
    [InlineData(2, 0, "Final")]
    [InlineData(4, 1, "Final")]
    [InlineData(4, 0, "Semi-finals")]
    [InlineData(8, 2, "Final")]
    [InlineData(8, 1, "Semi-finals")]
    [InlineData(8, 0, "Quarter-finals")]
    public void RoundName_CountsBackFromTheFinal(int size, int round, string expected)
    {
        Assert.Equal(expected, BracketPlanner.RoundName(round, size));
    }

    // ── Seeding ───────────────────────────────────────────────────────────

    [Fact]
    public void SeedOrder_ForEight_IsTheStandardLayout()
    {
        Assert.Equal([1, 8, 4, 5, 2, 7, 3, 6], BracketPlanner.SeedOrder(8));
    }

    [Fact]
    public void SeedOrder_ForFour_IsTheStandardLayout()
    {
        Assert.Equal([1, 4, 2, 3], BracketPlanner.SeedOrder(4));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void SeedOrder_UsesEverySeedExactlyOnce(int size)
    {
        var order = BracketPlanner.SeedOrder(size);
        Assert.Equal(size, order.Count);
        Assert.Equal(Enumerable.Range(1, size), order.OrderBy(x => x));
    }

    /// <summary>
    /// The property the whole seeding exists for: a random draw would routinely knock the two
    /// best models out against each other in round one, producing a winner that says nothing.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void TopTwoSeeds_CannotMeetBeforeTheFinal(int size)
    {
        var bracket = BracketPlanner.Build(Contenders(size), size);
        var finalRound = BracketPlanner.RoundCount(size) - 1;

        // Play the bracket through with the better seed always winning.
        var current = bracket;
        for (var round = 0; round < finalRound; round++)
        {
            foreach (var match in current.Where(m => m.Round == round).ToList())
            {
                var winner = Seed(match.SlotA) < Seed(match.SlotB) ? match.SlotA : match.SlotB;
                current = BracketPlanner.Advance(current, match.Round, match.Index, winner, size);
            }
        }

        var decider = current.Single(m => m.Round == finalRound);
        Assert.Equal([1, 2], new[] { Seed(decider.SlotA), Seed(decider.SlotB) }.OrderBy(x => x));
    }

    [Fact]
    public void FirstRound_PitsTheTopSeedAgainstTheBottomSeed()
    {
        var bracket = BracketPlanner.Build(Contenders(8), 8);
        var opener = bracket.Single(m => m is { Round: 0, Index: 0 });

        Assert.Equal(1, Seed(opener.SlotA));
        Assert.Equal(8, Seed(opener.SlotB));
    }

    // ── Shape ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(8, 7)]
    public void Build_ProducesOneMatchLessThanTheField(int size, int expected)
    {
        Assert.Equal(expected, BracketPlanner.Build(Contenders(size), size).Count);
    }

    [Fact]
    public void Build_LeavesLaterRoundsEmptyUntilWinnersArrive()
    {
        var bracket = BracketPlanner.Build(Contenders(8), 8);

        Assert.All(bracket.Where(m => m.Round == 0), m => Assert.True(m.IsReady));
        Assert.All(bracket.Where(m => m.Round > 0), m => Assert.False(m.IsReady));
    }

    [Fact]
    public void Build_RejectsAFieldThatDoesNotMatchTheSize()
    {
        Assert.Throws<ArgumentException>(() => BracketPlanner.Build(Contenders(4), 8));
    }

    // ── Advancing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The parity rule is what keeps a bracket readable: the winner of the top match has to
    /// stay on top rather than being appended wherever there is room.
    /// </summary>
    [Fact]
    public void NextSlot_SendsEvenMatchesToSlotAAndOddToSlotB()
    {
        Assert.Equal((1, 0, true), BracketPlanner.NextSlot(0, 0, 8));
        Assert.Equal((1, 0, false), BracketPlanner.NextSlot(0, 1, 8));
        Assert.Equal((1, 1, true), BracketPlanner.NextSlot(0, 2, 8));
        Assert.Equal((1, 1, false), BracketPlanner.NextSlot(0, 3, 8));
    }

    [Fact]
    public void NextSlot_OfTheFinalIsNothing()
    {
        Assert.Null(BracketPlanner.NextSlot(2, 0, 8));
        Assert.Null(BracketPlanner.NextSlot(0, 0, 2));
    }

    [Fact]
    public void Advance_PlacesTheWinnerInTheNextRound()
    {
        var bracket = BracketPlanner.Build(Contenders(4), 4);
        var winner = bracket.Single(m => m is { Round: 0, Index: 1 }).SlotA;

        var advanced = BracketPlanner.Advance(bracket, 0, 1, winner, 4);

        Assert.Equal(winner, advanced.Single(m => m.Round == 1).SlotB);
    }

    [Fact]
    public void Advance_FromTheFinalChangesNothing()
    {
        var bracket = BracketPlanner.Build(Contenders(2), 2);
        var winner = bracket[0].SlotA;

        Assert.Equal(bracket, BracketPlanner.Advance(bracket, 0, 0, winner, 2));
    }

    private static int Seed(BracketSlot slot) => int.Parse(slot.DisplayName[1..]);
}
