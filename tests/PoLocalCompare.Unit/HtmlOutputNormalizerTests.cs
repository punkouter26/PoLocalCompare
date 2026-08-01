using PoLocalCompare.Api.Common.Domain;

namespace PoLocalCompare.Unit;

/// <summary>
/// Models wrap their HTML in markdown fences often enough that the raw output is unusable
/// without this step — it runs on every result before storage, so a regression here corrupts
/// the archive rather than just the display.
/// </summary>
public class HtmlOutputNormalizerTests
{
    [Fact]
    public void Normalize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlOutputNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlOutputNormalizer.Normalize(string.Empty));
    }

    [Fact]
    public void Normalize_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlOutputNormalizer.Normalize("   \n\t  "));
    }

    [Fact]
    public void Normalize_BareHtml_IsReturnedUnchangedApartFromTrimming()
    {
        const string html = "<html><body>Hi</body></html>";

        Assert.Equal(html, HtmlOutputNormalizer.Normalize($"  {html}  "));
    }

    [Fact]
    public void Normalize_HtmlFencedBlock_UnwrapsTheBody()
    {
        var raw = "```html\n<html><body>Hi</body></html>\n```";

        Assert.Equal("<html><body>Hi</body></html>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_UnlabelledFencedBlock_UnwrapsTheBody()
    {
        var raw = "```\n<div>plain</div>\n```";

        Assert.Equal("<div>plain</div>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_FenceLabelIsCaseInsensitive()
    {
        var raw = "```HTML\n<p>x</p>\n```";

        Assert.Equal("<p>x</p>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_FenceWithSurroundingProse_ExtractsTheFirstFencedBody()
    {
        var raw = "Here you go:\n```html\n<h1>Title</h1>\n```\nHope that helps!";

        Assert.Equal("<h1>Title</h1>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_TwoFencedBlocks_UnwrapsTheOutermostSpan_KnownLimitation()
    {
        // Pins the real behaviour, not the ideal one. The whole-string pattern is anchored
        // ^```…```$, and a reply containing two fenced blocks still satisfies those anchors, so
        // the body it captures runs from the first fence to the last — swallowing the prose
        // between them and the second block. The lazy first-fence pattern that would yield just
        // "<first/>" is only consulted when the anchored pattern fails to match at all.
        //
        // Left as-is deliberately: models emit a single fenced block in practice, and the worst
        // case here is extra text rather than lost output. If multi-block replies start showing
        // up, make the anchored pattern reject inner fences and this test should flip.
        var raw = "```html\n<first/>\n```\nand\n```html\n<second/>\n```";

        Assert.Equal("<first/>\n```\nand\n```html\n<second/>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_EmptyFence_FallsBackToTheTrimmedOriginal()
    {
        // An empty fence carries no content; discarding the surrounding text as well would
        // lose the model's only output.
        var raw = "```html\n\n```";

        Assert.Equal(raw.Trim(), HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_PreservesInnerBackticksThatAreNotFences()
    {
        var raw = "```html\n<code>a `b` c</code>\n```";

        Assert.Equal("<code>a `b` c</code>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_MultiLineHtmlInsideFence_KeepsInternalNewlines()
    {
        var raw = "```html\n<html>\n  <body>x</body>\n</html>\n```";
        var result = HtmlOutputNormalizer.Normalize(raw);

        Assert.Contains("\n", result);
        Assert.StartsWith("<html>", result);
        Assert.EndsWith("</html>", result);
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var raw = "```html\n<html><body>Hi</body></html>\n```";

        var once = HtmlOutputNormalizer.Normalize(raw);
        var twice = HtmlOutputNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }
}
