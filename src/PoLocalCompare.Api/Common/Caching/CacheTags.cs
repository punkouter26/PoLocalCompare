namespace PoLocalCompare.Api.Common.Caching;

/// <summary>
/// HybridCache tag names shared across slices. Lives in Common so a writer in one slice
/// can invalidate a reader in another without reaching into that slice's endpoint class.
/// </summary>
public static class CacheTags
{
    /// <summary>Invalidated whenever ELO changes — see <c>RecordVerdictHandler</c>.</summary>
    public const string Leaderboard = "leaderboard";
}
