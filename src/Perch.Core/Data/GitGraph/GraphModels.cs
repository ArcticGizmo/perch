namespace Perch.Data.GitGraph;

/// <summary>
/// One commit in the graph, as read from <c>git log --topo-order</c>. A class, not a record struct: a big repo holds
/// 100k of these and every row of the layout points at one, so they're shared by reference.
/// </summary>
public sealed class GraphCommit
{
    public GraphCommit(string hash, string[] parents, string authorName, string authorEmail, long unixTime, string subject)
    {
        Hash = hash;
        Parents = parents;
        AuthorName = authorName;
        AuthorEmail = authorEmail;
        UnixTime = unixTime;
        Subject = subject;
    }

    public string Hash { get; }

    /// <summary>Parent hashes, first parent first; empty for a root commit.</summary>
    public string[] Parents { get; }

    public string AuthorName { get; }
    public string AuthorEmail { get; }

    /// <summary>Author time, seconds since the Unix epoch (git's <c>%at</c>).</summary>
    public long UnixTime { get; }

    public string Subject { get; }

    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    /// <summary>A worktree's uncommitted changes, laid out as a pseudo-commit on top of its HEAD (see
    /// <see cref="GraphWorktree.WipHash"/>), so its tether gets a lane of its own.</summary>
    public bool IsWorkInProgress => Hash.StartsWith(GraphWorktree.WipPrefix, StringComparison.Ordinal);

    /// <summary>A stash entry, laid out as a pseudo-commit on top of the commit it was made on (see
    /// <see cref="GraphStash.RowHash"/>).</summary>
    public bool IsStash => Hash.StartsWith(GraphStash.Prefix, StringComparison.Ordinal);

    /// <summary>A row that isn't a real commit: a worktree's changes or a stash.</summary>
    public bool IsPseudo => IsWorkInProgress || IsStash;
    public bool IsMerge => Parents.Length > 1;
    public DateTimeOffset Date => DateTimeOffset.FromUnixTimeSeconds(UnixTime);
}

public enum GraphRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
    Stash,
}

/// <summary>
/// A ref pointing into the graph. <see cref="Name"/> is the short name shown on a pill (<c>main</c>,
/// <c>origin/main</c>, <c>v0.6.1</c>); <see cref="Target"/> is the commit it points at, with annotated tags already
/// peeled to their commit.
/// </summary>
public sealed record GraphRef(string Name, string FullName, GraphRefKind Kind, string Target)
{
    /// <summary>A local branch's upstream (<c>origin/main</c>), or null when it tracks nothing.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits this branch has that its upstream doesn't, and the other way round.</summary>
    public int Ahead { get; init; }
    public int Behind { get; init; }

    /// <summary>The upstream is configured but no longer exists (deleted on the remote and pruned).</summary>
    public bool UpstreamGone { get; init; }

    /// <summary>Tracks an upstream that exists and is at the same commit.</summary>
    public bool InSync => Upstream is not null && !UpstreamGone && Ahead == 0 && Behind == 0;
}

/// <summary>
/// One line segment in the band between two rows: from lane <see cref="From"/> at the upper row's centre to lane
/// <see cref="To"/> at the lower row's centre, coloured as lane <see cref="Color"/>. Shorts keep a 100k-row layout
/// small; no repo has 32k concurrent lanes.
/// </summary>
public readonly record struct GraphEdge(short From, short To, short Color);

/// <summary>
/// One laid-out row: the lane its node sits in, and the edges in the band <i>above</i> it (from the previous row).
/// Storing the band above rather than below means a row is final as soon as its commit is placed, which is what
/// lets the allocator resume page by page and a virtualised painter draw any window of rows on its own.
/// </summary>
public readonly record struct GraphRow(int Lane, GraphEdge[] Up);

/// <summary>Which commits the graph shows.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(TolerantStringEnumConverter<GraphScope>))]
public enum GraphScope
{
    /// <summary>Every branch, remote branch, tag, HEAD and worktree head.</summary>
    All,

    /// <summary>The primary branch plus whatever each worktree has checked out.</summary>
    Worktrees,

    /// <summary>The current worktree's HEAD and its base branch, back to where they diverged (today's View tree).</summary>
    Branch,
}

/// <summary>The revisions a log walks: <see cref="Tips"/> (refs, hashes or <c>--branches</c>-style options), minus
/// everything reachable from <see cref="Exclude"/>.</summary>
public sealed record GraphLogQuery(IReadOnlyList<string> Tips, IReadOnlyList<string> Exclude)
{
    public static readonly GraphLogQuery Everything = new(["--branches", "--remotes", "--tags", "HEAD"], []);
}

/// <summary>
/// One working tree of the repo as the graph shows it: its folder, the branch checked out (null when detached),
/// the commit at its HEAD (null for an unborn branch), how many paths it has changed, and whether it's the one the
/// window was opened on.
/// </summary>
/// <summary>
/// One stash entry: its commit, the commit it was made on (<see cref="Base"/>, its first parent), its place in the list
/// (<c>stash@{0}</c>), git's message ("WIP on main: …" or "On main: &lt;message&gt;") and when it was made.
/// </summary>
public sealed record GraphStash(string Hash, string Base, string Selector, string Message, long UnixTime)
{
    internal const string Prefix = "stash:";

    /// <summary>The pseudo-commit id of this entry's row; stable while the entry exists, so lane hints keep it in place.</summary>
    public string RowHash => Prefix + Hash;

    /// <summary>The entry as a ref, for the rail's Stashes list and the pill on its row.</summary>
    public GraphRef AsRef() => new(Selector, Selector, GraphRefKind.Stash, RowHash);
}

public sealed record GraphWorktree(string Path, string? Branch, string? Head, int Changes, bool IsCurrent)
{
    internal const string WipPrefix = "wip:";

    /// <summary>The pseudo-commit id of this worktree's uncommitted changes; stable across refreshes, so lane hints
    /// keep its tether in place.</summary>
    public string WipHash => WipPrefix + Path;

    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
}
