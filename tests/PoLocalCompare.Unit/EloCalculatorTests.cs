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
    public void Calculate_EqualRatings_ShiftIsHalfK(double k)
    {
        var (newA, newB) = EloCalculator.Calculate(1200, 1200, k, outcomeA: 1.0);

        var expectedShift = Math.Round(k * 0.5, 1);
        Assert.Equal(expectedShift, Math.Round(newA - 1200, 1));
        Assert.Equal(-expectedShift, Math.Round(newB - 1200, 1));
    }

    // ── Expectation weighting: a heavy favourite gains little, an upset moves hard ──

    [Theory]
    [InlineData(2000, 1400, true)]   // expected winner — tiny gain
    [InlineData(1400, 2000, true)]   // upset — large gain
    public void Calculate_ShiftScalesWithHowUnexpectedTheResultWas(double ra, double rb, bool aWins)
    {
        var outcomeA = aWins ? 1.0 : 0.0;
        var (newA, _) = EloCalculator.Calculate(ra, rb, k: 32, outcomeA);

        var shift = Math.Abs(newA - ra);
        if (aWins && ra > rb)
            Assert.True(shift is > 0.0 and <= 1.0, $"Expected an at-most 1-point shift for a heavy favourite, got {shift:F1}.");
        else
            Assert.True(shift > 20.0, $"Expected a large shift for an upset win, got {shift:F1}.");
    }

    // ── Rounding to 1 decimal place ────────────────────────────────────────

    // ── Rounding to 1 decimal place ────────────────────────────────────────

    [Fact]
    public void Calculate_ResultsAreRoundedToOneDecimalPlace()
    {
        var (newA, newB) = EloCalculator.Calculate(1205, 1198, k: 32, outcomeA: 1.0);

        Assert.Equal(newA, Math.Round(newA, 1));
        Assert.Equal(newB, Math.Round(newB, 1));
    }

    // ── Zero-sum property ─────────────────────────────────────────────────

    [Theory]
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
