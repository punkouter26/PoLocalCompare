using System.Text.RegularExpressions;

namespace PoLocalCompare.Shared.Analysis;

/// <summary>
/// Client-side normalization of a model's raw output into something an iframe can render.
/// </summary>
/// <remarks>
/// The preview, the structural scorecard and the source diff all have to agree on which text
/// they are talking about — a stray markdown fence otherwise shifts every line of the diff and
/// makes two near-identical outputs look wholly different. This is deliberately separate from
/// the API-side normalizer that runs at persist time: this one never mutates what is stored,
/// and "View Source" still shows the untouched raw output.
/// </remarks>
public static class HtmlPreview
{
    private static readonly Regex WholeFencePattern = new(
        @"^\s*```(?:html)?\s*(?<body>[\s\S]*?)\s*```\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FirstFencePattern = new(
        @"```(?:html)?\s*(?<body>[\s\S]*?)\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool ContainsFence(string? raw) =>
        raw?.Contains("```", StringComparison.Ordinal) == true;

    /// <summary>Strips a wrapping markdown fence, if present. Returns the input trimmed otherwise.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var trimmed = raw.Trim();

        var whole = WholeFencePattern.Match(trimmed);
        if (whole.Success)
        {
            var body = whole.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body)) return body;
        }

        var first = FirstFencePattern.Match(trimmed);
        if (first.Success)
        {
            var body = first.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body)) return body;
        }

        return trimmed;
    }
}
