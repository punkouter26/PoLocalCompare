namespace PoLocalCompare.Shared.Analysis;

public enum DiffKind
{
    /// <summary>Present and identical on both sides.</summary>
    Equal,
    /// <summary>Only on the left.</summary>
    Removed,
    /// <summary>Only on the right.</summary>
    Added,
    /// <summary>A removal and an addition that line up — rendered as one side-by-side row.</summary>
    Changed,
}

/// <summary>
/// One row of a side-by-side diff. Either side may be absent, which is what makes the two
/// panes stay vertically aligned when one output is longer than the other.
/// </summary>
public sealed record DiffRow(
    DiffKind Kind,
    int? LeftLineNumber,
    string? LeftText,
    int? RightLineNumber,
    string? RightText);

public sealed record DiffStats(int Equal, int Changed, int Added, int Removed)
{
    public int TotalDifferences => Changed + Added + Removed;
}

/// <summary>
/// A contiguous run of diff rows. Long stretches of identical lines arrive
/// <see cref="IsCollapsed"/>, carrying their rows so the UI can reveal them without re-diffing.
/// </summary>
public sealed record DiffSegment(bool IsCollapsed, IReadOnlyList<DiffRow> Rows)
{
    public int RowCount => Rows.Count;
}

/// <summary>
/// Line-level diff for comparing two models' HTML side by side.
/// </summary>
/// <remarks>
/// Plain LCS. Model outputs are small enough that the O(n·m) table is not worth avoiding, but
/// it is not unbounded either — see <see cref="MaxDiffableLines"/>, past which the two outputs
/// are shown unaligned rather than allocating a table big enough to stall the browser. This
/// runs in WebAssembly on the UI thread, so a pathological input has to degrade, not hang.
/// </remarks>
public static class LineDiff
{
    /// <summary>
    /// Ceiling on lines per side. 4000² ints ≈ 64 MB, already generous for a single HTML file;
    /// beyond this the diff is not useful to read anyway.
    /// </summary>
    public const int MaxDiffableLines = 4000;

    public static IReadOnlyList<DiffRow> Compute(string? left, string? right)
    {
        var leftLines = SplitLines(left);
        var rightLines = SplitLines(right);

        if (leftLines.Length > MaxDiffableLines || rightLines.Length > MaxDiffableLines)
            return Unaligned(leftLines, rightLines);

        var lcs = BuildLcsTable(leftLines, rightLines);
        var operations = Walk(lcs, leftLines, rightLines);
        return PairIntoRows(operations);
    }

    /// <summary>
    /// Groups rows into alternating visible and collapsed runs, keeping <paramref name="context"/>
    /// identical lines either side of every difference.
    /// </summary>
    /// <remarks>
    /// Two HTML documents built from the same prompt share long identical stretches — boilerplate
    /// head, reset CSS — and rendering thousands of unchanged rows is both unreadable and the one
    /// thing that would make this view slow in WebAssembly. Runs shorter than
    /// <paramref name="minimumCollapse"/> stay visible: hiding two lines behind a "show 2 more"
    /// control costs more than it saves.
    /// </remarks>
    public static IReadOnlyList<DiffSegment> Fold(
        IReadOnlyList<DiffRow> rows,
        int context = 3,
        int minimumCollapse = 6)
    {
        if (rows.Count == 0) return [];

        var keep = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == DiffKind.Equal) continue;

            var from = Math.Max(0, i - context);
            var to = Math.Min(rows.Count - 1, i + context);
            for (var j = from; j <= to; j++) keep[j] = true;
        }

        var segments = new List<DiffSegment>();
        var start = 0;

        while (start < rows.Count)
        {
            var visible = keep[start];
            var end = start;
            while (end + 1 < rows.Count && keep[end + 1] == visible) end++;

            var run = rows.Skip(start).Take(end - start + 1).ToList();
            var collapse = !visible && run.Count >= minimumCollapse;
            segments.Add(new DiffSegment(collapse, run));

            start = end + 1;
        }

        // Merge neighbours that ended up on the same side of the collapse decision, so a short
        // unchanged run does not split one visible block into three.
        var merged = new List<DiffSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (merged.Count > 0 && !merged[^1].IsCollapsed && !segment.IsCollapsed)
            {
                var combined = merged[^1].Rows.Concat(segment.Rows).ToList();
                merged[^1] = new DiffSegment(false, combined);
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    public static DiffStats Summarize(IReadOnlyList<DiffRow> rows) => new(
        Equal: rows.Count(r => r.Kind == DiffKind.Equal),
        Changed: rows.Count(r => r.Kind == DiffKind.Changed),
        Added: rows.Count(r => r.Kind == DiffKind.Added),
        Removed: rows.Count(r => r.Kind == DiffKind.Removed));

    private static string[] SplitLines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal)
                  .Replace('\r', '\n')
                  .Split('\n');

    private static int[,] BuildLcsTable(string[] left, string[] right)
    {
        var table = new int[left.Length + 1, right.Length + 1];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                table[i, j] = left[i] == right[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table;
    }

    private static List<DiffRow> Walk(int[,] table, string[] left, string[] right)
    {
        var operations = new List<DiffRow>();
        int i = 0, j = 0;

        while (i < left.Length && j < right.Length)
        {
            if (left[i] == right[j])
            {
                operations.Add(new DiffRow(DiffKind.Equal, i + 1, left[i], j + 1, right[j]));
                i++;
                j++;
            }
            else if (table[i + 1, j] >= table[i, j + 1])
            {
                operations.Add(new DiffRow(DiffKind.Removed, i + 1, left[i], null, null));
                i++;
            }
            else
            {
                operations.Add(new DiffRow(DiffKind.Added, null, null, j + 1, right[j]));
                j++;
            }
        }

        while (i < left.Length)
        {
            operations.Add(new DiffRow(DiffKind.Removed, i + 1, left[i], null, null));
            i++;
        }

        while (j < right.Length)
        {
            operations.Add(new DiffRow(DiffKind.Added, null, null, j + 1, right[j]));
            j++;
        }

        return operations;
    }

    /// <summary>
    /// Collapses each run of removals immediately followed by additions into shared rows, so a
    /// rewritten block reads as "left said X, right said Y" on one line instead of as a block of
    /// deletions above an unrelated-looking block of insertions.
    /// </summary>
    private static List<DiffRow> PairIntoRows(List<DiffRow> operations)
    {
        var rows = new List<DiffRow>(operations.Count);
        var index = 0;

        while (index < operations.Count)
        {
            if (operations[index].Kind != DiffKind.Removed)
            {
                rows.Add(operations[index]);
                index++;
                continue;
            }

            var removals = new List<DiffRow>();
            while (index < operations.Count && operations[index].Kind == DiffKind.Removed)
            {
                removals.Add(operations[index]);
                index++;
            }

            var additions = new List<DiffRow>();
            while (index < operations.Count && operations[index].Kind == DiffKind.Added)
            {
                additions.Add(operations[index]);
                index++;
            }

            var paired = Math.Min(removals.Count, additions.Count);
            for (var k = 0; k < paired; k++)
            {
                rows.Add(new DiffRow(
                    DiffKind.Changed,
                    removals[k].LeftLineNumber, removals[k].LeftText,
                    additions[k].RightLineNumber, additions[k].RightText));
            }

            for (var k = paired; k < removals.Count; k++) rows.Add(removals[k]);
            for (var k = paired; k < additions.Count; k++) rows.Add(additions[k]);
        }

        return rows;
    }

    /// <summary>
    /// Fallback past <see cref="MaxDiffableLines"/>: both sides are still readable and still
    /// line-numbered, they are simply zipped positionally instead of aligned.
    /// </summary>
    private static List<DiffRow> Unaligned(string[] left, string[] right)
    {
        var rows = new List<DiffRow>(Math.Max(left.Length, right.Length));

        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var l = i < left.Length ? left[i] : null;
            var r = i < right.Length ? right[i] : null;
            var kind = l is null ? DiffKind.Added
                     : r is null ? DiffKind.Removed
                     : l == r ? DiffKind.Equal
                     : DiffKind.Changed;

            rows.Add(new DiffRow(
                kind,
                l is null ? null : i + 1, l,
                r is null ? null : i + 1, r));
        }

        return rows;
    }
}
