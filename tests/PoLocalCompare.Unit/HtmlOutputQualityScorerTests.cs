using PoLocalCompare.Api.Common.Domain;

namespace PoLocalCompare.Unit;

/// <summary>
/// The score is a leaderboard-visible column, so the deduction table is a contract, not an
/// implementation detail — these tests pin each deduction independently.
/// </summary>
public class HtmlOutputQualityScorerTests
{
    // The trailing comment is padding. Every variant below must stay clear of the 200-character
    // minimum on its own — without that slack, deleting one tag also trips the length deduction
    // and a "loses ten" test would silently be measuring two deductions at once.
    private const string FullPage =
        "<!DOCTYPE html><html><head><style>body{margin:0}</style></head>" +
        "<body><div id='app'>Content</div><script>console.log('hi')</script></body></html>" +
        "<!-- padding ------------------------------------------------------------------------" +
        "------------------------------------------------------------------------------------>";

    [Fact]
    public void Score_Null_IsZero()
    {
        Assert.Equal(0, HtmlOutputQualityScorer.Score(null));
    }

    [Fact]
    public void Score_Empty_IsZero()
    {
        Assert.Equal(0, HtmlOutputQualityScorer.Score(string.Empty));
    }

    [Fact]
    public void Score_Whitespace_IsZero()
    {
        Assert.Equal(0, HtmlOutputQualityScorer.Score("    \n  "));
    }

    [Fact]
    public void Score_CompleteDocument_IsPerfect()
    {
        Assert.Equal(100, HtmlOutputQualityScorer.Score(FullPage));
    }

    [Fact]
    public void Score_MissingDoctype_LosesTen()
    {
        var withoutDoctype = FullPage.Replace("<!DOCTYPE html>", string.Empty);

        Assert.Equal(90, HtmlOutputQualityScorer.Score(withoutDoctype));
    }

    [Fact]
    public void Score_MissingScript_LosesTen()
    {
        var withoutScript = FullPage.Replace("<script>console.log('hi')</script>", string.Empty);

        Assert.Equal(90, HtmlOutputQualityScorer.Score(withoutScript));
    }

    [Fact]
    public void Score_ProseWithNoMarkup_TakesTheLargestPenalty()
    {
        // Not-HTML-at-all costs 40, far more than any single missing tag — a model that
        // answered in prose must rank below one that produced imperfect HTML.
        var prose = new string('x', 400);
        var score = HtmlOutputQualityScorer.Score(prose);

        Assert.True(score <= 20, $"Expected prose to score <= 20 but got {score}.");
    }

    [Fact]
    public void Score_ShortOutput_LosesTheLengthPoints()
    {
        var full = HtmlOutputQualityScorer.Score(FullPage);
        var truncated = HtmlOutputQualityScorer.Score(
            "<!DOCTYPE html><html><body><script>x</script></body></html>");

        Assert.True(truncated < full);
    }

    [Fact]
    public void Score_IsCaseInsensitiveAboutTagNames()
    {
        Assert.Equal(
            HtmlOutputQualityScorer.Score(FullPage),
            HtmlOutputQualityScorer.Score(FullPage.ToUpperInvariant()));
    }

    [Fact]
    public void Score_ToleratesWhitespaceInsideTags()
    {
        var spaced = FullPage
            .Replace("<html>", "< html >")
            .Replace("<body>", "< body >");

        Assert.Equal(100, HtmlOutputQualityScorer.Score(spaced));
    }

    [Fact]
    public void Score_NeverExceedsOneHundred()
    {
        Assert.True(HtmlOutputQualityScorer.Score(FullPage + FullPage) <= 100);
    }

    [Fact]
    public void Score_NeverGoesNegative()
    {
        Assert.True(HtmlOutputQualityScorer.Score("no markup at all") >= 0);
    }

    [Fact]
    public void Score_LeadingWhitespaceDoesNotChangeTheResult()
    {
        Assert.Equal(
            HtmlOutputQualityScorer.Score(FullPage),
            HtmlOutputQualityScorer.Score("\n\n   " + FullPage));
    }
}
