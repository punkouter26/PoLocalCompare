namespace PoLocalCompare.Api.Common.Caching;

/// <summary>
/// HybridCache tag names shared across slices. Lives in Common so a writer in one slice
/// can invalidate a reader in another without reaching into that slice's endpoint class.
/// </summary>
public static class CacheTags
{
    /// <summary>Invalidated whenever ELO changes — see <c>RecordVerdictHandler</c>.</summary>
    public const string Leaderboard = "leaderboard";

    /// <summary>
    /// The ten-minute availability probe. Dropped by <c>DuelExecutionService</c> when a remote
    /// model fails a real duel, so a deployment that has gone down is re-probed on the next
    /// Home load rather than staying "available" for the rest of the window.
    /// </summary>
    public const string ModelAvailability = "model-availability";
}
