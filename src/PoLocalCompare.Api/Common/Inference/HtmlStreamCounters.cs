using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// Running HTML-structure counters accumulated per streamed token, shared by every
/// server-side <see cref="IRemoteInferenceProxy"/> so all runners report the same numbers.
/// </summary>
/// <remarks>
/// Runs once per token on the SSE hot path, so the patterns are source-generated and the
/// angle-bracket tally uses span counting — the previous inline version allocated two
/// <c>MatchCollection</c>s and two LINQ enumerators for every token.
/// </remarks>
internal partial struct HtmlStreamCounters
{
    [GeneratedRegex("<[a-zA-Z]")]
    private static partial Regex TagStartRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex StyleRuleRegex();

    public int TagCount { get; private set; }
    public int OpenDepth { get; private set; }
    public int StyleRules { get; private set; }

    public void Accumulate(string token)
    {
        TagCount += TagStartRegex().Count(token);
        StyleRules += StyleRuleRegex().Count(token);

        var span = token.AsSpan();
        OpenDepth += span.Count('<') - span.Count('>');
    }

    /// <summary>Open depth clamped at zero — a malformed stream can close more tags than it opens.</summary>
    public readonly HtmlStreamStats ToStats(string? preview) =>
        new(TagCount, Math.Max(0, OpenDepth), StyleRules, 0.0, preview);
}
