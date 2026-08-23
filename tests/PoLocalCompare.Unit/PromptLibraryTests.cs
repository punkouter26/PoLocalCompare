using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Shared.Analysis;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Unit;

/// <summary>
/// The library feeds the Compare page and the tournament shortlist, so a malformed entry is a
/// rejected duel at run time rather than as a compile error. These pin the shape every prompt
/// has to satisfy to be startable at all.
/// </summary>
public class PromptLibraryTests
{
    [Fact]
    public void All_HaveUniqueIdsAndATitleAndCategory()
    {
        var ids = PromptLibrary.All.Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        Assert.All(PromptLibrary.All, prompt =>
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Title));
            Assert.False(string.IsNullOrWhiteSpace(prompt.Category));
        });
    }

    [Fact]
    public void All_SatisfyTheCommenceDuelLengthBounds()
    {
        // CommenceDuelHandler rejects anything outside this range, so a library entry that
        // violates it would be a button that always fails.
        Assert.All(PromptLibrary.All, prompt =>
        {
            Assert.InRange(
                prompt.Text.Length,
                CommenceDuelCommand.MinPromptLength,
                CommenceDuelCommand.MaxPromptLength);
        });
    }

    [Fact]
    public void All_AskForASelfContainedSingleFile()
    {
        // The sandbox has no same-origin access, so anything split across files renders blank
        // and the model looks worse than it is.
        Assert.All(PromptLibrary.All, prompt =>
            Assert.Contains("self-contained single HTML file", prompt.Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelfRunning_IsANonEmptySubsetOfAll()
    {
        Assert.NotEmpty(PromptLibrary.SelfRunning);
        Assert.All(PromptLibrary.SelfRunning, prompt => Assert.Contains(prompt, PromptLibrary.All));
        Assert.All(PromptLibrary.SelfRunning, prompt => Assert.True(prompt.SelfRunning));
    }

    [Fact]
    public void ById_ReturnsTheMatchingPromptAndNullForAnUnknownId()
    {
        var expected = PromptLibrary.All[0];

        Assert.Equal(expected, PromptLibrary.ById(expected.Id));
        Assert.Null(PromptLibrary.ById("no-such-prompt"));
    }
}

/// <summary>
/// The preview, the scorecard and the diff all read the same normalized text — if they
/// disagreed, a stray fence would shift every line of the diff.
/// </summary>
public class HtmlPreviewTests
{
    [Fact]
    public void Normalize_StripsAWrappingFence()
    {
        Assert.Equal("<div>x</div>", HtmlPreview.Normalize("```html\n<div>x</div>\n```"));
    }
}
