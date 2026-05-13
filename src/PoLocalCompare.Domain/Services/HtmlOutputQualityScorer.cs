using System.Text.RegularExpressions;

namespace PoLocalCompare.Domain.Services;

public static class HtmlOutputQualityScorer
{
    public static int Score(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return 0;
        }

        var text = html.Trim();
        var hasDoctype = ContainsTag(text, "!doctype");
        var hasHtml = ContainsTag(text, "html");
        var hasBody = ContainsTag(text, "body");
        var hasScript = ContainsTag(text, "script");
        var looksLikeHtml = LooksLikeHtml(text);

        var score = 100;
        if (!looksLikeHtml) score -= 40;
        if (!hasDoctype) score -= 10;
        if (!hasHtml) score -= 10;
        if (!hasBody) score -= 10;
        if (!hasScript) score -= 10;
        if (text.Length < 200) score -= 10;

        return Math.Clamp(score, 0, 100);
    }

    private static bool ContainsTag(string html, string tagName) =>
        Regex.IsMatch(html, $@"<\s*{Regex.Escape(tagName)}\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeHtml(string html) =>
        Regex.IsMatch(html, @"<(?:!doctype|html|head|body|canvas|div|script|style)\b", RegexOptions.IgnoreCase);
}