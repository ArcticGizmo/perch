namespace Perch.Data.Roost;

/// <summary>How a pane is drawn: a full thread, or a mini card (header + the last few activity lines).</summary>
public enum RoostPaneSize
{
    Collapsed = 0,
    Expanded = 1,
}

/// <summary>The Roost's three layouts (a persisted segmented toggle).</summary>
public enum RoostLayoutMode
{
    /// <summary>A fixed 2×2 cell viewport; more cells scroll in a row at a time.</summary>
    Tiled = 0,
    /// <summary>tmux main-vertical: the focused pane large and expanded, the rest stacked as mini cards.</summary>
    MainStack = 1,
    /// <summary>One pane, tmux <c>prefix z</c>.</summary>
    Zoom = 2,
}

/// <summary>What <see cref="RoostLayout.ResolveSize"/> needs to know about one pane.</summary>
/// <param name="Pin">The user's manual pin, if any.</param>
/// <param name="Group">The pane's rail group (status-derived).</param>
/// <param name="Ended">The session has exited and the pane is lingering.</param>
/// <param name="Focused">This is the focused pane.</param>
/// <param name="TypingElsewhere">The user is typing in some other pane's composer right now.</param>
/// <param name="Current">The size the pane is drawn at now; null for a pane appearing for the first time.</param>
public readonly record struct RoostSizeInputs(
    RoostPin Pin, RoostGroup Group, bool Ended, bool Focused, bool TypingElsewhere, RoostPaneSize? Current);

/// <summary>A resolved size. <see cref="Held"/> = the pane wants to expand but is held collapsed while the user
/// types; the UI pulses it in its status hue instead.</summary>
public readonly record struct RoostSizeDecision(RoostPaneSize Size, bool Held);

/// <summary>One Tiled grid cell: a single expanded pane, or a stack of mini cards.</summary>
public sealed record RoostCell(IReadOnlyList<string> Keys, bool Expanded);

/// <summary>How many panes are scrolled out of the Tiled viewport, and whether any of them needs the user —
/// the floating "↓ N more · 1 needs you" pill (and its ↑ twin).</summary>
public readonly record struct RoostOverflow(int Above, int AboveNeedsYou, int Below, int BelowNeedsYou)
{
    public bool Any => Above > 0 || Below > 0;
}

/// <summary>
/// The Roost's layout rules (UI-free, unit-tested): the collapse resolver and the Tiled cell packing. The
/// window only measures and paints what these return, so the rules can't drift between layouts.
/// </summary>
public static class RoostLayout
{
    /// <summary>The Tiled viewport: two columns, two rows.</summary>
    public const int Columns = 2;
    public const int VisibleRows = 2;

    /// <summary>
    /// The size a pane should be drawn at. In order:
    /// <list type="number">
    /// <item>A manual pin wins.</item>
    /// <item>Otherwise status decides: Needs you / Done · review expand; Working / Quiet / ended collapse.</item>
    /// <item><b>Typing hold:</b> a collapsed pane that would auto-expand while the user types in another pane stays
    ///   collapsed (<see cref="RoostSizeDecision.Held"/>), so the layout never shifts under their cursor. A pane
    ///   appearing for the first time isn't held — it lands at the end and moves nothing.</item>
    /// <item>The focused pane never auto-collapses: the collapse waits until focus moves off it.</item>
    /// </list>
    /// </summary>
    public static RoostSizeDecision ResolveSize(RoostSizeInputs i)
    {
        if (i.Pin == RoostPin.Expanded) return new(RoostPaneSize.Expanded, false);
        if (i.Pin == RoostPin.Collapsed) return new(RoostPaneSize.Collapsed, false);

        var wanted = !i.Ended && i.Group is RoostGroup.NeedsYou or RoostGroup.DoneReview
            ? RoostPaneSize.Expanded
            : RoostPaneSize.Collapsed;

        if (wanted == RoostPaneSize.Expanded && i.Current == RoostPaneSize.Collapsed && i.TypingElsewhere)
            return new(RoostPaneSize.Collapsed, true);
        if (wanted == RoostPaneSize.Collapsed && i.Current == RoostPaneSize.Expanded && i.Focused)
            return new(RoostPaneSize.Expanded, false);
        return new(wanted, false);
    }

    /// <summary>How many mini cards stack in one cell: as many as fit, never fewer than one.</summary>
    public static int CellCapacity(double cellHeight, double miniCardHeight, double gap)
    {
        if (miniCardHeight <= 0) return 1;
        return Math.Max(1, (int)Math.Floor((cellHeight + gap) / (miniCardHeight + gap)));
    }

    /// <summary>
    /// Flows panes through Tiled cells in their (first-seen) order: an expanded pane takes a whole cell of its
    /// own — starting a fresh one if the current cell already holds mini cards — and mini cards stack up to
    /// <paramref name="capacity"/> per cell. Order is always preserved; an expand or collapse can only move the
    /// panes after it along.
    /// </summary>
    public static IReadOnlyList<RoostCell> Pack(IReadOnlyList<(string Key, RoostPaneSize Size)> panes, int capacity)
    {
        capacity = Math.Max(1, capacity);
        var cells = new List<RoostCell>();
        var minis = new List<string>();

        void FlushMinis()
        {
            if (minis.Count == 0) return;
            cells.Add(new RoostCell(minis.ToArray(), Expanded: false));
            minis.Clear();
        }

        foreach (var (key, size) in panes)
        {
            if (size == RoostPaneSize.Expanded)
            {
                FlushMinis();
                cells.Add(new RoostCell([key], Expanded: true));
                continue;
            }
            minis.Add(key);
            if (minis.Count == capacity) FlushMinis();
        }
        FlushMinis();
        return cells;
    }

    /// <summary>Total grid rows for <paramref name="cellCount"/> cells.</summary>
    public static int RowCount(int cellCount) => (cellCount + Columns - 1) / Columns;

    /// <summary>The cell holding <paramref name="key"/>, or -1.</summary>
    public static int CellOf(IReadOnlyList<RoostCell> cells, string key)
    {
        for (int i = 0; i < cells.Count; i++)
            if (cells[i].Keys.Contains(key)) return i;
        return -1;
    }

    /// <summary>Clamps a scroll position (the first visible row) to the grid.</summary>
    public static int ClampFirstRow(int firstRow, int cellCount) =>
        Math.Clamp(firstRow, 0, Math.Max(0, RowCount(cellCount) - VisibleRows));

    /// <summary>The smallest scroll from <paramref name="firstRow"/> that brings cell <paramref name="cellIndex"/>
    /// into view (unchanged if it already is) — focusing a pane from the rail or keys scrolls it in.</summary>
    public static int ScrollToReveal(int cellIndex, int firstRow, int cellCount)
    {
        if (cellIndex < 0) return ClampFirstRow(firstRow, cellCount);
        int row = cellIndex / Columns;
        if (row < firstRow) firstRow = row;
        else if (row >= firstRow + VisibleRows) firstRow = row - VisibleRows + 1;
        return ClampFirstRow(firstRow, cellCount);
    }

    /// <summary>Counts the panes scrolled above and below the viewport starting at <paramref name="firstRow"/>,
    /// and how many of each need the user.</summary>
    public static RoostOverflow Overflow(IReadOnlyList<RoostCell> cells, int firstRow, Func<string, bool> needsYou)
    {
        firstRow = ClampFirstRow(firstRow, cells.Count);
        int firstVisible = firstRow * Columns, endVisible = (firstRow + VisibleRows) * Columns;
        int above = 0, aboveNy = 0, below = 0, belowNy = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (i >= firstVisible && i < endVisible) continue;
            foreach (var key in cells[i].Keys)
            {
                bool ny = needsYou(key);
                if (i < firstVisible) { above++; if (ny) aboveNy++; }
                else { below++; if (ny) belowNy++; }
            }
        }
        return new RoostOverflow(above, aboveNy, below, belowNy);
    }

    /// <summary>
    /// Main + stack: the focused pane is the main one (falling back to the first pane when nothing visible is
    /// focused); every other pane stacks, in order. Null main for an empty roster.
    /// </summary>
    public static (string? Main, IReadOnlyList<string> Stack) MainStack(IReadOnlyList<string> keys, string? focused)
    {
        if (keys.Count == 0) return (null, []);
        var main = focused is not null && keys.Contains(focused) ? focused : keys[0];
        return (main, keys.Where(k => k != main).ToArray());
    }

    /// <summary>Zoom: the focused pane, else the first. Null for an empty roster.</summary>
    public static string? ZoomTarget(IReadOnlyList<string> keys, string? focused) =>
        focused is not null && keys.Contains(focused) ? focused : keys.Count > 0 ? keys[0] : null;
}
