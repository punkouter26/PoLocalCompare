using System.Globalization;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Challenges;

/// <summary>What one side actually measured, and whether that met the budget.</summary>
/// <param name="Measured">
/// Null when the run produced no measurement at all — a model that failed outright. Treated as
/// a miss, because "we never found out" is not "within budget".
/// </param>
public sealed record ChallengeMeasurement(ChallengeKind Kind, double Threshold, double? Measured, bool Met);

/// <summary>How a challenge duel came out once both sides were measured.</summary>
public enum ChallengeOutcome
{
    /// <summary>Both sides stayed inside the budget, so the budget decides nothing.</summary>
    BothMet,

    /// <summary>Only the left model stayed inside the budget.</summary>
    LeftOnly,

    /// <summary>Only the right model stayed inside the budget.</summary>
    RightOnly,

    /// <summary>Neither model stayed inside the budget.</summary>
    NeitherMet,
}

/// <summary>
/// The rules of challenge mode: what a budget means, and what it decides.
/// </summary>
/// <remarks>
/// Pure and in Shared so both halves can use one copy — the server adjudicates with it, and the
/// setup form and the Arena describe budgets with it. Getting those to disagree would mean a
/// duel presented under one rule and judged under another.
///
/// A budget is a hard constraint, not a score: exceeding it loses the match outright regardless
/// of how good the output was. That is the whole point — it asks "which model can do this
/// within the budget", which is a different question from "which output is better", and it is
/// why challenge results are ranked separately from ELO rather than folded into it.
/// </remarks>
public static class ChallengeRules
{
    /// <summary>
    /// Whether a measurement satisfies the budget. Every kind is a ceiling, so the test is
    /// always "at or under" — and a missing measurement always fails.
    /// </summary>
    public static bool Meets(double? measured, double threshold) =>
        measured.HasValue && measured.Value <= threshold;

    /// <summary>
    /// Measures one side. <paramref name="failed"/> short-circuits to a miss: a model that
    /// crashed has a stored duration, and counting that as "finished in 0.4 seconds" would let
    /// failing fast win a speed challenge.
    /// </summary>
    /// <param name="isMetered">
    /// Whether this model bills for tokens at all. Distinguishes "ran for free" from "we have
    /// no rate for it" — see the cost branch below, where conflating the two hands wins to the
    /// most expensive models in the catalog.
    /// </param>
    public static ChallengeMeasurement Measure(
        ChallengeKind kind,
        double threshold,
        bool failed,
        long totalDurationMs,
        double? apiCostUsd,
        int tokenCount,
        bool isMetered = false)
    {
        if (kind == ChallengeKind.None)
            return new ChallengeMeasurement(kind, threshold, null, Met: true);

        double? measured = failed
            ? null
            : kind switch
            {
                ChallengeKind.MaxSeconds => totalDurationMs / 1000.0,
                ChallengeKind.MaxCostUsd => CostOf(apiCostUsd, isMetered),
                ChallengeKind.MaxTokens => tokenCount,
                _ => null,
            };

        return new ChallengeMeasurement(kind, threshold, measured, Meets(measured, threshold));
    }

    /// <summary>
    /// What a run cost, or null when that is genuinely unknown.
    /// </summary>
    /// <remarks>
    /// The distinction this draws is load-bearing, and getting it wrong inverts the whole mode.
    ///
    /// A model that runs <em>on this machine</em> — in the browser on WebGPU, or through a
    /// local Ollama service — bills nothing, so a null cost means zero and it passes any budget.
    /// That is correct and it is why local models can compete in a cost challenge at all.
    ///
    /// A <em>metered</em> model with a null cost is a different thing entirely: it billed
    /// something, we just have no rate on file for it. Reading that as zero is how the most
    /// expensive deployments in the catalog end up winning cost challenges outright — which is
    /// exactly what happened while seven of eleven remote models carried no pricing. Unknown is
    /// a miss, because a budget you cannot verify has not been met.
    /// </remarks>
    private static double? CostOf(double? apiCostUsd, bool isMetered)
    {
        if (apiCostUsd.HasValue) return apiCostUsd.Value;
        return isMetered ? null : 0;
    }

    /// <summary>What the budget decides, given both sides' measurements.</summary>
    public static ChallengeOutcome Adjudicate(ChallengeMeasurement left, ChallengeMeasurement right) =>
        (left.Met, right.Met) switch
        {
            (true, true) => ChallengeOutcome.BothMet,
            (true, false) => ChallengeOutcome.LeftOnly,
            (false, true) => ChallengeOutcome.RightOnly,
            _ => ChallengeOutcome.NeitherMet,
        };

    /// <summary>Short label for a budget, e.g. "under 5s" or "under $0.0020".</summary>
    public static string Describe(ChallengeKind kind, double threshold) => kind switch
    {
        ChallengeKind.MaxSeconds => $"under {Format(kind, threshold)}",
        ChallengeKind.MaxCostUsd => $"under {Format(kind, threshold)}",
        ChallengeKind.MaxTokens => $"under {Format(kind, threshold)}",
        _ => "no budget",
    };

    /// <summary>Renders a measured value or a threshold in the units of its kind.</summary>
    public static string Format(ChallengeKind kind, double? value)
    {
        if (value is not { } v) return "—";

        return kind switch
        {
            ChallengeKind.MaxSeconds => v.ToString("0.#", CultureInfo.InvariantCulture) + "s",
            ChallengeKind.MaxCostUsd => "$" + v.ToString("0.0000", CultureInfo.InvariantCulture),
            ChallengeKind.MaxTokens => v.ToString("N0", CultureInfo.InvariantCulture) + " tokens",
            _ => v.ToString("0.##", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Human name for the axis a budget constrains.</summary>
    public static string KindLabel(ChallengeKind kind) => kind switch
    {
        ChallengeKind.MaxSeconds => "Time",
        ChallengeKind.MaxCostUsd => "Cost",
        ChallengeKind.MaxTokens => "Tokens",
        _ => "None",
    };

    /// <summary>
    /// The budgets the setup form offers, per kind. Presets rather than a free number field:
    /// a meaningful ceiling depends on units most people do not carry in their head, and
    /// "$0.002" typed into a box that wanted seconds is a silently absurd challenge.
    /// </summary>
    public static IReadOnlyList<double> PresetsFor(ChallengeKind kind) => kind switch
    {
        ChallengeKind.MaxSeconds => [5, 10, 20, 45],
        ChallengeKind.MaxCostUsd => [0.0005, 0.001, 0.002, 0.005],
        ChallengeKind.MaxTokens => [500, 1000, 2000, 4000],
        _ => [],
    };

    /// <summary>Rejects a budget that no run could satisfy, or that every run trivially would.</summary>
    public static bool IsValidThreshold(ChallengeKind kind, double threshold) =>
        kind == ChallengeKind.None || threshold > 0;
}
