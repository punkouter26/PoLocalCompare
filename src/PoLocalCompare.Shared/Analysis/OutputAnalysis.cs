using System.Text.RegularExpressions;

namespace PoLocalCompare.Shared.Analysis;

/// <summary>
/// Structural facts about one model's HTML output, derived in the browser without a server
/// round-trip. Deliberately descriptive rather than judgemental: the Arena shows these next to
/// each other so a person can see <em>how</em> two outputs differ before voting. Nothing here
/// feeds ELO — <see cref="PoLocalCompare.Shared.Enums.VerdictSource"/> stays the only thing
/// that moves ratings.
/// </summary>
public sealed record OutputAnalysis
{
    public int SizeBytes { get; init; }
    public int LineCount { get; init; }

    /// <summary>Opening tags, excluding <c>&lt;!doctype&gt;</c> and closing tags.</summary>
    public int TagCount { get; init; }

    /// <summary>Distinct element names — a proxy for structural variety.</summary>
    public int UniqueTagCount { get; init; }

    /// <summary>Deepest nesting reached, ignoring void elements.</summary>
    public int MaxNestingDepth { get; init; }

    /// <summary>Declaration blocks inside <c>&lt;style&gt;</c> sections.</summary>
    public int CssRuleCount { get; init; }

    public int ScriptCount { get; init; }

    /// <summary>Characters of inline script — the rough size of the behaviour the model wrote.</summary>
    public int ScriptChars { get; init; }

    /// <summary>Buttons, inputs, selects, textareas and links — what a user can actually operate.</summary>
    public int InteractiveElementCount { get; init; }

    /// <summary>
    /// Absolute http(s) references. The sandbox permits these, but each one is a way the
    /// preview can fail on a filtered network while the code itself is fine.
    /// </summary>
    public int ExternalResourceCount { get; init; }

    public bool HasDoctype { get; init; }
    public bool HasTitle { get; init; }
    public bool HasViewportMeta { get; init; }
    public bool HasLangAttribute { get; init; }

    /// <summary>Accessibility observations, phrased as what is missing.</summary>
    public IReadOnlyList<string> AccessibilityIssues { get; init; } = [];

    /// <summary>Structural observations that usually mean the output was truncated or malformed.</summary>
    public IReadOnlyList<string> StructuralIssues { get; init; } = [];

    /// <summary>0–100 completeness signal. Presentational only; it never reaches the leaderboard.</summary>
    public int CompletenessScore { get; init; }

    public static readonly OutputAnalysis Empty = new();

    // ── Patterns ─────────────────────────────────────────────────────────────
    // Compiled once. Model output is a few hundred KB at most, so a regex pass is cheap
    // relative to shipping the document to the server and back.

    private static readonly Regex OpenTagPattern = new(
        @"<\s*(?<name>[a-zA-Z][a-zA-Z0-9-]*)\b(?<attrs>[^>]*?)(?<selfclose>/?)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CloseTagPattern = new(
        @"<\s*/\s*(?<name>[a-zA-Z][a-zA-Z0-9-]*)\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StyleBlockPattern = new(
        @"<style\b[^>]*>(?<body>[\s\S]*?)</style>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ScriptBlockPattern = new(
        @"<script\b[^>]*>(?<body>[\s\S]*?)</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExternalRefPattern = new(
        @"(?:src|href)\s*=\s*[""']\s*(?:https?:)?//",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ImgTagPattern = new(
        @"<img\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AltAttrPattern = new(
        @"\balt\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeadingPattern = new(
        @"<\s*h(?<level>[1-6])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LangAttrPattern = new(
        @"<html\b[^>]*\blang\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ViewportMetaPattern = new(
        @"<meta\b[^>]*\bname\s*=\s*[""']viewport[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitlePattern = new(
        @"<title\b[^>]*>\s*\S",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LabelledControlPattern = new(
        @"<(?:input|select|textarea)\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControlLabelHintPattern = new(
        @"\b(?:aria-label|aria-labelledby|id|title|placeholder)\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Elements that never nest, so they must not push the depth counter.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> InteractiveElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "button", "input", "select", "textarea", "details", "summary",
    };

    public static OutputAnalysis Analyze(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return Empty;

        var accessibility = new List<string>();
        var structural = new List<string>();

        var hasDoctype = Regex.IsMatch(html, @"<\s*!doctype\s+html", RegexOptions.IgnoreCase);
        var hasTitle = TitlePattern.IsMatch(html);
        var hasViewport = ViewportMetaPattern.IsMatch(html);
        var hasLang = LangAttrPattern.IsMatch(html);

        // ── Tags, nesting depth and interactivity ────────────────────────────
        // One ordered pass over both open and close tags: depth is only meaningful in
        // document order, so counting the two independently would not give it.
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagCount = 0;
        var interactive = 0;
        var depth = 0;
        var maxDepth = 0;
        var unclosed = 0;

        var events = OpenTagPattern.Matches(html)
            .Select(m => (Index: m.Index, IsOpen: true, Name: m.Groups["name"].Value,
                          SelfClosing: m.Groups["selfclose"].Value == "/"))
            .Concat(CloseTagPattern.Matches(html)
                .Select(m => (Index: m.Index, IsOpen: false, Name: m.Groups["name"].Value, SelfClosing: false)))
            .OrderBy(e => e.Index);

        foreach (var evt in events)
        {
            if (evt.IsOpen)
            {
                tagCount++;
                tagNames.Add(evt.Name);
                if (InteractiveElements.Contains(evt.Name)) interactive++;

                if (evt.SelfClosing || VoidElements.Contains(evt.Name)) continue;

                depth++;
                unclosed++;
                if (depth > maxDepth) maxDepth = depth;
            }
            else
            {
                if (depth > 0) depth--;
                if (unclosed > 0) unclosed--;
            }
        }

        // ── Style and script volume ──────────────────────────────────────────
        var cssRules = StyleBlockPattern.Matches(html)
            .Sum(m => m.Groups["body"].Value.Count(c => c == '{'));

        var scriptBlocks = ScriptBlockPattern.Matches(html);
        var scriptChars = scriptBlocks.Sum(m => m.Groups["body"].Value.Trim().Length);

        var externalRefs = ExternalRefPattern.Matches(html).Count;

        // ── Accessibility observations ───────────────────────────────────────
        var images = ImgTagPattern.Matches(html);
        var imagesMissingAlt = images.Count(m => !AltAttrPattern.IsMatch(m.Value));
        if (imagesMissingAlt > 0)
            accessibility.Add($"{imagesMissingAlt} image{(imagesMissingAlt == 1 ? "" : "s")} without alt text");

        if (!hasLang && hasDoctype)
            accessibility.Add("<html> has no lang attribute");

        if (!hasTitle)
            accessibility.Add("No page <title>");

        if (!hasViewport)
            accessibility.Add("No viewport meta — will not scale on mobile");

        var headingLevels = HeadingPattern.Matches(html)
            .Select(m => int.Parse(m.Groups["level"].Value))
            .ToList();

        if (headingLevels.Count > 0)
        {
            if (headingLevels[0] != 1)
                accessibility.Add($"First heading is h{headingLevels[0]}, not h1");

            var skips = 0;
            for (var i = 1; i < headingLevels.Count; i++)
            {
                if (headingLevels[i] > headingLevels[i - 1] + 1) skips++;
            }
            if (skips > 0)
                accessibility.Add($"{skips} skipped heading level{(skips == 1 ? "" : "s")}");
        }

        var controls = LabelledControlPattern.Matches(html);
        var unlabelledControls = controls.Count(m => !ControlLabelHintPattern.IsMatch(m.Value));
        if (unlabelledControls > 0)
            accessibility.Add($"{unlabelledControls} form control{(unlabelledControls == 1 ? "" : "s")} with no label hint");

        // ── Structural observations ──────────────────────────────────────────
        if (!hasDoctype)
            structural.Add("Missing <!DOCTYPE html>");

        if (unclosed > 0)
            structural.Add($"{unclosed} unclosed element{(unclosed == 1 ? "" : "s")} — output may be truncated");

        if (scriptBlocks.Count > 0 && scriptChars == 0)
            structural.Add("Empty <script> block");

        if (HtmlPreview.ContainsFence(html))
            structural.Add("Markdown fences present in raw output");

        var lineCount = html.Count(c => c == '\n') + 1;

        return new OutputAnalysis
        {
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(html),
            LineCount = lineCount,
            TagCount = tagCount,
            UniqueTagCount = tagNames.Count,
            MaxNestingDepth = maxDepth,
            CssRuleCount = cssRules,
            ScriptCount = scriptBlocks.Count,
            ScriptChars = scriptChars,
            InteractiveElementCount = interactive,
            ExternalResourceCount = externalRefs,
            HasDoctype = hasDoctype,
            HasTitle = hasTitle,
            HasViewportMeta = hasViewport,
            HasLangAttribute = hasLang,
            AccessibilityIssues = accessibility,
            StructuralIssues = structural,
            CompletenessScore = ScoreCompleteness(
                hasDoctype, hasTitle, hasViewport, hasLang,
                tagCount, cssRules, scriptChars, interactive,
                accessibility.Count, structural.Count),
        };
    }

    /// <summary>
    /// A rough "did the model build a whole page" signal, not a quality judgement. Kept separate
    /// from the persisted <c>OutputQualityScore</c> so tightening it here can never retroactively
    /// change a stored duel.
    /// </summary>
    private static int ScoreCompleteness(
        bool hasDoctype, bool hasTitle, bool hasViewport, bool hasLang,
        int tagCount, int cssRules, int scriptChars, int interactive,
        int accessibilityIssues, int structuralIssues)
    {
        var score = 0;

        // Document scaffolding — 30
        if (hasDoctype) score += 12;
        if (hasTitle) score += 6;
        if (hasViewport) score += 6;
        if (hasLang) score += 6;

        // Substance — 45
        score += Math.Min(15, tagCount / 4);
        score += Math.Min(15, cssRules);
        score += Math.Min(15, scriptChars / 60);

        // Something to actually do — 15
        score += Math.Min(15, interactive * 3);

        // Penalties — capped so one noisy category cannot zero an otherwise complete page.
        score += 10;
        score -= Math.Min(6, accessibilityIssues * 2);
        score -= Math.Min(10, structuralIssues * 5);

        return Math.Clamp(score, 0, 100);
    }
}
