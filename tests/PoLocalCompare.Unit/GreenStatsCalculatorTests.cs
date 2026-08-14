using PoLocalCompare.Api.Features.Duels;

namespace PoLocalCompare.Unit;

/// <summary>
/// Two arithmetic conversions. Pinning the formula with known values covers the linearity and
/// zero cases that used to have a method each — if a scale factor were wrong, the known values
/// would be the assertion that caught it.
/// </summary>
public class GreenStatsCalculatorTests
{
    [Theory]
    [InlineData(1, 3_600_000, 1.0)]      // one hour at one watt is one watt-hour
    [InlineData(115, 30_000, 0.958333)]
    [InlineData(65, 10_000, 0.180556)]
    [InlineData(250, 5_000, 0.347222)]
    [InlineData(115, 0, 0)]              // no time, no energy
    public void ComputeEnergyWh_KnownValues(double tdp, long ms, double expected)
    {
        Assert.Equal(expected, GreenStatsCalculator.ComputeEnergyWh(tdp, ms), precision: 6);
    }

    [Fact]
    public void ComputeEnergyWh_RoundsToSixDecimals()
    {
        // 1 ms at 1 W is 2.777…e-7 Wh, below the rounding floor — it must not report a
        // spurious long tail.
        var result = GreenStatsCalculator.ComputeEnergyWh(1, 1);

        Assert.Equal(Math.Round(result, 6), result);
    }

    [Theory]
    [InlineData(1000, 0.12, 0.12)]   // a full kilowatt-hour costs the full rate
    [InlineData(0, 0.12, 0)]         // no energy is free
    [InlineData(5000, 0, 0)]         // a zero rate is free
    public void ComputeEnergyCostUsd_KnownValues(double energyWh, double rateUsdPerKwh, double expected)
    {
        Assert.Equal(expected, GreenStatsCalculator.ComputeEnergyCostUsd(energyWh, rateUsdPerKwh), precision: 8);
    }
}
