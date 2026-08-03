using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// A duel outlives the catalog entry it points at, which is why most of the Archive used to
/// render "[Deleted Model]": names were resolved by live lookup only, and retired seed IDs
/// resolve to nothing.
/// </summary>
public class DuelModelNamesTests
{
    private static readonly ModelId Id = ModelId.From("01SEED000000000000000000E");

    [Theory]
    // The live catalog wins, so renaming a model shows through on old duels.
    [InlineData("GPT-5.4 Nano", "Old Name", "GPT-5.4 Nano")]
    // Model retired from the catalog → the snapshot taken when the duel was created.
    [InlineData(null, "SmolLM2 135M", "SmolLM2 135M")]
    [InlineData("", "SmolLM2 135M", "SmolLM2 135M")]
    // Neither available (a row predating the snapshot) → the ID, which is the signal callers
    // use to detect "unresolved" and substitute their own label.
    [InlineData(null, null, "01SEED000000000000000000E")]
    [InlineData("   ", "  ", "01SEED000000000000000000E")]
    public void Resolve_PrefersLiveNameThenSnapshotThenId(string? live, string? snapshot, string expected)
    {
        Assert.Equal(expected, DuelModelNames.Resolve(live, snapshot, Id));
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundNames()
    {
        Assert.Equal("Phi-4", DuelModelNames.Resolve("  Phi-4 ", null, Id));
        Assert.Equal("Phi-4", DuelModelNames.Resolve(null, " Phi-4  ", Id));
    }
}
