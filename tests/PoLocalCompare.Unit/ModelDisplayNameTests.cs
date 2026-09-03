using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Models;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Unit;

/// <summary>
/// One resolver, three former call sites. The Archive, the Home page's recent list and the
/// kill-list handler each had their own copy of this and disagreed on the answer, so the rules
/// are pinned here rather than in whichever surface happens to be looked at.
/// </summary>
public class ModelDisplayNameTests
{
    private static readonly ModelId Id = ModelId.From("01SEED000000000000000000P");

    [Theory]
    // The live catalog wins, so renaming a model shows through on old duels.
    [InlineData("GPT-5.4 Nano", "Old Name", "GPT-5.4 Nano")]
    // Model retired from the catalog → the snapshot taken when the duel was created.
    // Neither available (a row predating the snapshot) → the ID, which is the signal callers
    // use to detect "unresolved" and substitute their own label.
    [InlineData(null, null, "01SEED000000000000000000P")]
    public void Resolve_PrefersLiveNameThenSnapshotThenId(string? live, string? snapshot, string expected)
    {
        Assert.Equal(expected, ModelDisplayName.Resolve(live, snapshot, Id));
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundNames()
    {
        Assert.Equal("Phi-4", ModelDisplayName.Resolve("  Phi-4 ", null, Id));
        Assert.Equal("Phi-4", ModelDisplayName.Resolve(null, " Phi-4  ", Id));
    }

    [Fact]
    public void IsUnresolved_TrueWhenTheNameIsTheIdOrBlank()
    {
        // The id itself, the id with different casing and surrounding space, and a blank name
        // all mean the same thing to a caller: nothing was resolved.
        Assert.True(ModelDisplayName.IsUnresolved(Id.Value, Id.Value));
        Assert.True(ModelDisplayName.IsUnresolved($"  {Id.Value.ToLowerInvariant()} ", Id.Value));
        Assert.True(ModelDisplayName.IsUnresolved("   ", Id.Value));
    }

    [Fact]
    public void ResolveForDisplay_SubstitutesThePlaceholderRatherThanLeakingTheId()
    {
        // The whole point: a 26-character ULID must never reach a table cell.
        Assert.Equal(ModelDisplayName.RetiredPlaceholder, ModelDisplayName.ResolveForDisplay(null, null, Id));
    }

    [Fact]
    public void ResolveForDisplay_PassesARealNameThroughTrimmed()
    {
        Assert.Equal("Phi-4", ModelDisplayName.ResolveForDisplay("  Phi-4  ", null, Id));
        Assert.False(ModelDisplayName.IsUnresolved("Phi-4", Id.Value));
    }

    // ── ModelTypeGroup.ShortLabel vocabulary ───────────────────────────────────────────────

    /// <summary>
    /// The badge text and the filter chips must use the same words. Three call sites read
    /// <see cref="ModelTypeGroup.ShortLabel"/>; the chips read <see cref="ModelTypeGroup.Label"/>;
    /// a card showing "SVC" while sitting under a tab that says "Ollama" was the symptom that
    /// motivated this test.
    /// </summary>
    [Theory]
    [InlineData(ModelType.Remote, "REMOTE")]
    [InlineData(ModelType.Local, "BROWSER")]
    [InlineData(ModelType.LocalService, "OLLAMA")]
    public void ShortLabel_MatchesTheFilterChipVocabulary(ModelType type, string expected)
    {
        Assert.Equal(expected, ModelTypeGroup.ShortLabel(type));
    }
}
