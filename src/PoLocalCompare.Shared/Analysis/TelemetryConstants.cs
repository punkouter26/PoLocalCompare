// Stable rendering heuristics used by ProcessingPanel so the controls on the live race surface
// match what the duelling models are actually producing. The values are descriptive of the
// shared `OutputAnalysis` size class, not coupled to a specific deployment, which is why they
// live here and not in the per-deployment inference proxy.
//
// A future deployment that legitimately streams 12 k tokens at a stretch is a different shape
// of model than what we have today — that's the moment these constants deserve another look.
namespace PoLocalCompare.Shared.Analysis;

public static class TelemetryConstants
{
    /// <summary>
    /// Working assumption for "how much text is the model going to generate?" Used by the
    /// ProcessingPanel progress bar and the "~Done at" ETA. A remote Foundry call writes to the
    /// configured <c>max_completion_tokens</c> (currently 4 096); a local browser model is
    /// unbounded in practice but uses this as a soft milestone so the progress bar isn't
    /// pinned at 0 % for the first few seconds of generation.
    /// </summary>
    public const int ExpectedCompletionTokens = 4096;

    /// <summary>
    /// Rough bytes-per-token for English text-only output. Real HTML with tags, attributes and
    /// embedded JS leans tighter (≈4 chars per token on average); 4.5 is the published
    /// rule-of-thumb and matches what the panel already displayed before this constant existed,
    /// so the visible "Output size" stays stable when the value moves out of a magic literal.
    /// </summary>
    public const double ApproxBytesPerToken = 4.5;
}
