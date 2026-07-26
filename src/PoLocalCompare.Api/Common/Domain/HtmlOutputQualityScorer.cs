using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Common.Domain;

/// <remarks>
/// Patterns are source-generated: <c>Score</c> runs per enriched result and again for every
/// legacy row read during leaderboard/archive aggregation, and the previous interpolated
/// <c>Regex.IsMatch</c> allocated and re-hashed a fresh pattern string on each of its four calls.
/// </remarks>
public static partial class HtmlOutputQualityScorer
{
    [GeneratedRegex(@"<\s*!doctype\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoctypeRegex();

    [GeneratedRegex(@"<\s*html\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<\s*body\b", RegexOptions.IgnoreCase)]
    private static partial Regex BodyTagRegex();

    [GeneratedRegex(@"<\s*script\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<(?:!doctype|html|head|body|canvas|div|script|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeHtmlRegex();

    public static int Score(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return 0;
        }

        var text = html.Trim();

        var score = 100;
        if (!LooksLikeHtmlRegex().IsMatch(text)) score -= 40;
        if (!DoctypeRegex().IsMatch(text)) score -= 10;
        if (!HtmlTagRegex().IsMatch(text)) score -= 10;
        if (!BodyTagRegex().IsMatch(text)) score -= 10;
        if (!ScriptTagRegex().IsMatch(text)) score -= 10;
        if (text.Length < 200) score -= 10;

        return Math.Clamp(score, 0, 100);
    }
}
