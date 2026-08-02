using Azure;

namespace PoLocalCompare.Api.Common.Persistence;

/// <summary>
/// Turns the <c>ETag</c> string carried on an entity into the <see cref="ETag"/> that
/// <c>UpdateEntityAsync</c> wants.
/// </summary>
/// <remarks>
/// Every repository doing an If-Match conditional update (standards §5.5) needs the same
/// "no ETag yet means unconditional" rule. Getting it wrong in one repository silently turns
/// that table's updates into last-write-wins, which is exactly the failure the ETag exists to
/// prevent — so the rule lives in one place rather than once per slice.
/// </remarks>
internal static class TableETag
{
    internal static ETag Parse(string? etag) =>
        string.IsNullOrEmpty(etag) ? ETag.All : new ETag(etag);
}
