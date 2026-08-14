using PoLocalCompare.Api.Common.Domain;

namespace PoLocalCompare.Unit;

/// <summary>
/// The leaderboard's "W/L/T (Win %)" column reads <see cref="WinRateCalculator.Calculate"/>.
/// These tests pin the four cases the projection has to handle, including the
/// never-competed branch (must be 0, not NaN, so the column renders).
/// </summary>
public class WinRateCalculatorTests
{
    [Fact]
    public void Calculate_TypicalRecord_ReturnsCorrectRatio()
    {
        // 17 wins out of 19 judged duels → 0.8947…
        Assert.Equal(17.0 / 19.0, WinRateCalculator.Calculate(winCount: 17, duelCount: 19), 10);
    }

    [Fact]
    public void Calculate_AllWins_ReturnsOne()
    {
        Assert.Equal(1.0, WinRateCalculator.Calculate(winCount: 12, duelCount: 12), 10);
    }

    [Fact]
    public void Calculate_NoWins_ReturnsZero()
    {
        // 0 wins / 5 duels — common for newly-added models with only losses.
        Assert.Equal(0.0, WinRateCalculator.Calculate(winCount: 0, duelCount: 5), 10);
    }

    [Fact]
    public void Calculate_NeverCompeted_ReturnsZeroNotNaN()
    {
        // The DTO's WinRate is rendered directly; NaN would print as "NaN%" on the page.
        Assert.Equal(0.0, WinRateCalculator.Calculate(winCount: 0, duelCount: 0));
        Assert.Equal(0.0, WinRateCalculator.Calculate(winCount: 0, duelCount: -1)); // defensive
    }

    [Fact]
    public void Calculate_AllDraws_ReturnsZero()
    {
        // Draws aren't wins. A model that drew every duel has 0% even though W/L/T reads 0/0/N.
        Assert.Equal(0.0, WinRateCalculator.Calculate(winCount: 0, duelCount: 8));
    }
}