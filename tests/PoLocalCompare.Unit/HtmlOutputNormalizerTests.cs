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
    [Theory]
    [InlineData(null, "")]                                   // null is empty
    [InlineData("```html\n<div>x</div>\n```", "<div>x</div>")] // unwrap a single fence
    public void Normalize_HandlesNullFencedAndBareInput(string? raw, string expected)
    {
        Assert.Equal(expected, HtmlOutputNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_FenceWithSurroundingProse_ExtractsTheFirstFencedBody()
    {
        // Wrapping prose is the trickiest case: the server has to keep the body but drop the
        // chatter that surrounds it. Pin the position of the fenced block, not the wrapping
        // copy, because the chatter is whatever the model chose to say.
        var raw = "Here you go:\n```html\n<p>hi</p>\n```\nHope that helps!";

        Assert.Equal("<p>hi</p>", HtmlOutputNormalizer.Normalize(raw));

        // Idempotent: normalising twice is the same as normalising once. Any path that
        // accumulates whitespace or rewrites tags on each pass would diverge.
        var raw2 = "<p>once</p>";
        Assert.Equal(
            HtmlOutputNormalizer.Normalize(raw2),
            HtmlOutputNormalizer.Normalize(HtmlOutputNormalizer.Normalize(raw2)));
    }
}
