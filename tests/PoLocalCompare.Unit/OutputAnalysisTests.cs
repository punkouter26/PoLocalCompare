using PoLocalCompare.Shared.Analysis;

namespace PoLocalCompare.Unit;

/// <summary>
/// The Arena shows these numbers side by side as evidence to vote with, so what each metric
/// counts is a contract. In particular the "issue" lists must not fire on well-formed output —
/// a scorecard that flags every page teaches people to ignore it.
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
    public void Analyze_Null_ReturnsEmpty()
    {
        Assert.Equal(OutputAnalysis.Empty, OutputAnalysis.Analyze(null));
    }

    [Fact]
    public void Analyze_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(OutputAnalysis.Empty, OutputAnalysis.Analyze("   \n  "));
    }

    [Fact]
    public void Analyze_WellFormedPage_ReportsNoStructuralIssues()
    {
        var analysis = OutputAnalysis.Analyze(WellFormedPage);

        Assert.Empty(analysis.StructuralIssues);
    }

    [Fact]
    public void Analyze_WellFormedPage_ReportsNoAccessibilityIssues()
    {
        var analysis = OutputAnalysis.Analyze(WellFormedPage);

        Assert.Empty(analysis.AccessibilityIssues);
    }

    [Fact]
    public void Analyze_WellFormedPage_DetectsDocumentScaffolding()
    {
        var analysis = OutputAnalysis.Analyze(WellFormedPage);

        Assert.True(analysis.HasDoctype);
        Assert.True(analysis.HasTitle);
        Assert.True(analysis.HasViewportMeta);
        Assert.True(analysis.HasLangAttribute);
    }

    [Fact]
    public void Analyze_CountsInteractiveElements()
    {
        var analysis = OutputAnalysis.Analyze(
            "<body><button>a</button><a href='#'>b</a><input><p>not interactive</p></body>");

        Assert.Equal(3, analysis.InteractiveElementCount);
    }

    [Fact]
    public void Analyze_CountsCssRulesInsideStyleBlocksOnly()
    {
        // The brace in the script must not be counted as a CSS rule.
        var analysis = OutputAnalysis.Analyze(
            "<style>a{color:red}b{color:blue}</style><script>if(x){y()}</script>");

        Assert.Equal(2, analysis.CssRuleCount);
    }

    [Fact]
    public void Analyze_VoidElements_DoNotIncreaseNestingDepth()
    {
        // <br> and <img> never nest; counting them as open would inflate depth without limit.
        var withVoids = OutputAnalysis.Analyze("<div><br><img alt=''><br></div>");
        var withoutVoids = OutputAnalysis.Analyze("<div></div>");

        Assert.Equal(withoutVoids.MaxNestingDepth, withVoids.MaxNestingDepth);
    }

    [Fact]
    public void Analyze_SelfClosingTag_DoesNotCountAsUnclosed()
    {
        var analysis = OutputAnalysis.Analyze("<!DOCTYPE html><div><span/></div>");

        Assert.DoesNotContain(analysis.StructuralIssues, i => i.Contains("unclosed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_TruncatedOutput_ReportsUnclosedElements()
    {
        var analysis = OutputAnalysis.Analyze("<!DOCTYPE html><html><body><div><p>cut off");

        Assert.Contains(analysis.StructuralIssues, i => i.Contains("unclosed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_MissingDoctype_ReportsStructuralIssue()
    {
        var analysis = OutputAnalysis.Analyze("<html><body><p>hi</p></body></html>");

        Assert.Contains(analysis.StructuralIssues, i => i.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_MarkdownFence_ReportedAsStructuralIssue()
    {
        var analysis = OutputAnalysis.Analyze("```html\n<div>x</div>\n```");

        Assert.Contains(analysis.StructuralIssues, i => i.Contains("fence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ImageWithoutAlt_ReportedAsAccessibilityIssue()
    {
        var analysis = OutputAnalysis.Analyze("<body><img src='a.png'><img src='b.png' alt='ok'></body>");

        Assert.Contains(analysis.AccessibilityIssues, i => i.Contains("alt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_SkippedHeadingLevel_ReportedAsAccessibilityIssue()
    {
        var analysis = OutputAnalysis.Analyze("<body><h1>a</h1><h4>b</h4></body>");

        Assert.Contains(analysis.AccessibilityIssues, i => i.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_FirstHeadingNotH1_ReportedAsAccessibilityIssue()
    {
        var analysis = OutputAnalysis.Analyze("<body><h2>a</h2></body>");

        Assert.Contains(analysis.AccessibilityIssues, i => i.Contains("h2", StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData("")]
    [InlineData("<div>x</div>")]
    [InlineData("not html at all")]
    public void CompletenessScore_StaysWithinBounds(string html)
    {
        var score = OutputAnalysis.Analyze(html).CompletenessScore;

        Assert.InRange(score, 0, 100);
    }
}
