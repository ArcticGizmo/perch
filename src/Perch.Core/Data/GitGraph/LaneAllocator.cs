namespace Perch.Data.GitGraph;

/// <summary>
/// Assigns each commit a lane and the edges joining it to the rows around it. Commits must arrive in topological
/// order (children before parents), and may arrive page by page: the allocator keeps its state between
/// <see cref="Add(IReadOnlyList{GraphCommit})"/> calls, so paging gives exactly the rows one big batch would.
///
/// <para>The model: each lane waits for one commit hash. A commit takes the leftmost lane waiting for it (any other
/// waiters converge into it in the band just above the node); its first parent continues in its lane, and each
/// further parent joins a lane already waiting for it or opens a new one in the band just below. The primary
/// branch's first-parent chain is pinned to lane 0.</para>
///
/// <para>A new lane always opens in the leftmost free lane, so a line only sits further out when something to its left
/// is still running beside it; a branch keeps its lane once it has one, so lines don't wander. The layout is a pure
/// function of the commits: hiding a branch closes up the gap it left, and a new branch only pushes lines aside where
/// it actually runs beside them.</para>
///
/// <para>Not thread-safe; owned by one loader.</para>
/// </summary>
internal sealed class LaneAllocator
{
    internal sealed record Options(string? PrimaryTip = null);

    // The hash each lane waits for; null is a free lane.
    private readonly List<string?> _lanes = new();
    private readonly List<GraphRow> _rows = new();
    private string? _primaryNext;
    private readonly bool _hasPrimary;

    // Edges in the band below the last row, whose lower ends depend on the next commit.
    private List<(int From, int Lane)> _pending = new();
    // A row's edges so far, to drop duplicates in linear time; reused across rows.
    private readonly HashSet<GraphEdge> _seenEdges = new();

    public LaneAllocator(Options? options = null)
    {
        _primaryNext = options?.PrimaryTip;
        _hasPrimary = _primaryNext is not null;
    }

    public IReadOnlyList<GraphRow> Rows => _rows;

    /// <summary>The widest the layout has been, in lanes.</summary>
    public int Width { get; private set; }

    /// <summary>The lines leaving the bottom of the last row towards commits not loaded yet, drawn straight down.</summary>
    public GraphEdge[] OpenEdges =>
        _pending.Select(p => new GraphEdge((short)p.From, (short)p.Lane, (short)p.Lane)).ToArray();

    public void Add(IReadOnlyList<GraphCommit> page)
    {
        foreach (var c in page)
            Add(c);
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
            lane = Allocate();

        var up = new List<GraphEdge>(_pending.Count);
        _seenEdges.Clear();
        foreach (var (from, l) in _pending)
        {
            var e = new GraphEdge((short)from, (short)(_lanes[l] == c.Hash ? lane : l), (short)l);
            if (_seenEdges.Add(e)) up.Add(e);
        }

        for (int i = 0; i < _lanes.Count; i++)
            if (_lanes[i] == c.Hash) _lanes[i] = null;
        Set(lane, c.Parents.Length > 0 ? c.Parents[0] : null);

        var next = new List<(int From, int Lane)>(_lanes.Count + c.Parents.Length);
        List<int>? opened = null;
        for (int k = 1; k < c.Parents.Length; k++)
        {
            var p = c.Parents[k];
            // A parent on the primary chain will land in lane 0, so the merge line heads there rather than joining
            // some other branch's lane that happens to be waiting for it too.
            bool toPrimary = p == _primaryNext && FreeAt(0);
            int j = toPrimary ? 0 : _lanes.IndexOf(p);
            if (toPrimary || j < 0)
            {
                if (!toPrimary)
                    j = Allocate();
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

    private bool FreeAt(int i) => i >= _lanes.Count || _lanes[i] is null;

    private void Set(int i, string? hash)
    {
        while (_lanes.Count <= i) _lanes.Add(null);
        _lanes[i] = hash;
    }

    // Lane 0 is kept for the primary chain whenever there is one.
    private int Allocate()
    {
        for (int i = _hasPrimary ? 1 : 0; ; i++)
            if (FreeAt(i)) return i;
    }
}
