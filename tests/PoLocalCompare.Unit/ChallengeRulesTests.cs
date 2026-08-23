using PoLocalCompare.Shared.Challenges;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Unit;

public class ChallengeRulesTests
{
    private static ChallengeMeasurement Seconds(double threshold, double measured, bool failed = false) =>
        ChallengeRules.Measure(ChallengeKind.MaxSeconds, threshold, failed,
            totalDurationMs: (long)(measured * 1000), apiCostUsd: null, tokenCount: 0);

    // ── Meets ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4.0, 5.0, true)]
    [InlineData(5.0, 5.0, true)]    // a ceiling is inclusive
    [InlineData(5.1, 5.0, false)]
    [InlineData(null,  5.0, false)] // "we never found out" is not "within budget"
    public void Meets_TreatsTheBudgetAsAnInclusiveCeiling(double? measured, double threshold, bool expected)
    {
        Assert.Equal(expected, ChallengeRules.Meets(measured, threshold));
    }

    // ── Measure ───────────────────────────────────────────────────────────

    /// <summary>
    /// Each kind reads its own field and applies the same inclusive ceiling rule. A failed
    /// run clears the measurement so the next test's assertion can't be confused by it.
    /// </summary>
    [Theory]
    [InlineData(ChallengeKind.MaxSeconds, 5.0, true, 4.2)]   // seconds under a 5s ceiling
    [InlineData(ChallengeKind.MaxTokens, 1000.0, true, 820)]  // 820 tokens under a 1000 budget
    [InlineData(ChallengeKind.MaxCostUsd, 0.001, false, 0.0025)] // $0.0025 over a $0.001 budget
    public void Measure_ReadsItsOwnFieldAndAppliesTheInclusiveCeiling(
        ChallengeKind kind, double threshold, bool expectedMet, double measured)
    {
        var m = ChallengeRules.Measure(kind, threshold, failed: false,
            totalDurationMs: (long)(measured * 1000),
            apiCostUsd: kind == ChallengeKind.MaxCostUsd ? measured : (double?)null,
            tokenCount: kind == ChallengeKind.MaxTokens ? (int)measured : 0);

        Assert.Equal(expectedMet, m.Met);
    }

    /// <summary>
    /// The failure case that would otherwise break the whole mode: a model that crashes has a
    /// short stored duration, so counting it would make failing fast the winning speed strategy.
    /// </summary>
    [Fact]
    public void Measure_AFailedRunNeverMeetsTheBudget()
    {
        var m = Seconds(threshold: 5, measured: 0.4, failed: true);

        Assert.Null(m.Measured);
        Assert.False(m.Met);
    }

    /// <summary>
    /// An unpriced model is genuinely free rather than unmeasured, so it passes a cost budget.
    /// Reading the null price as "no measurement" would disqualify every local model from every
    /// cost challenge.
    /// </summary>
    [Fact]
    public void Measure_AnUnmeteredModelWithNoCostSpendsNothing()
    {
        // A browser or Ollama model runs on the user's own hardware, so a null cost genuinely
        // means zero and it passes any budget. This is what lets local models compete in a
        // cost challenge at all.
        var m = ChallengeRules.Measure(ChallengeKind.MaxCostUsd, 0.002, failed: false,
            totalDurationMs: 3000, apiCostUsd: null, tokenCount: 900, isMetered: false);

        Assert.Equal(0, m.Measured);
        Assert.True(m.Met);
    }

    [Fact]
    public void Measure_AMeteredModelWithNoRateIsAMissNotFree()
    {
        // The counterpart, and the one that actually bites: a remote model billed us something,
        // we just have no price on file for it. Reading that as zero is how the most expensive
        // deployments in the catalog win cost challenges outright — which is exactly what
        // happened while seven of eleven remote models carried no pricing. A budget that cannot
        // be verified has not been met.
        var m = ChallengeRules.Measure(ChallengeKind.MaxCostUsd, 0.002, failed: false,
            totalDurationMs: 3000, apiCostUsd: null, tokenCount: 900, isMetered: true);

        Assert.Null(m.Measured);
        Assert.False(m.Met);
    }

    /// <summary>An ordinary duel carries no budget and must never be reported as failing one.</summary>
    [Fact]
    public void Measure_WithNoBudget_AlwaysMeets()
    {
        var m = ChallengeRules.Measure(ChallengeKind.None, 0, failed: true,
            totalDurationMs: 999_999, apiCostUsd: 9.99, tokenCount: 99_999);

        Assert.True(m.Met);
    }

    // ── Adjudicate ────────────────────────────────────────────────────────

    [Fact]
    public void Adjudicate_OneSideInsideTheBudget_WinsOutright()
    {
        Assert.Equal(
            ChallengeOutcome.LeftOnly,
            ChallengeRules.Adjudicate(Seconds(5, 4.0), Seconds(5, 8.7)));

        Assert.Equal(
            ChallengeOutcome.RightOnly,
            ChallengeRules.Adjudicate(Seconds(5, 8.7), Seconds(5, 4.0)));
    }

    /// <summary>
    /// Both inside the budget means the budget decides nothing — the duel falls through to
    /// being judged on quality like any other.
    /// </summary>
    [Fact]
    public void Adjudicate_BothInsideTheBudget_DecidesNothing()
    {
        Assert.Equal(
            ChallengeOutcome.BothMet,
            ChallengeRules.Adjudicate(Seconds(10, 4.0), Seconds(10, 8.7)));
    }

    [Fact]
    public void Adjudicate_NeitherInsideTheBudget_IsItsOwnOutcome()
    {
        Assert.Equal(
            ChallengeOutcome.NeitherMet,
            ChallengeRules.Adjudicate(Seconds(3, 4.0), Seconds(3, 8.7)));
    }

    // ── Presentation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChallengeKind.MaxSeconds, 5.0, "5s")]
    [InlineData(ChallengeKind.MaxCostUsd, 0.002, "$0.0020")]
    [InlineData(ChallengeKind.MaxTokens, 1000.0, "1,000 tokens")]
    public void Format_RendersInTheUnitsOfItsKind(ChallengeKind kind, double value, string expected)
    {
        Assert.Equal(expected, ChallengeRules.Format(kind, value));
    }

    [Fact]
    public void Format_OfNothingIsADash()
    {
        Assert.Equal("—", ChallengeRules.Format(ChallengeKind.MaxSeconds, null));
    }

    [Fact]
    public void Describe_ReadsAsACeiling()
    {
        Assert.Equal("under 5s", ChallengeRules.Describe(ChallengeKind.MaxSeconds, 5));
        Assert.Equal("no budget", ChallengeRules.Describe(ChallengeKind.None, 0));
    }

    [Theory]
    [InlineData(ChallengeKind.MaxSeconds)]
    [InlineData(ChallengeKind.MaxCostUsd)]
    [InlineData(ChallengeKind.MaxTokens)]
    public void PresetsFor_EveryRealKind_AreAscendingAndPositive(ChallengeKind kind)
    {
        var presets = ChallengeRules.PresetsFor(kind);

        Assert.NotEmpty(presets);
        Assert.All(presets, p => Assert.True(p > 0));
        Assert.Equal(presets.OrderBy(p => p), presets);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(0.5, true)]
    public void IsValidThreshold_RejectsANonPositiveCeiling(double threshold, bool expected)
    {
        Assert.Equal(expected, ChallengeRules.IsValidThreshold(ChallengeKind.MaxSeconds, threshold));
    }
}
