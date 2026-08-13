using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Models;

/// <summary>
/// Resolves the name to show for a model that took part in a duel.
///
/// Lives in Shared, not in the API slice, because the Archive and the Home page's recent list
/// need the same answer client-side. While it was server-side each of them grew its own copy,
/// and the copies drifted.
///
/// A duel outlives the catalog entry it points at (seed IDs get retired, a wiped Models table
/// re-seeds under fresh ULIDs), so resolving names by live lookup alone left most of the Archive
/// rendering "[Deleted Model]". Order is deliberate: the live catalog wins so a rename shows
/// through everywhere, the snapshot taken when the duel was created covers a model that has since
/// gone, and the raw ID is the last resort that keeps the existing DTO contract (callers detect
/// "unresolved" by comparing the name against the ID).
/// </summary>
public static class ModelDisplayName
{
    /// <summary>What to show when nothing can name the model. See <see cref="ResolveForDisplay"/>.</summary>
    public const string RetiredPlaceholder = "Retired model";

    public static string Resolve(string? liveDisplayName, string? snapshotName, ModelId modelId)
    {
        if (!string.IsNullOrWhiteSpace(liveDisplayName))
            return liveDisplayName.Trim();

        if (!string.IsNullOrWhiteSpace(snapshotName))
            return snapshotName.Trim();

        return modelId;
    }

    /// <summary>True when the only "name" available is the id itself, i.e. nothing resolved.</summary>
    public static bool IsUnresolved(string? name, string? modelId) =>
        string.IsNullOrWhiteSpace(name)
        || (!string.IsNullOrWhiteSpace(modelId)
            && string.Equals(name.Trim(), modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <see cref="Resolve"/>, but substituting <see cref="RetiredPlaceholder"/> for an
    /// unresolved id rather than leaking a 26-character ULID into the UI.
    /// </summary>
    /// <remarks>
    /// "Retired", not "Deleted": the duel is intact, it is the catalog entry that is gone.
    /// <c>OrphanModelIdRemapper</c> repoints history at the current catalog on startup, so in
    /// practice this now fires only for a model whose name matches nothing the catalog knows.
    /// It stays because re-registering an API model after a storage wipe can mint a fresh id at
    /// any time. This is the single implementation: the Archive, the Home page's recent list and
    /// the kill-list handler each carried their own, and they had already drifted — two said
    /// "Retired model" and the third said "Unknown model" for the same condition.
    /// </remarks>
    public static string ResolveForDisplay(string? liveDisplayName, string? snapshotName, ModelId modelId)
    {
        var resolved = Resolve(liveDisplayName, snapshotName, modelId);
        return IsUnresolved(resolved, modelId) ? RetiredPlaceholder : resolved;
    }
}
