namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Resolves the name to show for a model that took part in a duel.
///
/// A duel outlives the catalog entry it points at — seed IDs get retired, a wiped Models table
/// re-seeds under fresh ULIDs — so resolving names by live lookup alone left most of the Archive
/// rendering "[Deleted Model]". Order is deliberate: the live catalog wins so a rename shows
/// through everywhere, the snapshot taken when the duel was created covers a model that has since
/// gone, and the raw ID is the last resort that keeps the existing DTO contract (callers detect
/// "unresolved" by comparing the name against the ID).
/// </summary>
public static class DuelModelNames
{
    public static string Resolve(string? liveDisplayName, string? snapshotName, ModelId modelId)
    {
        if (!string.IsNullOrWhiteSpace(liveDisplayName))
            return liveDisplayName.Trim();

        if (!string.IsNullOrWhiteSpace(snapshotName))
            return snapshotName.Trim();

        return modelId;
    }
}
