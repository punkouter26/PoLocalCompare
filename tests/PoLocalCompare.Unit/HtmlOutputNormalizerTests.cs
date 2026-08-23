using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Scoring;

namespace PoLocalCompare.Unit;

/// <summary>
/// The browser model posts raw HTML through <c>HtmlOutputNormalizer.Normalize</c> before
/// storage, so the scorecard, the diff, and the "view source" view all read the same shape.
/// These pin the round-trip so a regression here breaks every downstream.
/// </summary>
public class HtmlOutputNormalizerTests
{
    [Fact]
    public void Normalize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HtmlOutputNormalizer.Normalize(null));
    }

    [Fact]
    public void Normalize_HtmlFencedBlock_UnwrapsTheBody()
    {
        Assert.Equal("<div>x</div>", HtmlOutputNormalizer.Normalize("```html\n<div>x</div>\n```"));
    }

    [Fact]
    public void Normalize_FenceWithSurroundingProse_ExtractsTheFirstFencedBody()
    {
        var raw = "Here you go:\n```html\n<p>hi</p>\n```\nHope that helps!";

        Assert.Equal("<p>hi</p>", HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_BareHtml_IsReturnedTrimmed()
    {
        Assert.Equal("<div>x</div>", HtmlOutputNormalizer.Normalize("  <div>x</div>  "));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var raw = "<p>once</p>";

        Assert.Equal(
            HtmlOutputNormalizer.Normalize(raw),
            HtmlOutputNormalizer.Normalize(HtmlOutputNormalizer.Normalize(raw)));
    }
}
