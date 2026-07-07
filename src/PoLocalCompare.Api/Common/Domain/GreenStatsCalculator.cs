// SOLID: Single Responsibility
namespace PoLocalCompare.Domain.Services;

/// <summary>
/// Pure arithmetic Green Stats calculator — no I/O, no external dependencies.
/// Computes energy consumption, cost, and green score for local model duel results.
/// </summary>
public static class GreenStatsCalculator
{
    /// <summary>
    /// Computes energy consumed in watt-hours given TDP and duration.
    /// </summary>
    /// <param name="tdpWatts">Thermal Design Power in watts.</param>
    /// <param name="totalDurationMs">Total inference duration in milliseconds.</param>
    /// <returns>Energy in watt-hours.</returns>
    public static double ComputeEnergyWh(double tdpWatts, long totalDurationMs)
    {
        var hours = totalDurationMs / 3_600_000.0;
        return Math.Round(tdpWatts * hours, 6);
    }

    /// <summary>
    /// Computes energy cost in USD given energy and electricity rate.
    /// </summary>
    /// <param name="energyWh">Energy in watt-hours.</param>
    /// <param name="rateUsdPerKwh">Electricity rate in USD per kWh.</param>
    /// <returns>Cost in USD.</returns>
    public static double ComputeEnergyCostUsd(double energyWh, double rateUsdPerKwh)
    {
        return Math.Round(energyWh / 1000.0 * rateUsdPerKwh, 8);
    }

    /// <summary>
    /// Computes the Green Score as tokens generated per watt-hour.
    /// Higher is better — more output per unit of energy.
    /// </summary>
    /// <param name="tokenCount">Number of tokens generated.</param>
    /// <param name="energyWh">Energy consumed in watt-hours.</param>
    /// <returns>Green Score (tokens/Wh). Returns 0 if energyWh is zero.</returns>
    public static double ComputeGreenScore(int tokenCount, double energyWh)
    {
        if (energyWh <= 0) return 0;
        return Math.Round(tokenCount / energyWh, 2);
    }
}
