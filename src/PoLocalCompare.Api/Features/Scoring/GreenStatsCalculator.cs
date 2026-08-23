// SOLID: Single Responsibility
namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Pure arithmetic energy calculator — no I/O, no external dependencies.
/// Computes energy consumption and cost for local model duel results.
/// </summary>
/// <remarks>
/// It also used to compute a "Green Score" (tokens per watt-hour), which was carried through
/// the result entity, the model aggregate, three DTOs, the leaderboard and head-to-head
/// handlers and the lab report — and rendered nowhere a person would find it. Energy and cost
/// stayed because the telemetry panel actually shows them.
/// </remarks>
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
}
