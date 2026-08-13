using PoLocalCompare.Api.Common.Domain;

namespace PoLocalCompare.Unit;

public class GreenStatsCalculatorTests
{
    // ── ComputeEnergyWh ────────────────────────────────────────────────────

    [Fact]
    public void ComputeEnergyWh_OneHourAtOneWatt_IsOneWattHour()
    {
        Assert.Equal(1.0, GreenStatsCalculator.ComputeEnergyWh(tdpWatts: 1, totalDurationMs: 3_600_000));
    }

    [Fact]
    public void ComputeEnergyWh_ScalesLinearlyWithDuration()
    {
        var oneMinute = GreenStatsCalculator.ComputeEnergyWh(115, 60_000);
        var twoMinutes = GreenStatsCalculator.ComputeEnergyWh(115, 120_000);

        // Precision 5, not 6: the function rounds to 6 dp, so doubling an already-rounded
        // value can differ from the rounded double in the last place.
        Assert.Equal(oneMinute * 2, twoMinutes, precision: 5);
    }

    [Fact]
    public void ComputeEnergyWh_ScalesLinearlyWithTdp()
    {
        var low = GreenStatsCalculator.ComputeEnergyWh(50, 60_000);
        var high = GreenStatsCalculator.ComputeEnergyWh(100, 60_000);

        Assert.Equal(low * 2, high, precision: 5);
    }

    [Fact]
    public void ComputeEnergyWh_ZeroDuration_IsZero()
    {
        Assert.Equal(0, GreenStatsCalculator.ComputeEnergyWh(115, 0));
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
    [InlineData(115, 30_000, 0.958333)]
    [InlineData(65, 10_000, 0.180556)]
    [InlineData(250, 5_000, 0.347222)]
    public void ComputeEnergyWh_KnownValues(double tdp, long ms, double expected)
    {
        Assert.Equal(expected, GreenStatsCalculator.ComputeEnergyWh(tdp, ms), precision: 6);
    }

    // ── ComputeEnergyCostUsd ───────────────────────────────────────────────

    [Fact]
    public void ComputeEnergyCostUsd_OneKilowattHour_CostsTheFullRate()
    {
        Assert.Equal(0.12, GreenStatsCalculator.ComputeEnergyCostUsd(energyWh: 1000, rateUsdPerKwh: 0.12), precision: 8);
    }

    [Fact]
    public void ComputeEnergyCostUsd_ZeroEnergy_IsFree()
    {
        Assert.Equal(0, GreenStatsCalculator.ComputeEnergyCostUsd(0, 0.12));
    }

    [Fact]
    public void ComputeEnergyCostUsd_ZeroRate_IsFree()
    {
        Assert.Equal(0, GreenStatsCalculator.ComputeEnergyCostUsd(5000, 0));
    }

    [Fact]
    public void ComputeEnergyCostUsd_ScalesLinearlyWithRate()
    {
        var cheap = GreenStatsCalculator.ComputeEnergyCostUsd(1000, 0.10);
        var pricey = GreenStatsCalculator.ComputeEnergyCostUsd(1000, 0.30);

        Assert.Equal(cheap * 3, pricey, precision: 8);
    }
}
