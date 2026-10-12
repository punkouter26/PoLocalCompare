namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// The one place a token count becomes dollars.
/// </summary>
/// <remarks>
/// It was an inline expression in <see cref="FoundryInferenceProxy"/> until 2026-10-11, when
/// re-pricing stored results needed the same arithmetic: 39 of 111 results recorded between
/// 2026-09-23 and 2026-09-26 carried costs from the price-book bug (GPT-5.4 Nano billed at
/// GPT-5.4's rate, twelve times too high), and a repair that restated the formula could have
/// drifted from the one that prices new duels.
/// </remarks>
public static class TokenCost
{
    /// <summary>
    /// Cost of one call, or null when the model has no rate at all — an unpriced model is
    /// "unknown", which the leaderboard treats differently from "free".
    /// </summary>
    public static double? Compute(
        decimal? inputPricePerMillion,
        decimal? outputPricePerMillion,
        int? promptTokens,
        int outputTokens,
        int? separateReasoningTokens = null)
    {
        if (!inputPricePerMillion.HasValue && !outputPricePerMillion.HasValue) return null;

        // Reasoning tokens are billed as output. Most providers already count them inside
        // outputTokens; pass them here only for one that reports them beside it
        // (FoundryChatRequest.ReportsReasoningSeparately), or they are billed and not counted.
        var billedOutput = outputTokens + (separateReasoningTokens ?? 0);

        var inputCost = ((promptTokens ?? 0) / 1_000_000.0) * (double)(inputPricePerMillion ?? 0);
        var outputCost = (billedOutput / 1_000_000.0) * (double)(outputPricePerMillion ?? 0);
        return inputCost + outputCost;
    }
}
