using PoLocalCompare.Api.Features.Leaderboard;

namespace PoLocalCompare.Unit;

/// <summary>
/// The leaderboard's "W/L/T (Win %)" column reads <see cref="WinRateCalculator.Calculate"/>.
/// These tests pin the four cases the projection has to handle, including the
/// never-competed branch (must be 0, not NaN, so the column renders).
/// </summary>
public class WinRateCalculatorTests
{
    /// <summary>
    /// Every branch the DTO has to render. The DTO's WinRate is a plain double — NaN would
    /// print as "NaN%" on the leaderboard, which is why "never competed" must explicitly be 0.
    /// </summary>
    [Theory]
    [InlineData(17, 19, 17.0 / 19.0)]    // typical record
    [InlineData(12, 12, 1.0)]           // all wins
    [InlineData(0,  5,  0.0)]           // some losses, no wins
    [InlineData(0,  0,  0.0)]           // never competed — never NaN
    [InlineData(0, -1,  0.0)]           // defensive clamp on negative input
    [InlineData(0,  8,  0.0)]           // all draws are not wins
    public void Calculate_MapsEveryBranchTheLeaderboardRenders(int wins, int duels, double expected)
    {
        Assert.Equal(expected, WinRateCalculator.Calculate(winCount: wins, duelCount: duels), 10);
    }
}