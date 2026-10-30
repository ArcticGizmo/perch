namespace Perch.Data.GitGraph;

/// <summary>
/// Assigns each commit a lane and the edges joining it to the rows around it. Commits must arrive in topological
/// order (children before parents), and may arrive page by page: the allocator keeps its state between
/// <see cref="Add(IReadOnlyList{GraphCommit})"/> calls, so paging gives exactly the rows one big batch would.
///
/// <para>The model: each lane waits for one commit hash. A commit takes the leftmost lane waiting for it (any other
/// waiters converge into it in the band just above the node); its first parent continues in its lane, and each
/// further parent joins a lane already waiting for it or opens a new one in the band just below. Lanes are never
/// compacted, so a branch keeps a straight line, and freed lanes are reused. The primary branch's first-parent
/// chain is pinned to lane 0.</para>
///
/// <para>Stability across refreshes comes from <see cref="Options.Hints"/> (each commit's lane in the previous
/// layout; see <see cref="HintsFrom"/>). A lane that starts at a commit, or at a commit a few first-parent steps
/// below it in the same page, takes its previous lane when that's free; a lane with no usable hint opens after
/// every hinted lane, so a new branch can't push an existing one sideways.</para>
///
/// <para>Not thread-safe; owned by one loader.</para>
/// </summary>
internal sealed class LaneAllocator
{
    /// <summary>How far down a new tip's first-parent chain to look for a hinted commit: covers a burst of new
    /// commits on an existing branch between two refreshes.</summary>
    internal const int HintLookahead = 64;

    internal sealed record Options(string? PrimaryTip = null, IReadOnlyDictionary<string, int>? Hints = null);

    // The hash each lane waits for; null is a free lane.
    private readonly List<string?> _lanes = new();
    private readonly List<GraphRow> _rows = new();
    private readonly IReadOnlyDictionary<string, int>? _hints;
    private readonly int _firstUnhinted;
    private string? _primaryNext;
    private readonly bool _hasPrimary;

    // Edges in the band below the last row, whose lower ends depend on the next commit.
    private List<(int From, int Lane)> _pending = new();

    // The current page, for the hint lookahead.
    private Dictionary<string, GraphCommit>? _page;

    public LaneAllocator(Options? options = null)
    {
        _primaryNext = options?.PrimaryTip;
        _hasPrimary = _primaryNext is not null;
        _hints = options?.Hints is { Count: > 0 } h ? h : null;
        _firstUnhinted = _hints is null ? 0 : _hints.Values.Max() + 1;
    }

    public IReadOnlyList<GraphRow> Rows => _rows;

    /// <summary>The widest the layout has been, in lanes.</summary>
    public int Width { get; private set; }

    /// <summary>The lines leaving the bottom of the last row towards commits not loaded yet, drawn straight down.</summary>
    public GraphEdge[] OpenEdges =>
        _pending.Select(p => new GraphEdge((short)p.From, (short)p.Lane, (short)p.Lane)).ToArray();

    public void Add(IReadOnlyList<GraphCommit> page)
    {
        _page = _hints is null ? null : page.ToDictionary(c => c.Hash, StringComparer.Ordinal);
        foreach (var c in page)
            Add(c);
        _page = null;
    }

    public void Add(GraphCommit c)
    {
        bool primary = _primaryNext is not null && c.Hash == _primaryNext;
        if (primary)
            _primaryNext = c.Parents.Length > 0 ? c.Parents[0] : null;

        int lane = -1;
        if (primary && (FreeAt(0) || _lanes[0] == c.Hash))
            lane = 0;
        else
            for (int i = 0; i < _lanes.Count; i++)
                if (_lanes[i] == c.Hash) { lane = i; break; }
        if (lane < 0)
            lane = Allocate(TipHint(c));

        var up = new List<GraphEdge>(_pending.Count);
        foreach (var (from, l) in _pending)
        {
            var e = new GraphEdge((short)from, (short)(_lanes[l] == c.Hash ? lane : l), (short)l);
            if (!up.Contains(e)) up.Add(e);
        }

        for (int i = 0; i < _lanes.Count; i++)
            if (_lanes[i] == c.Hash) _lanes[i] = null;
        Set(lane, c.Parents.Length > 0 ? c.Parents[0] : null);

        var next = new List<(int From, int Lane)>(_lanes.Count + c.Parents.Length);
        List<int>? opened = null;
        for (int k = 1; k < c.Parents.Length; k++)
        {
            var p = c.Parents[k];
            int j = _lanes.IndexOf(p);
            if (j < 0)
            {
                j = Allocate(Hint(p));
                Set(j, p);
                (opened ??= new()).Add(j);
            }
            else if (j != lane)
                next.Add((lane, j));     // joins a lane already waiting for that parent
        }
        for (int i = 0; i < _lanes.Count; i++)
            if (_lanes[i] is not null)
                next.Add((opened?.Contains(i) == true ? lane : i, i));
        _pending = next;

        while (_lanes.Count > 0 && _lanes[^1] is null)
            _lanes.RemoveAt(_lanes.Count - 1);
        Width = Math.Max(Width, Math.Max(_lanes.Count, lane + 1));
        _rows.Add(new GraphRow(lane, up.ToArray()));
    }

    /// <summary>Every commit's lane in a finished layout, as the <see cref="Options.Hints"/> for the next one.</summary>
    public static Dictionary<string, int> HintsFrom(IReadOnlyList<GraphCommit> commits, IReadOnlyList<GraphRow> rows)
    {
        var hints = new Dictionary<string, int>(Math.Min(commits.Count, rows.Count), StringComparer.Ordinal);
        for (int i = 0; i < commits.Count && i < rows.Count; i++)
            hints[commits[i].Hash] = rows[i].Lane;
        return hints;
    }

    private bool FreeAt(int i) => i >= _lanes.Count || _lanes[i] is null;

    private void Set(int i, string? hash)
    {
        while (_lanes.Count <= i) _lanes.Add(null);
        _lanes[i] = hash;
    }

    private int Allocate(int? hint)
    {
        int floor = _hasPrimary ? 1 : 0;
        if (hint is int h && h >= floor && FreeAt(h))
            return h;
        for (int i = Math.Max(floor, _firstUnhinted); ; i++)
            if (FreeAt(i)) return i;
    }

    private int? Hint(string hash) => _hints is not null && _hints.TryGetValue(hash, out var l) ? l : null;

    // A new tip with no hint of its own is often new commits on top of a known branch: look down its first-parent
    // chain within this page for one that had a lane.
    private int? TipHint(GraphCommit c)
    {
        if (_hints is null)
            return null;
        var cur = c;
        for (int i = 0; i < HintLookahead && cur is not null; i++)
        {
            if (Hint(cur.Hash) is int l) return l;
            if (cur.Parents.Length == 0 || _page is null || !_page.TryGetValue(cur.Parents[0], out cur)) break;
        }
        return null;
    }
}
