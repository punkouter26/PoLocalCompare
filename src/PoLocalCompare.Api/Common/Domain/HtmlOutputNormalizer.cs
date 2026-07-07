using System.Text.RegularExpressions;

namespace PoLocalCompare.Api.Common.Domain;

public static class HtmlOutputNormalizer
{
    private static readonly Regex FencedBlockRegex = new(
        @"^\s*```(?:html)?\s*(?<body>[\s\S]*?)\s*```\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FirstFenceRegex = new(
        @"```(?:html)?\s*(?<body>[\s\S]*?)\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();

        var fencedBlock = FencedBlockRegex.Match(trimmed);
        if (fencedBlock.Success)
        {
            var body = fencedBlock.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body;
            }
        }

        var firstFence = FirstFenceRegex.Match(trimmed);
        if (firstFence.Success)
        {
            var body = firstFence.Groups["body"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                return body;
            }
        }

        return trimmed;
    }
}