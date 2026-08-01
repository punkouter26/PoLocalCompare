// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>Stats derived from scanning the HTML content stream during generation.</summary>
public sealed record HtmlStreamStats(
    int TagCount,
    int OpenDepth,
    int StyleRules,
    double RepetitionScore,
    /// <summary>Partial HTML built so far; only populated every 25 tokens to limit bandwidth.</summary>
    string? HtmlPreview = null);

/// <summary>
/// Proxy interface for calling remote inference models (Azure AI Foundry).
/// </summary>
public interface IRemoteInferenceProxy
{
    /// <summary>
    /// Streams tokens from the remote model and returns a completed DuelResult.
    /// Reports intermediate status updates via the provided async callback.
    /// </summary>
    Task<DuelResult> RunInferenceAsync(
        Model model,
        DuelId duelId,
        string promptFull,
        Func<int, long, HtmlStreamStats?, Task> onTokenUpdate,
        CancellationToken cancellationToken);
}
