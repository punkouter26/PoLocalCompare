namespace PoLocalCompare.Client.Presentation;

/// <summary>
/// Presentation rules for the <c>FailureReason</c> carried on a duel result.
/// </summary>
/// <remarks>
/// A failure reason is written as a plain-language first line followed by a "[technical] …"
/// block. Card bodies and badges show only the first line; the full text stays in the Arena's
/// technical-details expander. The split rule lives here so the two never disagree about where
/// the human-readable part ends.
/// </remarks>
public static class FailureReasonText
{
    /// <summary>The human-readable first line, trimmed. Null in, null out.</summary>
    public static string? FirstLine(string? reason) => reason?.Split('\n', 2)[0].Trim();
}
