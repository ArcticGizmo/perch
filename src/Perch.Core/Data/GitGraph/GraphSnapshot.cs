namespace Perch.Data.GitGraph;

/// <summary>A dirty worktree's dashed line: from its WIP row down its own lane to the row of its HEAD commit
/// (<see cref="int.MaxValue"/> while that commit isn't loaded yet).</summary>
public readonly record struct GraphTether(int WipRow, int HeadRow, int Lane, GraphWorktree Worktree);

/// <summary>
/// One immutable, laid-out view of a repo's graph for a scope: the rows in display order (a WIP pseudo-commit per
/// dirty worktree first, then the log), their lanes and edges, and lookups from a commit to its refs and the
/// worktrees whose HEAD it is. Safe to read from the UI thread while the next page is laid out:
/// <see cref="Append"/> hands the allocator on to a new snapshot and leaves this one untouched.
/// </summary>
internal sealed class GraphSnapshot
{
    private readonly LaneAllocator _lanes;
    private readonly Dictionary<string, int> _rowOf;
    private readonly Dictionary<string, List<GraphRef>> _refsAt;
    private readonly Dictionary<string, List<GraphWorktree>> _worktreesAt;
    private readonly string? _primaryTip;
    private bool _handedOn;

    private GraphSnapshot(string root, GraphScope scope, GraphLogQuery query, GraphCommit[] commits, int realCount,
        bool hasMore, IReadOnlyList<GraphRef> refs, IReadOnlyList<GraphWorktree> worktrees, string? primaryTip,
        LaneAllocator lanes, Dictionary<string, int> rowOf, ScopeContext context)
    {
        Root = root;
        Scope = scope;
        Query = query;
        Context = context;
        Commits = commits;
        RealCommitCount = realCount;
        HasMore = hasMore;
        Refs = refs;
        Worktrees = worktrees;
        _primaryTip = primaryTip;
        _lanes = lanes;
        _rowOf = rowOf;
        Rows = [.. lanes.Rows];
        Width = lanes.Width;
        OpenEdges = lanes.OpenEdges;

        _refsAt = new(StringComparer.Ordinal);
        foreach (var r in refs)
            if (!context.Hidden.Contains(r.FullName))
                (_refsAt.TryGetValue(r.Target, out var l) ? l : _refsAt[r.Target] = new()).Add(r);
        _worktreesAt = new(StringComparer.Ordinal);
        foreach (var w in worktrees)
            if (w.Head is not null)
                (_worktreesAt.TryGetValue(w.Head, out var l) ? l : _worktreesAt[w.Head] = new()).Add(w);

        var tethers = new List<GraphTether>();
        for (int i = 0; i < commits.Length && commits[i].IsWorkInProgress; i++)
        {
            var wt = worktrees.First(w => w.WipHash == commits[i].Hash);
            tethers.Add(new(i, RowOf(commits[i].Parents[0]) is var h and >= 0 ? h : int.MaxValue, Rows[i].Lane, wt));
        }
        Tethers = tethers;
    }

    /// <summary>What the loader learned on the way that the window needs too: the current worktree's full status
    /// (for the staging panes) and, for <see cref="GraphScope.Branch"/>, the base ref it scoped against and the
    /// candidates it chose from.</summary>
    public sealed record ScopeContext(GitRepoStatus? CurrentStatus, string? BaseRef, IReadOnlyList<string> BaseCandidates)
    {
        /// <summary>A merge, rebase or cherry-pick the current worktree has stopped in the middle of.</summary>
        public GitInProgress InProgress { get; init; }

        /// <summary>Branches (full ref names) left out of the walk and drawn without pills; the rail still lists them.</summary>
        public IReadOnlySet<string> Hidden { get; init; } = new HashSet<string>();

        public static readonly ScopeContext None = new(null, null, []);
    }

    public bool IsHidden(GraphRef r) => Context.Hidden.Contains(r.FullName);

    /// <summary>The worktree the git commands run in (the one the window was opened on).</summary>
    public string Root { get; }
    public GraphScope Scope { get; }
    public GraphLogQuery Query { get; }
    public ScopeContext Context { get; }

    /// <summary>Every row's commit, in display order: WIP pseudo-commits first.</summary>
    public IReadOnlyList<GraphCommit> Commits { get; }
    public GraphRow[] Rows { get; }
    public int Width { get; }

    /// <summary>Lines leaving the last row towards commits not loaded yet.</summary>
    public GraphEdge[] OpenEdges { get; }

    /// <summary>Real commits loaded so far (excludes WIP rows): the <c>--skip</c> for the next page.</summary>
    public int RealCommitCount { get; }

    /// <summary>The last page came back full, so there may be older commits to load.</summary>
    public bool HasMore { get; }

    public IReadOnlyList<GraphRef> Refs { get; }
    public IReadOnlyList<GraphWorktree> Worktrees { get; }
    public IReadOnlyList<GraphTether> Tethers { get; }

    public int RowOf(string hash) => _rowOf.TryGetValue(hash, out var r) ? r : -1;

    public IReadOnlyList<GraphRef> RefsAt(string hash) => _refsAt.TryGetValue(hash, out var l) ? l : [];

    /// <summary>The worktrees whose HEAD is <paramref name="hash"/>.</summary>
    public IReadOnlyList<GraphWorktree> WorktreesAt(string hash) => _worktreesAt.TryGetValue(hash, out var l) ? l : [];

    /// <summary>The worktree the window was opened on, if the repo reported one.</summary>
    public GraphWorktree? Current => Worktrees.FirstOrDefault(w => w.IsCurrent);

    /// <summary>The row to land on with nothing else to go by: the current worktree's WIP row when it's dirty,
    /// else its HEAD commit, else the top.</summary>
    public int HomeRow()
    {
        if (Current is not { } w) return Rows.Length > 0 ? 0 : -1;
        int r = RowOf(w.WipHash);
        if (r < 0 && w.Head is not null) r = RowOf(w.Head);
        return r >= 0 ? r : Rows.Length > 0 ? 0 : -1;
    }

    /// <summary>
    /// The worktree <paramref name="dir"/> is in: the one whose folder is <paramref name="dir"/> or the deepest one
    /// above it, so a worktree nested inside another (<c>.claude/worktrees/x</c>) wins over its host. Compared as
    /// strings, with no file access, so it's cheap enough for every session scan.
    /// </summary>
    public GraphWorktree? WorktreeContaining(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
        var d = Norm(dir);
        GraphWorktree? best = null;
        foreach (var w in Worktrees)
        {
            var p = Norm(w.Path);
            if ((d.Equals(p, cmp) || d.StartsWith(p + "/", cmp)) && (best is null || p.Length > Norm(best.Path).Length))
                best = w;
        }
        return best;
    }

    /// <summary>The lane of the row <paramref name="hash"/> sits on, or -1 when it isn't loaded.</summary>
    public int LaneOf(string hash) => RowOf(hash) is var r and >= 0 ? Rows[r].Lane : -1;

    /// <summary>
    /// The row "go to" lands on for <paramref name="query"/>: a ref named exactly that (ignoring case), else a
    /// commit whose hash starts with it (4+ characters), else the next row after <paramref name="afterRow"/> whose
    /// subject or author contains it, wrapping round, so pressing Enter again walks the matches. -1 for none.
    /// </summary>
    public int Find(string query, int afterRow)
    {
        query = query.Trim();
        if (query.Length == 0 || Commits.Count == 0)
            return -1;
        foreach (var r in Refs)
            if (string.Equals(r.Name, query, StringComparison.OrdinalIgnoreCase) && RowOf(r.Target) is var row and >= 0)
                return row;
        if (query.Length >= 4 && query.All(Uri.IsHexDigit))
            for (int i = 0; i < Commits.Count; i++)
                if (Commits[i].Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    return i;
        for (int k = 1; k <= Commits.Count; k++)
        {
            int i = ((afterRow < 0 ? -1 : afterRow) + k) % Commits.Count;
            var c = Commits[i];
            if (!c.IsWorkInProgress && (c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        || c.AuthorName.Contains(query, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Every loaded row matching <paramref name="query"/> (ignoring case), top to bottom: its message or author contains
    /// it, its hash starts with it (4+ hex characters), or a branch or tag shown on it has it in its name. Empty for a
    /// blank query. The find bar walks these and washes them.
    /// </summary>
    public List<int> FindAll(string query)
    {
        var rows = new List<int>();
        query = query.Trim();
        if (query.Length == 0) return rows;
        bool hex = query.Length >= 4 && query.All(Uri.IsHexDigit);
        for (int i = 0; i < Commits.Count; i++)
        {
            var c = Commits[i];
            if (c.IsWorkInProgress) continue;
            if (c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.AuthorName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (hex && c.Hash.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                || RefsAt(c.Hash).Any(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                rows.Add(i);
        }
        return rows;
    }

    /// <summary>The worktree whose WIP row is <paramref name="row"/>, or null for a commit row.</summary>
    public GraphWorktree? WipAt(int row)
    {
        foreach (var t in Tethers)
            if (t.WipRow == row) return t.Worktree;
        return null;
    }

    /// <summary>True when <paramref name="e"/>, in the band above <paramref name="row"/>, is part of a WIP tether
    /// and should be drawn dashed.</summary>
    public bool IsTether(int row, GraphEdge e)
    {
        foreach (var t in Tethers)
            if (row > t.WipRow && row <= t.HeadRow && e.Color == t.Lane)
                return true;
        return false;
    }

    /// <summary>Every row's lane, as hints for the next layout of the same scope.</summary>
    public Dictionary<string, int> Hints() => LaneAllocator.HintsFrom(Commits, Rows);

    /// <summary>
    /// Lays out <paramref name="log"/> (one page, topological order) under a WIP row per dirty worktree, ordered by
    /// where its HEAD sits. The primary branch's chain gets lane 0, starting from its worktree's WIP row when that
    /// worktree is dirty.
    /// </summary>
    public static GraphSnapshot Build(string root, GraphScope scope, GraphLogQuery query, IReadOnlyList<GraphCommit> log,
        IReadOnlyList<GraphRef> refs, IReadOnlyList<GraphWorktree> worktrees, string? primaryBranch,
        IReadOnlyDictionary<string, int>? hints, int pageSize, ScopeContext? context = null)
    {
        var logRow = new Dictionary<string, int>(log.Count, StringComparer.Ordinal);
        for (int i = 0; i < log.Count; i++)
            logRow[log[i].Hash] = i;

        var wips = worktrees
            .Where(w => w.Changes > 0 && w.Head is not null)
            .OrderBy(w => logRow.TryGetValue(w.Head!, out var r) ? r : int.MaxValue)
            .Select(w => new GraphCommit(w.WipHash, [w.Head!], "", "", 0, $"{w.Changes} uncommitted"))
            .ToList();

        string? primaryTip = null;
        if (primaryBranch is not null)
            primaryTip = worktrees.FirstOrDefault(w => w.Branch == primaryBranch && w.Changes > 0 && w.Head is not null)?.WipHash
                ?? refs.FirstOrDefault(r => r.Name == primaryBranch && r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch)?.Target;

        var commits = new GraphCommit[wips.Count + log.Count];
        wips.CopyTo(commits);
        for (int i = 0; i < log.Count; i++)
            commits[wips.Count + i] = log[i];
        var rowOf = new Dictionary<string, int>(commits.Length, StringComparer.Ordinal);
        for (int i = 0; i < commits.Length; i++)
            rowOf[commits[i].Hash] = i;

        var lanes = new LaneAllocator(new LaneAllocator.Options(primaryTip, hints));
        lanes.Add(commits);
        return new GraphSnapshot(root, scope, query, commits, log.Count, log.Count >= pageSize, refs, worktrees,
            primaryTip, lanes, rowOf, context ?? ScopeContext.None);
    }

    /// <summary>
    /// A new snapshot with <paramref name="page"/> (the next page of the same query) laid out below this one. The
    /// lane state moves on to the new snapshot, so a snapshot can be appended to once.
    /// </summary>
    public GraphSnapshot Append(IReadOnlyList<GraphCommit> page, int pageSize)
    {
        if (_handedOn)
            throw new InvalidOperationException("This snapshot has already been appended to.");
        _handedOn = true;

        var commits = new GraphCommit[Commits.Count + page.Count];
        for (int i = 0; i < Commits.Count; i++)
            commits[i] = Commits[i];
        var rowOf = new Dictionary<string, int>(_rowOf, StringComparer.Ordinal);
        for (int i = 0; i < page.Count; i++)
        {
            commits[Commits.Count + i] = page[i];
            rowOf[page[i].Hash] = Commits.Count + i;
        }
        _lanes.Add(page);
        return new GraphSnapshot(Root, Scope, Query, commits, RealCommitCount + page.Count, page.Count >= pageSize,
            Refs, Worktrees, _primaryTip, _lanes, rowOf, Context);
    }
}
