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
public sealed record GraphRef(string Name, string FullName, GraphRefKind Kind, string Target);

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
