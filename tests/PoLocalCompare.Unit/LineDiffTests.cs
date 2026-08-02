using PoLocalCompare.Shared.Analysis;

namespace PoLocalCompare.Unit;

/// <summary>
/// The Arena renders these rows directly into a two-column grid, so the alignment guarantee is
/// the contract: every row must carry at most one side per column, and the folded view must
/// never drop a line the unfolded view had.
/// </summary>
public class LineDiffTests
{
    [Fact]
    public void Compute_IdenticalInput_IsAllEqual()
    {
        var rows = LineDiff.Compute("a\nb\nc", "a\nb\nc");

        Assert.All(rows, row => Assert.Equal(DiffKind.Equal, row.Kind));
        Assert.Equal(0, LineDiff.Summarize(rows).TotalDifferences);
    }

    [Fact]
    public void Compute_BothEmpty_ReturnsNoRows()
    {
        Assert.Empty(LineDiff.Compute(null, null));
    }

    [Fact]
    public void Compute_OnlyLeft_MarksEveryRowRemoved()
    {
        var rows = LineDiff.Compute("a\nb", null);

        Assert.All(rows, row => Assert.Equal(DiffKind.Removed, row.Kind));
        Assert.All(rows, row => Assert.Null(row.RightText));
    }

    [Fact]
    public void Compute_OnlyRight_MarksEveryRowAdded()
    {
        var rows = LineDiff.Compute(null, "a\nb");

        Assert.All(rows, row => Assert.Equal(DiffKind.Added, row.Kind));
        Assert.All(rows, row => Assert.Null(row.LeftText));
    }

    [Fact]
    public void Compute_ReplacedLine_PairsIntoASingleChangedRow()
    {
        // A rewritten line must read as "left said X, right said Y" on one row rather than as a
        // deletion above an unrelated-looking insertion.
        var rows = LineDiff.Compute("a\nOLD\nc", "a\nNEW\nc");

        var changed = Assert.Single(rows, row => row.Kind == DiffKind.Changed);
        Assert.Equal("OLD", changed.LeftText);
        Assert.Equal("NEW", changed.RightText);
    }

    [Fact]
    public void Compute_PureInsertion_IsAddedNotChanged()
    {
        var rows = LineDiff.Compute("a\nc", "a\nb\nc");

        Assert.Contains(rows, row => row.Kind == DiffKind.Added && row.RightText == "b");
        Assert.DoesNotContain(rows, row => row.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Compute_PreservesEveryLineFromBothSides()
    {
        var rows = LineDiff.Compute("a\nb\nc\nd", "a\nx\nc\ny\nz");

        var left = rows.Where(r => r.LeftText is not null).Select(r => r.LeftText).ToList();
        var right = rows.Where(r => r.RightText is not null).Select(r => r.RightText).ToList();

        Assert.Equal(new[] { "a", "b", "c", "d" }, left);
        Assert.Equal(new[] { "a", "x", "c", "y", "z" }, right);
    }

    [Fact]
    public void Compute_LineNumbersAreSequentialPerSide()
    {
        var rows = LineDiff.Compute("a\nb\nc", "a\nx\nc");

        var leftNumbers = rows.Where(r => r.LeftLineNumber is not null).Select(r => r.LeftLineNumber!.Value);
        var rightNumbers = rows.Where(r => r.RightLineNumber is not null).Select(r => r.RightLineNumber!.Value);

        Assert.Equal(new[] { 1, 2, 3 }, leftNumbers);
        Assert.Equal(new[] { 1, 2, 3 }, rightNumbers);
    }

    [Fact]
    public void Compute_NormalizesWindowsLineEndings()
    {
        var rows = LineDiff.Compute("a\r\nb", "a\nb");

        Assert.Equal(0, LineDiff.Summarize(rows).TotalDifferences);
    }

    [Fact]
    public void Fold_LongIdenticalRun_IsCollapsed()
    {
        var identical = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}"));
        var rows = LineDiff.Compute(identical, identical);

        var segments = LineDiff.Fold(rows);

        Assert.Contains(segments, s => s.IsCollapsed);
    }

    [Fact]
    public void Fold_KeepsContextAroundEveryDifference()
    {
        var left = string.Join('\n', Enumerable.Range(0, 40).Select(i => i == 20 ? "OLD" : $"line {i}"));
        var right = string.Join('\n', Enumerable.Range(0, 40).Select(i => i == 20 ? "NEW" : $"line {i}"));

        var segments = LineDiff.Fold(LineDiff.Compute(left, right), context: 3);
        var visible = segments.Where(s => !s.IsCollapsed).SelectMany(s => s.Rows).ToList();

        Assert.Contains(visible, r => r.Kind == DiffKind.Changed);
        // The change plus three lines of context on each side.
        Assert.Equal(7, visible.Count);
    }

    [Fact]
    public void Fold_ShortIdenticalRun_StaysVisible()
    {
        // Hiding two lines behind a "show 2 more" control costs more than it saves.
        var rows = LineDiff.Compute("x\na\nb\ny", "p\na\nb\nq");

        var segments = LineDiff.Fold(rows, context: 0, minimumCollapse: 6);

        Assert.All(segments, s => Assert.False(s.IsCollapsed));
    }

    [Fact]
    public void Fold_LosesNoRows()
    {
        var left = string.Join('\n', Enumerable.Range(0, 60).Select(i => i is 10 or 45 ? "OLD" : $"line {i}"));
        var right = string.Join('\n', Enumerable.Range(0, 60).Select(i => i is 10 or 45 ? "NEW" : $"line {i}"));
        var rows = LineDiff.Compute(left, right);

        var folded = LineDiff.Fold(rows).SelectMany(s => s.Rows).ToList();

        Assert.Equal(rows.Count, folded.Count);
    }

    [Fact]
    public void Fold_Empty_ReturnsNoSegments()
    {
        Assert.Empty(LineDiff.Fold([]));
    }

    [Fact]
    public void Compute_BeyondTheLineCeiling_StillReturnsEveryLine()
    {
        // Past MaxDiffableLines the alignment degrades to positional zipping, but nothing may
        // be dropped — the pane still has to show the whole document.
        var huge = string.Join('\n', Enumerable.Range(0, LineDiff.MaxDiffableLines + 50).Select(i => $"l{i}"));

        var rows = LineDiff.Compute(huge, huge);

        Assert.Equal(LineDiff.MaxDiffableLines + 50, rows.Count);
    }

    [Fact]
    public void Summarize_CountsEachKind()
    {
        // Two removals against three additions: the first two pair into Changed rows and the
        // odd one out stays a plain Added row.
        var rows = LineDiff.Compute("same\nOLD\ngone", "same\nNEW\nextra\nplus");
        var stats = LineDiff.Summarize(rows);

        Assert.Equal(1, stats.Equal);
        Assert.Equal(2, stats.Changed);
        Assert.Equal(1, stats.Added);
        Assert.Equal(0, stats.Removed);
        Assert.Equal(3, stats.TotalDifferences);
    }
}
