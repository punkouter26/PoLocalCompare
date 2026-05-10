// SOLID: Single Responsibility — pure ELO formula only
namespace PoLocalCompare.Domain.Services;

public static class EloCalculator
{
    /// <summary>
    /// Calculates updated ELO ratings for both players after a match.
    /// Standard ELO formula: E_a = 1/(1+10^((Rb-Ra)/400)), R'_a = Ra + K*(Sa - Ea)
    /// </summary>
    /// <param name="ratingA">Current ELO of player A (winner if outcomeA = 1)</param>
    /// <param name="ratingB">Current ELO of player B</param>
    /// <param name="k">K-factor controlling rating change magnitude</param>
    /// <param name="outcomeA">Score for player A: 1.0 = win, 0.0 = loss</param>
    /// <returns>New ELO ratings rounded to 1 decimal place</returns>
    public static (double NewRatingA, double NewRatingB) Calculate(
        double ratingA,
        double ratingB,
        double k,
        double outcomeA)
    {
        double expectedA = 1.0 / (1.0 + Math.Pow(10, (ratingB - ratingA) / 400.0));
        double expectedB = 1.0 - expectedA;
        double outcomeB = 1.0 - outcomeA;

        double newRatingA = Math.Round(ratingA + k * (outcomeA - expectedA), 1);
        double newRatingB = Math.Round(ratingB + k * (outcomeB - expectedB), 1);

        return (newRatingA, newRatingB);
    }
}
