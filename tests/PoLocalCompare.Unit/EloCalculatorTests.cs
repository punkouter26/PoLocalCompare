using PoLocalCompare.Api.Features.Leaderboard;

namespace PoLocalCompare.Unit;

public class EloCalculatorTests
{
    // ── Standard formula: winner always gains, loser always loses ──────────

    [Fact]
    public void Calculate_WinnerGainsAndLoserLoses_WhicheverSideWon()
    {
        var (winA, loseB) = EloCalculator.Calculate(1200, 1200, k: 32, outcomeA: 1.0);
        Assert.True(winA > 1200, "Winner ELO should increase.");
        Assert.True(loseB < 1200, "Loser ELO should decrease.");

        var (loseA, winB) = EloCalculator.Calculate(1200, 1200, k: 32, outcomeA: 0.0);
        Assert.True(loseA < 1200, "Loser ELO should decrease.");
        Assert.True(winB > 1200, "Winner ELO should increase.");
    }

    // ── Equal ratings: expected score = 0.5 → shift = K * 0.5 ────────────

    [Theory]
    [InlineData(32)]
    [InlineData(16)]
    public void Calculate_EqualRatings_ShiftIsHalfK(double k)
    {
        var (newA, newB) = EloCalculator.Calculate(1200, 1200, k, outcomeA: 1.0);

        var expectedShift = Math.Round(k * 0.5, 1);
        Assert.Equal(expectedShift, Math.Round(newA - 1200, 1));
        Assert.Equal(-expectedShift, Math.Round(newB - 1200, 1));
    }

    // ── Expectation weighting: the favourite gains little, the upset gains a lot ──

    [Fact]
    public void Calculate_ShiftScalesWithHowUnexpectedTheResultWas()
    {
        // A is 600 ELO points stronger and wins as expected — a tiny, still-positive shift.
        var (favourite, _) = EloCalculator.Calculate(ratingA: 2000, ratingB: 1400, k: 32, outcomeA: 1.0);
        var favouriteShift = favourite - 2000;
        Assert.True(favouriteShift is > 0.0 and <= 1.0,
            $"Expected an at-most 1-point shift for a heavy favourite, got {favouriteShift:F1}.");

        // The same 600-point gap the other way round — an upset moves the rating hard.
        var (underdog, _) = EloCalculator.Calculate(ratingA: 1400, ratingB: 2000, k: 32, outcomeA: 1.0);
        Assert.True(underdog - 1400 > 20.0, $"Expected a large shift for an upset win, got {underdog - 1400:F1}.");
    }

    // ── Rounding to 1 decimal place ────────────────────────────────────────

    [Fact]
    public void Calculate_ResultsAreRoundedToOneDecimalPlace()
    {
        var (newA, newB) = EloCalculator.Calculate(1205, 1198, k: 32, outcomeA: 1.0);

        // Verify no more than 1 dp
        Assert.Equal(newA, Math.Round(newA, 1));
        Assert.Equal(newB, Math.Round(newB, 1));
    }

    // ── Zero-sum property ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1200, 1200, 32, 1.0)]
    [InlineData(1500, 1100, 32, 0.0)]
    [InlineData(900, 1600, 16, 1.0)]
    public void Calculate_RatingChangesAreZeroSum(double ra, double rb, double k, double outcome)
    {
        var (newA, newB) = EloCalculator.Calculate(ra, rb, k, outcome);

        var totalBefore = ra + rb;
        var totalAfter = newA + newB;

        // Due to rounding, allow a tolerance of 0.2 (two 0.1 roundings)
        Assert.True(Math.Abs(totalAfter - totalBefore) <= 0.2,
            $"ELO is not zero-sum: before={totalBefore}, after={totalAfter}, diff={totalAfter - totalBefore}.");
    }
}
