namespace Perch.Data.Roost;

/// <summary>One region of a <see cref="RoostGridLayout"/>, in grid units. <see cref="Id"/> is stable through
/// edits (a split keeps it on the first half), so a tab's session assignment follows the region.</summary>
public readonly record struct RoostRegion(int Id, int Row, int Column, int RowSpan, int ColumnSpan)
{
    public int Bottom => Row + RowSpan;
    public int Right => Column + ColumnSpan;
    public int Area => RowSpan * ColumnSpan;
}

/// <summary>How <see cref="RoostGridLayout.Split"/> cuts a region.</summary>
public enum RoostSplit
{
    /// <summary>Side by side: the new divider runs top to bottom.</summary>
    Columns = 0,
    /// <summary>Stacked: the new divider runs left to right.</summary>
    Rows = 1,
}

/// <summary>Which way a divider runs: a <see cref="Vertical"/> one sits on a column line (x), a
/// <see cref="Horizontal"/> one on a row line (y).</summary>
public enum RoostAxis
{
    Vertical = 0,
    Horizontal = 1,
}

/// <summary>A draggable divider: the maximal run of a grid line, from <see cref="Start"/> to <see cref="End"/> units
/// along it, with regions on both sides.</summary>
public readonly record struct RoostDivider(RoostAxis Axis, int Line, int Start, int End);

/// <summary>
/// A tab's painted layout (docs/roost-tabs-plan.md): regions that exactly tile a <see cref="Units"/> × <see cref="Units"/>
/// grid, each at least <see cref="MinSpan"/> units each way, at most <see cref="MaxRegions"/> of them. Immutable;
/// every edit returns a new layout (or null when it isn't allowed), and an edit can never produce an invalid one.
/// UI-free, unit-tested. <see cref="ToShape"/> hands it to the tile panel as an ordinary template shape.
/// </summary>
public sealed class RoostGridLayout
{
    /// <summary>The grid's size each way: halves, thirds, quarters and sixths all land on unit lines.</summary>
    public const int Units = 12;

    /// <summary>The smallest region span (1/6 of the stage).</summary>
    public const int MinSpan = 2;

    /// <summary>The most regions in one tab — each is a full thread, so this bounds the tab's cost.</summary>
    public const int MaxRegions = 8;

    private readonly RoostRegion[] _regions;

    private RoostGridLayout(RoostRegion[] regions)
    {
        _regions = regions;
        Signature = string.Join(";", regions.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.Row}.{r.Column}.{r.RowSpan}.{r.ColumnSpan}"));
    }

    /// <summary>One region filling the grid.</summary>
    public static readonly RoostGridLayout Full = new([new RoostRegion(0, 0, 0, Units, Units)]);

    /// <summary>The regions, in the order they were created.</summary>
    public IReadOnlyList<RoostRegion> Regions => _regions;

    /// <summary>The regions top-to-bottom, then left-to-right — the order a preset's sessions are mapped in.</summary>
    public IReadOnlyList<RoostRegion> ReadingOrder => _regions.OrderBy(r => r.Row).ThenBy(r => r.Column).ToList();

    /// <summary>A key that changes whenever the geometry or the ids do.</summary>
    public string Signature { get; }

    public RoostRegion? Find(int id) => Array.FindIndex(_regions, r => r.Id == id) is var i and >= 0 ? _regions[i] : null;

    /// <summary>A layout from persisted (or otherwise untrusted) regions, or null when they don't form a valid one.</summary>
    public static RoostGridLayout? Create(IEnumerable<RoostRegion>? regions)
    {
        if (regions is null) return null;
        var list = regions.ToArray();
        return IsValid(list) ? new RoostGridLayout(list) : null;
    }

    /// <summary>A persisted layout read back: anything invalid (hand-edited, or from a newer build) reads as
    /// <see cref="Full"/>.</summary>
    public static RoostGridLayout FromPersisted(IEnumerable<RoostRegion>? regions) => Create(regions) ?? Full;

    /// <summary>Whether <paramref name="regions"/> exactly tile the grid within the size and count limits, with
    /// distinct non-negative ids.</summary>
    public static bool IsValid(IReadOnlyList<RoostRegion> regions)
    {
        if (regions.Count is < 1 or > MaxRegions) return false;
        if (regions.Select(r => r.Id).Distinct().Count() != regions.Count) return false;
        var taken = new bool[Units, Units];
        int area = 0;
        foreach (var r in regions)
        {
            if (r.Id < 0 || r.Row < 0 || r.Column < 0 || r.RowSpan < MinSpan || r.ColumnSpan < MinSpan
                || r.Bottom > Units || r.Right > Units) return false;
            for (int y = r.Row; y < r.Bottom; y++)
                for (int x = r.Column; x < r.Right; x++)
                {
                    if (taken[y, x]) return false;
                    taken[y, x] = true;
                }
            area += r.Area;
        }
        return area == Units * Units;
    }

    // ── Edits ─────────────────────────────────────────────────────────────────

    /// <summary>Whether <see cref="Split"/> would succeed.</summary>
    public bool CanSplit(int id, RoostSplit how) => Split(id, how) is not null;

    /// <summary>
    /// Cuts region <paramref name="id"/> in two at the unit line nearest its middle. The region keeps its id on the
    /// left (or top) half; the other half gets a new id. Null when a half would fall under <see cref="MinSpan"/> or
    /// the layout already has <see cref="MaxRegions"/>.
    /// </summary>
    public RoostGridLayout? Split(int id, RoostSplit how)
    {
        if (_regions.Length >= MaxRegions || Find(id) is not { } r) return null;
        int span = how == RoostSplit.Columns ? r.ColumnSpan : r.RowSpan;
        int first = span / 2, second = span - first;
        if (first < MinSpan || second < MinSpan) return null;
        int newId = _regions.Max(x => x.Id) + 1;
        var (a, b) = how == RoostSplit.Columns
            ? (r with { ColumnSpan = first }, new RoostRegion(newId, r.Row, r.Column + first, r.RowSpan, second))
            : (r with { RowSpan = first }, new RoostRegion(newId, r.Row + first, r.Column, second, r.ColumnSpan));
        return With(_regions.Select(x => x.Id == id ? a : x).Append(b));
    }

    /// <summary>Whether <see cref="Remove"/> would succeed.</summary>
    public bool CanRemove(int id) => MergeTarget(id) is not null;

    /// <summary>
    /// Removes region <paramref name="id"/> (the painter's ×): the neighbour that together with it forms a rectangle
    /// grows into its space. Prefers the larger neighbour, then the one to the left / above. Null when no neighbour
    /// shares a whole side with it (or it's the only region).
    /// </summary>
    public RoostGridLayout? Remove(int id)
    {
        if (Find(id) is not { } r || MergeTarget(id) is not { } n) return null;
        bool sideBySide = n.Row == r.Row && n.RowSpan == r.RowSpan;
        var grown = sideBySide
            ? new RoostRegion(n.Id, r.Row, Math.Min(r.Column, n.Column), r.RowSpan, r.ColumnSpan + n.ColumnSpan)
            : new RoostRegion(n.Id, Math.Min(r.Row, n.Row), r.Column, r.RowSpan + n.RowSpan, r.ColumnSpan);
        return With(_regions.Where(x => x.Id != id).Select(x => x.Id == n.Id ? grown : x));
    }

    /// <summary>The region that would absorb <paramref name="id"/> on <see cref="Remove"/>, or null.</summary>
    public RoostRegion? MergeTarget(int id)
    {
        if (_regions.Length < 2 || Find(id) is not { } r) return null;
        return _regions
            .Where(n => n.Id != id && (
                (n.Row == r.Row && n.RowSpan == r.RowSpan && (n.Right == r.Column || r.Right == n.Column))
                || (n.Column == r.Column && n.ColumnSpan == r.ColumnSpan && (n.Bottom == r.Row || r.Bottom == n.Row))))
            .OrderByDescending(n => n.Area).ThenBy(n => n.Row).ThenBy(n => n.Column)
            .Cast<RoostRegion?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// Every draggable divider. A divider is the <em>smallest</em> run of an inner grid line that the regions touching
    /// it fit inside entirely — so it can move without bending any region. In a 2 × 2 the middle row line is two
    /// dividers (each half moves on its own); beside a tall region it's one, the tall region's full height.
    /// </summary>
    public IReadOnlyList<RoostDivider> Dividers => _dividers ??= FindDividers();

    private IReadOnlyList<RoostDivider>? _dividers;

    private List<RoostDivider> FindDividers()
    {
        var list = new List<RoostDivider>();
        foreach (var axis in new[] { RoostAxis.Vertical, RoostAxis.Horizontal })
        {
            bool v = axis == RoostAxis.Vertical;
            for (int line = 1; line < Units; line++)
            {
                var touching = _regions.Where(r => (v ? r.Right : r.Bottom) == line || (v ? r.Column : r.Row) == line).ToList();
                int u = 0;
                while (u < Units)
                {
                    if (!IsDividerAt(axis, line, u)) { u++; continue; }
                    // Grow [start, end) until every touching region overlapping it lies inside it.
                    int start = u, end = u + 1;
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        foreach (var r in touching)
                        {
                            int lo = v ? r.Row : r.Column, hi = v ? r.Bottom : r.Right;
                            if (hi <= start || lo >= end) continue;
                            if (lo < start) { start = lo; grew = true; }
                            if (hi > end) { end = hi; grew = true; }
                        }
                    }
                    list.Add(new RoostDivider(axis, line, start, end));
                    u = end;
                }
            }
        }
        return list;
    }

    /// <summary>The divider running through unit <paramref name="along"/> of <paramref name="line"/>, or null.</summary>
    public RoostDivider? DividerAt(RoostAxis axis, int line, int along) =>
        Dividers.Cast<RoostDivider?>().FirstOrDefault(d => d!.Value.Axis == axis && d.Value.Line == line
                                                            && along >= d.Value.Start && along < d.Value.End);

    /// <summary>How far <paramref name="divider"/> can move each way: (lowest line, highest line) that keeps every
    /// region on both sides at least <see cref="MinSpan"/>.</summary>
    public (int Min, int Max) DividerRange(RoostDivider divider)
    {
        var (before, after) = Sides(divider);
        bool v = divider.Axis == RoostAxis.Vertical;
        int min = before.Max(r => (v ? r.Column : r.Row) + MinSpan);
        int max = after.Min(r => (v ? r.Right : r.Bottom) - MinSpan);
        return (min, max);
    }

    /// <summary>
    /// Drags <paramref name="divider"/> to grid line <paramref name="to"/>, clamped to <see cref="DividerRange"/> (so
    /// a drag past the limit stops at the last valid line). Every region along the divider moves its edge with it.
    /// Null when <paramref name="divider"/> isn't one of this layout's.
    /// </summary>
    public RoostGridLayout? MoveDivider(RoostDivider divider, int to)
    {
        if (!Dividers.Contains(divider)) return null;
        var (min, max) = DividerRange(divider);
        to = Math.Clamp(to, min, max);
        if (to == divider.Line) return this;
        var (before, after) = Sides(divider);
        var beforeIds = before.Select(r => r.Id).ToHashSet();
        var afterIds = after.Select(r => r.Id).ToHashSet();
        bool v = divider.Axis == RoostAxis.Vertical;
        return With(_regions.Select(r =>
            beforeIds.Contains(r.Id) ? (v ? r with { ColumnSpan = to - r.Column } : r with { RowSpan = to - r.Row })
            : afterIds.Contains(r.Id) ? (v ? r with { Column = to, ColumnSpan = r.Right - to } : r with { Row = to, RowSpan = r.Bottom - to })
            : r));
    }

    // The regions ending on the divider's line (left / above) and starting on it (right / below), within its run.
    private (List<RoostRegion> Before, List<RoostRegion> After) Sides(RoostDivider d)
    {
        bool v = d.Axis == RoostAxis.Vertical;
        bool InRun(RoostRegion r) => v ? r.Row < d.End && r.Bottom > d.Start : r.Column < d.End && r.Right > d.Start;
        var before = _regions.Where(r => (v ? r.Right : r.Bottom) == d.Line && InRun(r)).ToList();
        var after = _regions.Where(r => (v ? r.Column : r.Row) == d.Line && InRun(r)).ToList();
        return (before, after);
    }

    // A divider sits at (line, u) when one region ends on the line there and another starts on it.
    private bool IsDividerAt(RoostAxis axis, int line, int u)
    {
        bool v = axis == RoostAxis.Vertical;
        bool Covers(RoostRegion r) => v ? r.Row <= u && u < r.Bottom : r.Column <= u && u < r.Right;
        return _regions.Any(r => (v ? r.Right : r.Bottom) == line && Covers(r))
               && _regions.Any(r => (v ? r.Column : r.Row) == line && Covers(r));
    }

    // Every edit is checked: a layout built here is valid, or the edit is refused.
    private static RoostGridLayout? With(IEnumerable<RoostRegion> regions) => Create(regions);

    // ── Presets & drawing ─────────────────────────────────────────────────────

    /// <summary>
    /// A snap template as a layout (the painter's built-in presets): each column and row boundary is its share of
    /// the star weights, rounded to the nearest unit line (so 60 / 40 becomes 7 / 5). Ids follow the template's fill
    /// order. Auto has no grid of its own and reads as <see cref="Full"/>.
    /// </summary>
    public static RoostGridLayout FromTemplate(RoostSnapTemplate template)
    {
        if (template == RoostSnapTemplate.Auto) return Full;
        var shape = RoostTemplates.Shape(template);
        var xs = Boundaries(shape.Columns);
        var ys = Boundaries(shape.Rows);
        var regions = shape.Slots.Select((s, i) => new RoostRegion(i,
            ys[s.Row], xs[s.Column], ys[s.Row + s.RowSpan] - ys[s.Row], xs[s.Column + s.ColumnSpan] - xs[s.Column]));
        return Create(regions) ?? Full;
    }

    /// <summary>The built-in presets, in the snap flyout's order (Auto left out).</summary>
    public static IReadOnlyList<(RoostSnapTemplate Template, string Name, RoostGridLayout Layout)> Presets { get; } =
        RoostTemplates.Picker.Where(t => t != RoostSnapTemplate.Auto)
            .Select(t => (t, RoostTemplates.Name(t), FromTemplate(t))).ToList();

    // Unit lines for star weights: 0, each cumulative share rounded, then Units.
    private static int[] Boundaries(IReadOnlyList<double> weights)
    {
        double sum = weights.Sum(), run = 0;
        var lines = new int[weights.Count + 1];
        for (int i = 0; i < weights.Count; i++)
        {
            run += weights[i];
            lines[i + 1] = (int)Math.Round(run / sum * Units, MidpointRounding.AwayFromZero);
        }
        lines[^1] = Units;
        return lines;
    }

    /// <summary>
    /// The layout as a tile-panel shape on a uniform unit grid. Slot <c>i</c> is <see cref="Regions"/>[<c>i</c>];
    /// <see cref="SlotOf"/> maps a region id to it.
    /// </summary>
    public RoostTemplateShape ToShape() => new(
        Enumerable.Repeat(1.0, Units).ToArray(), Enumerable.Repeat(1.0, Units).ToArray(),
        _regions.Select(r => new RoostSlot(r.Row, r.Column, r.RowSpan, r.ColumnSpan)).ToArray());

    /// <summary>
    /// This layout (a preset or a saved one) renumbered to take over <paramref name="previous"/>'s region ids in
    /// reading order: its first region (top-left) gets the id of <paramref name="previous"/>'s first, and so on.
    /// Regions past <paramref name="previous"/>'s count get fresh ids. A tab's sessions then follow the swap by
    /// reading position, and the extras' sessions go back to the rail (D9).
    /// </summary>
    public RoostGridLayout AdoptIds(RoostGridLayout previous)
    {
        var from = previous.ReadingOrder;
        var to = ReadingOrder;
        int next = previous._regions.Max(r => r.Id) + 1;
        var ids = new Dictionary<int, int>();
        for (int i = 0; i < to.Count; i++) ids[to[i].Id] = i < from.Count ? from[i].Id : next++;
        return Create(_regions.Select(r => r with { Id = ids[r.Id] }))!;
    }

    /// <summary>The <see cref="ToShape"/> slot of region <paramref name="id"/>, or -1.</summary>
    public int SlotOf(int id) => Array.FindIndex(_regions, r => r.Id == id);
}
