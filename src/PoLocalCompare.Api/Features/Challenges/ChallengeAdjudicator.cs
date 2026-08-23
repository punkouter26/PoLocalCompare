using PoLocalCompare.Shared.Challenges;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Challenges;

/// <summary>
/// Applies a challenge budget to a finished duel, before anything looks at the outputs.
/// </summary>
/// <remarks>
/// Runs ahead of <see cref="AutoJudge"/> rather than inside it, for two reasons. A budget is not
/// an opinion — it is arithmetic over values already on the result rows — so it does not need the
/// judge, and it must keep working with <c>AiJudge:Enabled=false</c>. And when the budget decides
/// the match, calling the LLM at all would be paying for a second opinion that cannot be acted on.
///
/// The three outcomes, and why each is what it is:
///
/// • One side inside the budget — that side wins outright, recorded with
///   <see cref="VerdictSource.Constraint"/>. Nothing read the outputs, and the source says so.
/// • Both inside — the budget separates nothing, so this stands down and the ordinary judge
///   decides on quality. A challenge is a filter on top of a duel, not a replacement for it.
/// • Neither inside — recorded as a tie. Both models are on record as having missed, which is a
///   real result, and no rating moves. Leaving it Pending would be worse: the duel would sit
///   unjudged forever with no way for a human to resolve it honestly either.
/// </remarks>
public sealed class ChallengeAdjudicator(
    IDuelRepository duelRepository,
    IDuelResultRepository duelResultRepository,
    IModelRepository modelRepository,
    RecordVerdictHandler recordVerdict,
    ILogger<ChallengeAdjudicator> logger)
{
    /// <summary>
    /// Measures both sides and records a verdict when the budget decides one. Never throws.
    /// </summary>
    /// <returns>
    /// True when the budget settled the duel, so the caller must not run the LLM judge. False
    /// for an ordinary duel, for a budget that separated nothing, or for any failure — in every
    /// one of those cases the normal judging path is still the right next step.
    /// </returns>
    public async Task<bool> TryAdjudicateAsync(DuelId duelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var duel = await duelRepository.GetByIdAsync(duelId);
            if (duel is null || !duel.IsChallenge) return false;

            // A human who picked inside the window already decided it; there is nothing left
            // for the budget to settle.
            if (duel.Verdict != DuelVerdict.Pending) return false;

            var results = await duelResultRepository.GetByDuelIdAsync(duelId);
            var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
            var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

            // The model rows are needed to tell a free local run from an unpriced metered one.
            var leftModel = await modelRepository.GetByIdAsync(duel.LeftModelId);
            var rightModel = await modelRepository.GetByIdAsync(duel.RightModelId);

            var leftMeasure = MeasureSide(duel, left, leftModel);
            var rightMeasure = MeasureSide(duel, right, rightModel);
            var outcome = ChallengeRules.Adjudicate(leftMeasure, rightMeasure);

            var decision = DecideFrom(outcome, duel, leftMeasure, rightMeasure);
            if (decision is null) return false;

            try
            {
                await recordVerdict.HandleWithRetryAsync(decision);
            }
            catch (InvalidOperationException ex)
            {
                // Someone landed a verdict between the read above and this write. Theirs stands.
                logger.LogInformation(
                    "Challenge adjudication stood down for duel {DuelId}: {Reason}", duelId, ex.Message);
                return true;
            }

            logger.LogInformation(
                "Duel {DuelId} decided by its {Kind} budget: {Verdict}.",
                duelId, duel.ChallengeKind, decision.Verdict);

            return true;
        }
        catch (Exception ex)
        {
            // A broken adjudication must not take duel execution down with it, and must not
            // swallow the duel: returning false hands it to the ordinary judge.
            logger.LogError(ex, "Challenge adjudication failed for duel {DuelId}.", duelId);
            return false;
        }
    }

    private static ChallengeMeasurement MeasureSide(Duel duel, DuelResult? result, Model? model) =>
        ChallengeRules.Measure(
            duel.ChallengeKind,
            duel.ChallengeThreshold,
            // No result row at all is the same as a failed one for this purpose: nothing was
            // measured, so nothing can be said to have come in under budget.
            failed: result is null || result.IsFailure,
            totalDurationMs: result?.TotalDurationMs ?? 0,
            apiCostUsd: result?.ApiCostUsd,
            tokenCount: result?.TokenCount ?? 0,
            // Remote models bill; browser and Ollama models run on the user's own hardware and
            // genuinely cost nothing. A remote model with no recorded cost is unknown, not free.
            isMetered: model?.ModelType == ModelType.Remote);

    private static RecordVerdictCommand? DecideFrom(
        ChallengeOutcome outcome,
        Duel duel,
        ChallengeMeasurement left,
        ChallengeMeasurement right)
    {
        var budget = ChallengeRules.Describe(duel.ChallengeKind, duel.ChallengeThreshold);

        return outcome switch
        {
            ChallengeOutcome.LeftOnly => Command(
                DuelVerdict.Left,
                $"Challenge ({budget}): {ChallengeRules.Format(duel.ChallengeKind, left.Measured)} vs " +
                $"{ChallengeRules.Format(duel.ChallengeKind, right.Measured)} — the opponent exceeded the budget."),

            ChallengeOutcome.RightOnly => Command(
                DuelVerdict.Right,
                $"Challenge ({budget}): {ChallengeRules.Format(duel.ChallengeKind, right.Measured)} vs " +
                $"{ChallengeRules.Format(duel.ChallengeKind, left.Measured)} — the opponent exceeded the budget."),

            ChallengeOutcome.NeitherMet => Command(
                DuelVerdict.Tie,
                $"Challenge ({budget}): neither model met the budget " +
                $"({ChallengeRules.Format(duel.ChallengeKind, left.Measured)} and " +
                $"{ChallengeRules.Format(duel.ChallengeKind, right.Measured)}), so no rating moved."),

            // BothMet — the budget separates nothing; the ordinary judge decides on quality.
            _ => null,
        };

        RecordVerdictCommand Command(DuelVerdict verdict, string rationale) => new(
            duel.DuelId,
            verdict,
            VerdictSource.Constraint,
            rationale,
            // The "judge" is the rule, not a deployment. Named so the Arena's attribution line
            // says something true rather than falling back to a model that never ran.
            $"{ChallengeRules.KindLabel(duel.ChallengeKind)} budget");
    }
}
