namespace Perch.Data.GitGraph;

/// <summary>
/// One repo's graph visibility, kept in <see cref="AppSettings.GitTreeRepos"/> under the repo's shared git dir (so every
/// worktree of it agrees): the branches hidden from the graph, and the remote branches Perch has already seen, so only
/// later arrivals count as "new" for <see cref="AppSettings.GitTreeAutoHideOthers"/>.
/// </summary>
public sealed class GraphRepoPrefs
{
    /// <summary>Full ref names (<c>refs/remotes/origin/x</c>, <c>refs/heads/y</c>) left out of the graph.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Remote branches already seen; null until auto-hide first looks at this repo, which seeds it with
    /// whatever is there then, so turning it on never hides existing branches.</summary>
    public List<string>? Seen { get; set; }
}

/// <summary>
/// Hiding branches from the graph, and deciding which new remote branches are the user's. "Yours" means the branch
/// has a commit that trunk doesn't, authored with your <c>user.email</c> or <c>user.name</c>; it's decided from one
/// <c>git log</c> over the new branches minus trunk, so its cost is the handful of commits those branches add.
/// </summary>
internal static class BranchVisibility
{
    /// <summary>The All scope's walk with <paramref name="hidden"/> left out: each <c>--exclude</c> applies to the next
    /// <c>--branches</c>/<c>--remotes</c> only, and takes the name without its <c>refs/heads/</c> or <c>refs/remotes/</c>.</summary>
    public static GraphLogQuery AllExcept(IReadOnlyCollection<string> hidden, IEnumerable<string> extraTips)
    {
        var tips = new List<string>();
        foreach (var h in hidden)
            if (h.StartsWith("refs/heads/", StringComparison.Ordinal)) tips.Add("--exclude=" + h["refs/heads/".Length..]);
        tips.Add("--branches");
        foreach (var h in hidden)
            if (h.StartsWith("refs/remotes/", StringComparison.Ordinal)) tips.Add("--exclude=" + h["refs/remotes/".Length..]);
        tips.AddRange(["--remotes", "--tags", "HEAD", .. extraTips]);
        return new GraphLogQuery(tips, []);
    }

    /// <summary>Remote branches <paramref name="prefs"/> hasn't seen and doesn't already hide.</summary>
    public static List<GraphRef> Unseen(IEnumerable<GraphRef> refs, GraphRepoPrefs prefs)
    {
        var seen = new HashSet<string>(prefs.Seen ?? [], StringComparer.Ordinal);
        var hidden = new HashSet<string>(prefs.Hidden, StringComparer.Ordinal);
        return refs.Where(r => r.Kind == GraphRefKind.RemoteBranch && !seen.Contains(r.FullName) && !hidden.Contains(r.FullName)).ToList();
    }

    /// <summary>Marks every current remote branch as seen (turning auto-hide on, or its first look at a repo).</summary>
    public static void SeedSeen(GraphRepoPrefs prefs, IEnumerable<GraphRef> refs) =>
        prefs.Seen = refs.Where(r => r.Kind == GraphRefKind.RemoteBranch).Select(r => r.FullName).Distinct().ToList();

    /// <summary>The trunk to measure "new work" against: the primary branch and its same-named remote branches.</summary>
    public static IReadOnlyList<string> TrunkRefs(IReadOnlyList<GraphRef> refs, string? primary)
    {
        if (primary is null) return [];
        var name = GraphCheckout.LocalName(primary);
        return refs.Where(r => r.Name == primary || r.Name == name
                               || (r.Kind == GraphRefKind.RemoteBranch && GraphCheckout.LocalName(r.Name) == name))
            .Select(r => r.FullName).Distinct().ToList();
    }

    /// <summary>Which of <paramref name="candidates"/> are the user's (blocking; one git call). With no git identity to
    /// match, every candidate counts as the user's, so nothing is hidden on a guess.</summary>
    public static HashSet<string> Mine(string repo, IReadOnlyList<GraphRef> candidates, IReadOnlyList<string> trunk)
    {
        var all = candidates.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
        if (candidates.Count == 0) return all;
        var email = Config(repo, "user.email");
        var name = Config(repo, "user.name");
        if (email is null && name is null) return all;
        var args = new List<string> { "log", "--format=%H%x1f%P%x1f%ae%x1f%an" };
        args.AddRange(candidates.Select(c => c.FullName));
        if (trunk.Count > 0) { args.Add("--not"); args.AddRange(trunk); }
        var r = GitRunner.Run(repo, 30_000, [.. args]);
        return r.Exit == 0 ? MineFrom(r.Stdout, candidates, email, name) : all;
    }

    /// <summary>
    /// Pure half of <see cref="Mine"/>: <paramref name="log"/> is <c>hash␟parents␟email␟name</c> per line for the commits
    /// the candidates add over trunk. A candidate is the user's when a walk from its tip through those commits meets
    /// one the user authored; anything not in the log is on trunk, where the walk stops.
    /// </summary>
    public static HashSet<string> MineFrom(string log, IEnumerable<GraphRef> candidates, string? email, string? name)
    {
        var commits = new Dictionary<string, (string[] Parents, bool Mine)>(StringComparer.Ordinal);
        foreach (var line in log.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\x1f');
            if (f.Length < 4) continue;
            bool mine = (email is not null && f[2].Equals(email, StringComparison.OrdinalIgnoreCase))
                        || (name is not null && f[3].Equals(name, StringComparison.OrdinalIgnoreCase));
            commits[f[0]] = (f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), mine);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([c.Target]);
            while (stack.Count > 0)
            {
                var h = stack.Pop();
                if (!seen.Add(h) || !commits.TryGetValue(h, out var commit)) continue;
                if (commit.Mine) { result.Add(c.FullName); break; }
                foreach (var p in commit.Parents) stack.Push(p);
            }
        }
        return result;
    }

    private static string? Config(string repo, string key)
    {
        var r = GitRunner.Run(repo, 6000, "config", "--get", key);
        return r.Exit == 0 && r.Stdout.Trim() is { Length: > 0 } v ? v : null;
    }
}
