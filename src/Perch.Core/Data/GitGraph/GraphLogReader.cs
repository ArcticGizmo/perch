namespace Perch.Data.GitGraph;

/// <summary>
/// Reads the commit graph and its refs for one repository. The parsers are pure and unit-tested against canned
/// output; <see cref="LoadLog"/>/<see cref="LoadRefs"/> run git through <see cref="GitRunner"/> as an Automatic run
/// (the repo's own commands neutralised) and are best-effort: empty on any failure, never throwing. Blocking: call
/// them off the UI thread.
/// </summary>
internal static class GraphLogReader
{
    private const char Us = '\u001f';

    // Subjects are one line, so a record can't contain the separator or a NUL. %at (epoch seconds) is cheaper to
    // format and parse than an ISO date, and %h is avoided because abbreviating means a lookup per commit.
    private const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%s";
    private const string RefFormat =
        "--format=%(objectname)%1f%(*objectname)%1f%(refname)%1f%(upstream:short)%1f%(upstream:track,nobracket)";

    // A deep topo-order walk of a big repo is the slowest read Perch makes.
    private const int LogTimeoutMs = 30_000;
    private const int RefTimeoutMs = 6_000;

    /// <summary>
    /// Up to <paramref name="maxCount"/> commits of <paramref name="query"/> (default: every branch, remote branch,
    /// tag and HEAD) in topological order (children before parents), skipping the first <paramref name="skip"/>.
    /// </summary>
    public static IReadOnlyList<GraphCommit> LoadLog(string cwd, int maxCount, int skip = 0, GraphLogQuery? query = null) =>
        ReadLog(cwd, maxCount, skip, query) is { } raw ? ParseLog(raw) : [];

    /// <summary>The raw output behind <see cref="LoadLog"/>, or null on failure. Split out so the bench can time git
    /// and the parse separately.</summary>
    internal static string? ReadLog(string cwd, int maxCount, int skip = 0, GraphLogQuery? query = null)
    {
        if (maxCount <= 0 || !GitRepoService.IsRepo(cwd))
            return null;
        var args = new List<string> { "--no-optional-locks", "log", "--topo-order", "-z", LogFormat, $"--max-count={maxCount}" };
        if (skip > 0)
            args.Add($"--skip={skip}");
        args.AddRange(GraphLogArgs(query ?? GraphLogQuery.Everything));
        var r = GitRunner.Run(cwd, LogTimeoutMs, [.. args]);
        return r.Exit == 0 ? r.Stdout : null;
    }

    /// <summary>The revision arguments for a query: its tips, then <c>--not</c> and the exclusions. Pure; tested.</summary>
    internal static List<string> GraphLogArgs(GraphLogQuery q)
    {
        var args = new List<string>(q.Tips);
        if (q.Exclude.Count > 0)
        {
            args.Add("--not");
            args.AddRange(q.Exclude);
        }
        return args;
    }

    /// <summary>Every local branch, remote branch and tag, or empty on failure. Stashes come from <see cref="LoadStashes"/>:
    /// <c>refs/stash</c> is only the newest entry.</summary>
    public static IReadOnlyList<GraphRef> LoadRefs(string cwd)
    {
        if (!GitRepoService.IsRepo(cwd))
            return [];
        var r = GitRunner.Run(cwd, RefTimeoutMs, "--no-optional-locks", "for-each-ref", RefFormat,
            "refs/heads", "refs/remotes", "refs/tags");
        return r.Exit == 0 ? ParseRefs(r.Stdout) : [];
    }

    /// <summary>Every stash entry, newest first, or empty on failure (or when there are none).</summary>
    public static IReadOnlyList<GraphStash> LoadStashes(string cwd)
    {
        if (!GitRepoService.IsRepo(cwd))
            return [];
        var r = GitRunner.Run(cwd, RefTimeoutMs, "--no-optional-locks", "stash", "list", "--format=%H%x1f%P%x1f%gd%x1f%ct%x1f%gs");
        return r.Exit == 0 ? ParseStashes(r.Stdout) : [];
    }

    /// <summary>Parses <see cref="LoadStashes"/>' records (hash, parents, selector, time, message). Pure; tested.</summary>
    internal static IReadOnlyList<GraphStash> ParseStashes(string output)
    {
        var stashes = new List<GraphStash>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(Us);
            if (f.Length < 5 || f[0].Length < 7 || f[1].Length == 0 || !long.TryParse(f[3], out var time))
                continue;
            stashes.Add(new GraphStash(f[0], f[1].Split(' ')[0], f[2], f[4], time));
        }
        return stashes;
    }

    /// <summary>Parses NUL-separated <see cref="LogFormat"/> records. A malformed record is skipped.</summary>
    internal static IReadOnlyList<GraphCommit> ParseLog(string output)
    {
        var commits = new List<GraphCommit>();
        // Parent hashes are interned to the parent's own Hash string once it's been seen (children come first, so
        // that's only after the fact): look the parent up when the parent itself is parsed and reuse the child's
        // copy instead. Halves the hash strings held for a linear history.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\0'))
        {
            var record = raw.TrimStart('\n', '\r');
            if (record.Length == 0)
                continue;
            var f = record.Split(Us);
            if (f.Length < 6 || f[0].Length < 7 || !long.TryParse(f[4], out var time))
                continue;
            var hash = seen.Remove(f[0], out var shared) ? shared : f[0];
            var parents = f[1].Length == 0 ? [] : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parents.Length; i++)
            {
                if (seen.TryGetValue(parents[i], out var p)) parents[i] = p;
                else seen[parents[i]] = parents[i];
            }
            commits.Add(new GraphCommit(hash, parents, f[2], f[3], time, f[5].TrimEnd('\r', '\n')));
        }
        return commits;
    }

    /// <summary>
    /// Parses newline-separated <see cref="RefFormat"/> records. An annotated tag carries its commit in the peeled
    /// field; symbolic remote heads (<c>origin/HEAD</c>) are dropped as they only repeat another ref.
    /// </summary>
    internal static IReadOnlyList<GraphRef> ParseRefs(string output)
    {
        var refs = new List<GraphRef>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(Us);
            if (f.Length < 3 || f[0].Length == 0 || f[2].Length == 0)
                continue;
            var full = f[2];
            var target = f[1].Length > 0 ? f[1] : f[0];
            GraphRef? r = full switch
            {
                _ when full.StartsWith("refs/heads/", StringComparison.Ordinal)
                    => WithTracking(new(full["refs/heads/".Length..], full, GraphRefKind.LocalBranch, target),
                        f.Length > 3 ? f[3] : "", f.Length > 4 ? f[4] : ""),
                _ when full.StartsWith("refs/remotes/", StringComparison.Ordinal) && !full.EndsWith("/HEAD", StringComparison.Ordinal)
                    => new(full["refs/remotes/".Length..], full, GraphRefKind.RemoteBranch, target),
                _ when full.StartsWith("refs/tags/", StringComparison.Ordinal)
                    => new(full["refs/tags/".Length..], full, GraphRefKind.Tag, target),
                _ => null,
            };
            if (r is not null)
                refs.Add(r);
        }
        return refs;
    }

    // A local branch's upstream from %(upstream:short) and %(upstream:track,nobracket): "" (in sync), "ahead 2",
    // "behind 1", "ahead 2, behind 1" or "gone".
    private static GraphRef WithTracking(GraphRef r, string upstream, string track)
    {
        if (upstream.Length == 0) return r;
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var words = part.Split(' ');
            if (words.Length == 2 && int.TryParse(words[1], out var n))
            {
                if (words[0] == "ahead") ahead = n;
                else if (words[0] == "behind") behind = n;
            }
        }
        return r with { Upstream = upstream, Ahead = ahead, Behind = behind, UpstreamGone = track.Trim() == "gone" };
    }
}
