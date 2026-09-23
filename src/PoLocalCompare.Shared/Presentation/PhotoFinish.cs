using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Decides whether a duel ended close enough to deserve the finish-line camera.
/// </summary>
/// <remarks>
/// Pure and in Shared, rather than inline in <c>Arena.razor</c>, so the unit tier can reach it
/// (see CLAUDE.md on browser-side logic). Only two clean finishes qualify: a side that failed
/// did not cross a line, and naming it in a photo finish would describe a race that did not
/// happen. This is about who finished generating first — it says nothing about who won the
/// duel, which is the judge's call.
/// </remarks>
public static class PhotoFinish
{
    /// <summary>Two finishes within this of each other are a photo finish.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Returns which side crossed first and by how much, or null when it was not close (or
    /// either side did not finish cleanly).
    /// </summary>
    public static (Side First, TimeSpan Margin)? Detect(SideMetrics left, SideMetrics right)
    {
        if (left.Status != DuelStatus.Done || right.Status != DuelStatus.Done) return null;
        if (left.FinishedAt is not { } l || right.FinishedAt is not { } r) return null;

        var margin = (l - r).Duration();
        if (margin > Window) return null;

        return (l <= r ? Side.Left : Side.Right, margin);
    }
}
