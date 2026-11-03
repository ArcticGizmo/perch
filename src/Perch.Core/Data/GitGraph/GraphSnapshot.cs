namespace Perch.Data.GitGraph;

/// <summary>A dirty worktree's dashed line: from its WIP row down its own lane to the row of its HEAD commit
/// (<see cref="int.MaxValue"/> while that commit isn't loaded yet).</summary>
public readonly record struct GraphTether(int WipRow, int HeadRow, int Lane, GraphWorktree Worktree);

/// <summary>A stash entry's dashed line: from its row down its own lane to the commit it was made on.</summary>
public readonly record struct GraphStashTether(int StashRow, int BaseRow, int Lane, GraphStash Stash);

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
        bool hasMore, IReadOnlyList<GraphRef> refs, IReadOnlyList<GraphWorktree> worktrees, IReadOnlyList<GraphStash> stashes,
        string? primaryTip, LaneAllocator lanes, Dictionary<string, int> rowOf, ScopeContext context)
    {
        Stashes = stashes;
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
        int i = 0;
        for (; i < commits.Length && commits[i].IsWorkInProgress; i++)
        {
            var wt = worktrees.First(w => w.WipHash == commits[i].Hash);
            tethers.Add(new(i, RowOf(commits[i].Parents[0]) is var h and >= 0 ? h : int.MaxValue, Rows[i].Lane, wt));
        }
        Tethers = tethers;
        var stashTethers = new List<GraphStashTether>();
        foreach (var st in stashes)
            if (RowOf(st.RowHash) is var s and >= 0)
                stashTethers.Add(new(s, RowOf(st.Base), Rows[s].Lane, st));
        stashTethers.Sort((a, b) => a.StashRow.CompareTo(b.StashRow));
        StashTethers = stashTethers;
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

    /// <summary>Every row's commit, in display order: WIP pseudo-commits first, then the log with stash pseudo-commits
    /// woven in by when they were made.</summary>
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

    /// <summary>Every stash entry, newest first, whether or not it has a row (one made on a commit outside what's loaded
    /// has none).</summary>
    public IReadOnlyList<GraphStash> Stashes { get; }

    /// <summary>The stashes that have rows, top to bottom.</summary>
    public IReadOnlyList<GraphStashTether> StashTethers { get; }

    /// <summary>The stash entry whose row is <paramref name="row"/>, or null.</summary>
    public GraphStash? StashAt(int row)
    {
        foreach (var t in StashTethers)
            if (t.StashRow == row) return t.Stash;
        return null;
    }

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

    /// <summary>True when <paramref name="e"/>, in the band above <paramref name="row"/>, is part of a WIP or stash
    /// tether and should be drawn dashed.</summary>
    public bool IsTether(int row, GraphEdge e)
    {
        foreach (var t in Tethers)
            if (row > t.WipRow && row <= t.HeadRow && e.Color == t.Lane)
                return true;
        foreach (var t in StashTethers)
            if (row > t.StashRow && row <= t.BaseRow && e.Color == t.Lane)
                return true;
        return false;
    }

    /// <summary>Every row's lane, as hints for the next layout of the same scope.</summary>
    public Dictionary<string, int> Hints() => LaneAllocator.HintsFrom(Commits, Rows);

    /// <summary>
    /// Where lane 0 starts: the window's own worktree (its WIP row when dirty, else its HEAD), so the history being
    /// worked on is the straight line down the left and everything else branches off it. With no current worktree it's
    /// <paramref name="primaryBranch"/> (the trunk), again from its worktree's WIP row when that's dirty.
    /// </summary>
    internal static string? PrimaryTip(IReadOnlyList<GraphWorktree> worktrees, IReadOnlyList<GraphRef> refs, string? primaryBranch)
    {
        if (worktrees.FirstOrDefault(w => w.IsCurrent && w.Head is not null) is { } here)
            return here.Changes > 0 ? here.WipHash : here.Head;
        if (primaryBranch is null)
            return null;
        return worktrees.FirstOrDefault(w => w.Branch == primaryBranch && w.Changes > 0 && w.Head is not null)?.WipHash
            ?? refs.FirstOrDefault(r => r.Name == primaryBranch && r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch)?.Target;
    }

    /// <summary>
    /// Lays out <paramref name="log"/> (one page, topological order) under a WIP row per dirty worktree, the current
    /// one first and the rest ordered by where their HEADs sit, with a row per stash made on a commit in the page woven
    /// in by time (<see cref="WithStashes"/>). Lane 0 follows <see cref="PrimaryTip"/>'s first-parent chain.
    /// </summary>
    public static GraphSnapshot Build(string root, GraphScope scope, GraphLogQuery query, IReadOnlyList<GraphCommit> log,
        IReadOnlyList<GraphRef> refs, IReadOnlyList<GraphWorktree> worktrees, string? primaryBranch,
        IReadOnlyDictionary<string, int>? hints, int pageSize, ScopeContext? context = null,
        IReadOnlyList<GraphStash>? stashes = null)
    {
        stashes ??= [];
        var logRow = new Dictionary<string, int>(log.Count, StringComparer.Ordinal);
        for (int i = 0; i < log.Count; i++)
            logRow[log[i].Hash] = i;

        // The window's own changes are the top row; other worktrees' follow in the order of their HEADs.
        var pseudo = worktrees
            .Where(w => w.Changes > 0 && w.Head is not null)
            .OrderBy(w => w.IsCurrent ? -1 : logRow.TryGetValue(w.Head!, out var r) ? r : int.MaxValue)
            .Select(w => new GraphCommit(w.WipHash, [w.Head!], "", "", 0, $"{w.Changes} uncommitted"))
            .ToList();
        refs = stashes.Count == 0 ? refs : [.. refs, .. stashes.Select(st => st.AsRef())];

        string? primaryTip = PrimaryTip(worktrees, refs, primaryBranch);

        var woven = WithStashes(log, stashes);
        var commits = new GraphCommit[pseudo.Count + woven.Count];
        pseudo.CopyTo(commits);
        woven.CopyTo(commits, pseudo.Count);
        var rowOf = new Dictionary<string, int>(commits.Length, StringComparer.Ordinal);
        for (int i = 0; i < commits.Length; i++)
            rowOf[commits[i].Hash] = i;

        var lanes = new LaneAllocator(new LaneAllocator.Options(primaryTip, hints));
        lanes.Add(commits);
        return new GraphSnapshot(root, scope, query, commits, log.Count, log.Count >= pageSize, refs, worktrees, stashes,
            primaryTip, lanes, rowOf, context ?? ScopeContext.None);
    }

    /// <summary>
    /// <paramref name="page"/> with a row for each stash made on one of its commits, placed by time: just above the first
    /// commit that's no newer than the stash, so an old stash sits down among the history it was made in and a fresh one
    /// near the top. A stash never goes below its own commit (the layout needs it above its parent), which also covers a
    /// clock or rebase that put the commits' times out of order. Stashes sharing a spot go newest first. One made on a
    /// commit outside the page gets no row here; the rail still lists it.
    /// </summary>
    internal static List<GraphCommit> WithStashes(IReadOnlyList<GraphCommit> page, IEnumerable<GraphStash> stashes)
    {
        var placed = new List<(int At, GraphStash Stash)>();
        Dictionary<string, int>? rowOf = null;
        foreach (var st in stashes)
        {
            if (rowOf is null)
            {
                rowOf = new Dictionary<string, int>(page.Count, StringComparer.Ordinal);
                for (int i = 0; i < page.Count; i++) rowOf.TryAdd(page[i].Hash, i);
            }
            if (!rowOf.TryGetValue(st.Base, out var baseRow)) continue;
            int at = baseRow;
            for (int i = 0; i < baseRow; i++)
                if (page[i].UnixTime <= st.UnixTime) { at = i; break; }
            placed.Add((at, st));
        }
        if (placed.Count == 0) return [.. page];

        placed.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : b.Stash.UnixTime.CompareTo(a.Stash.UnixTime));
        var woven = new List<GraphCommit>(page.Count + placed.Count);
        int next = 0;
        for (int i = 0; i < page.Count; i++)
        {
            for (; next < placed.Count && placed[next].At == i; next++)
            {
                var st = placed[next].Stash;
                woven.Add(new GraphCommit(st.RowHash, [st.Base], "", "", st.UnixTime, st.Message));
            }
            woven.Add(page[i]);
        }
        return woven;
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

        // Stashes made on a commit in this page get their rows now; older pages are already laid out, so one newer than
        // the page's top commit sits at the top of the page.
        var woven = WithStashes(page, Stashes.Where(st => !_rowOf.ContainsKey(st.RowHash)));
        var commits = new GraphCommit[Commits.Count + woven.Count];
        for (int i = 0; i < Commits.Count; i++)
            commits[i] = Commits[i];
        var rowOf = new Dictionary<string, int>(_rowOf, StringComparer.Ordinal);
        for (int i = 0; i < woven.Count; i++)
        {
            commits[Commits.Count + i] = woven[i];
            rowOf[woven[i].Hash] = Commits.Count + i;
        }
        _lanes.Add(woven);
        return new GraphSnapshot(Root, Scope, Query, commits, RealCommitCount + page.Count, page.Count >= pageSize,
            Refs, Worktrees, Stashes, _primaryTip, _lanes, rowOf, Context);
    }
}
