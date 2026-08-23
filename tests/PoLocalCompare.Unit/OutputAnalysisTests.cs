using PoLocalCompare.Shared.Analysis;

namespace PoLocalCompare.Unit;

/// <summary>
/// The Arena shows these numbers side by side as evidence to vote with, so what each metric
/// counts is a contract. The scorecard must not flag well-formed pages — false positives
/// teach people to ignore it.
/// </summary>
public class OutputAnalysisTests
{
    private const string WellFormedPage =
        "<!DOCTYPE html>\n" +
        "<html lang=\"en\">\n" +
        "<head>\n" +
        "  <meta charset=\"utf-8\">\n" +
        "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
        "  <title>Counter</title>\n" +
        "  <style>body{margin:0}main{display:grid}button{padding:8px}</style>\n" +
        "</head>\n" +
        "<body>\n" +
        "  <main><h1>Counter</h1><p id=\"n\">0</p><button id=\"b\">Add</button></main>\n" +
        "  <script>document.getElementById('b').onclick=()=>{n.textContent=+n.textContent+1};</script>\n" +
        "</body>\n" +
        "</html>";

    [Fact]
    public void Analyze_WellFormedPage_ReportsNoIssues()
    {
        var analysis = OutputAnalysis.Analyze(WellFormedPage);

        Assert.Empty(analysis.StructuralIssues);
        Assert.Empty(analysis.AccessibilityIssues);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body><div><p>cut off", "unclosed")]
    [InlineData("```html\n<div>x</div>\n```",                "fence")]
    [InlineData("<body><img src='a.png'></body>",           "alt")]
    public void Analyze_ReportsEachClassOfIssueWithItsOwnKeyword(string html, string keyword)
    {
        // One assertion per kind of issue rather than one test each — the test name carries
        // the keyword, and Assert.Contains across both issue lists means a regression in any
        // bucket fails this row.
        var analysis = OutputAnalysis.Analyze(html);

        Assert.Contains(
            analysis.StructuralIssues.Concat(analysis.AccessibilityIssues),
            i => i.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_CountsExternalResources()
    {
        var analysis = OutputAnalysis.Analyze(
            "<script src=\"https://cdn.example/a.js\"></script>" +
            "<link href=\"//cdn.example/b.css\">" +
            "<img src=\"data:image/png;base64,AAA\" alt=''>");

        // The data: URI is not a network dependency, so it must not be counted as one.
        Assert.Equal(2, analysis.ExternalResourceCount);
    }

    [Fact]
    public void CompletenessScore_IsHigherForACompletePageThanAFragment()
    {
        var complete = OutputAnalysis.Analyze(WellFormedPage);
        var fragment = OutputAnalysis.Analyze("<div>hello</div>");

        Assert.True(complete.CompletenessScore > fragment.CompletenessScore);
    }

    /// <summary>Empty input must score inside the band, not below it.</summary>
    [Fact]
    public void CompletenessScore_StaysWithinBounds()
    {
        Assert.InRange(OutputAnalysis.Analyze("").CompletenessScore, 0, 100);
    }
}
