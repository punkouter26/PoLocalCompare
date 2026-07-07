using PoLocalCompare.Domain.Services;

namespace PoLocalCompare.Unit.Tests.Domain;

public class EloCalculatorTests
{
    // ── Standard formula: winner always gains, loser always loses ──────────

    [Fact]
    public void Calculate_WinForA_AIncreasesAndBDecreases()
    {
        var (newA, newB) = EloCalculator.Calculate(1200, 1200, k: 32, outcomeA: 1.0);

        Assert.True(newA > 1200, "Winner ELO should increase.");
        Assert.True(newB < 1200, "Loser ELO should decrease.");
    }

    [Fact]
    public void Calculate_LossForA_ADecreasesAndBIncreases()
    {
        var (newA, newB) = EloCalculator.Calculate(1200, 1200, k: 32, outcomeA: 0.0);

        Assert.True(newA < 1200, "Loser ELO should decrease.");
        Assert.True(newB > 1200, "Winner ELO should increase.");
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

    // ── K-factor parametrisation ───────────────────────────────────────────

    [Fact]
    public void Calculate_K32_ProducesLargerShiftThanK16()
    {
        var (newA32, _) = EloCalculator.Calculate(1200, 1200, k: 32, outcomeA: 1.0);
        var (newA16, _) = EloCalculator.Calculate(1200, 1200, k: 16, outcomeA: 1.0);

        Assert.True(newA32 - 1200 > newA16 - 1200, "K=32 should produce a larger rating shift than K=16.");
    }

    // ── Strong favourite: sub-1-point expected shift ───────────────────────

    [Fact]
    public void Calculate_HighRatedWinsAgainstLow_ShiftIsLessThan1Point()
    {
        // A is 600 ELO points stronger — expected to win, so shift is tiny
        var (newA, _) = EloCalculator.Calculate(ratingA: 2000, ratingB: 1400, k: 32, outcomeA: 1.0);

        var shift = newA - 2000;
        Assert.True(shift <= 1.0, $"Expected at-most 1-point rounded shift for a heavy favourite, got {shift:F1}.");
        Assert.True(shift > 0.0, "Winner should still gain rating points.");
        Assert.Equal(Math.Round(shift, 1), shift); // 1 decimal place
    }

    [Fact]
    public void Calculate_LowRatedWinsAgainstHigh_ShiftIsLargerthan20Points()
    {
        // A is 600 ELO points weaker — upset win gives large shift
        var (newA, _) = EloCalculator.Calculate(ratingA: 1400, ratingB: 2000, k: 32, outcomeA: 1.0);

        var shift = newA - 1400;
        Assert.True(shift > 20.0, $"Expected large shift for an upset win, got {shift:F1}.");
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
