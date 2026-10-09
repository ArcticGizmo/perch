namespace Perch.Data;

/// <summary>Where an inferred <see cref="WorktreeLayout"/> came from: the user's existing worktrees, a hint in an
/// ignore file, or Perch's default (Claude Code's own <c>.claude/worktrees</c>).</summary>
public enum WorktreeLayoutSource { Detected, IgnoreHint, Default }

/// <summary>
/// How a user lays out a repo's worktrees, as a path template over <c>{root}</c> (where git runs for the repo),
/// <c>{parent}</c> (the folder above it), <c>{repo}</c> (the root's folder name) and <c>{name}</c> (the new
/// worktree's name) — e.g. <c>{parent}/{repo}-{name}</c> or <c>{root}/.claude/worktrees/{name}</c>. Always written
/// with <c>/</c>; <see cref="Render"/> makes a real path.
/// </summary>
public sealed record WorktreeLayout(string Template, WorktreeLayoutSource Source, int Evidence = 0)
{
    /// <summary>Claude Code's own layout (<c>claude --worktree</c>), the fallback when nothing else is known.</summary>
    public const string DefaultTemplate = "{root}/.claude/worktrees/{name}";

    /// <summary>Whether worktrees land inside the repo's own folder — and so need ignoring there, or git shows them
    /// as untracked. False for the bare layout, whose root isn't a working tree.</summary>
    public bool InsideRoot(bool bare) => !bare && Template.StartsWith("{root}/", StringComparison.Ordinal);

    /// <summary>The folder, relative to the root, that worktrees go in (<c>.claude/worktrees</c>) — the line to add
    /// to <c>.git/info/exclude</c>. Null when they don't nest under the root.</summary>
    public string? ExcludeEntry(bool bare)
    {
        if (!InsideRoot(bare)) return null;
        var rel = Template["{root}/".Length..];
        int cut = rel.IndexOf("{name}", StringComparison.Ordinal);
        var dir = (cut >= 0 ? rel[..cut] : rel).TrimEnd('/');
        return dir.Length == 0 || dir.Contains('{') ? null : "/" + dir + "/";
    }

    /// <summary>The full path of a worktree called <paramref name="name"/> for the repo at <paramref name="root"/>.</summary>
    public string Render(string root, string name)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var parent = Path.GetDirectoryName(r) ?? r;
        var repo = Path.GetFileName(r);
        var path = Template
            .Replace("{root}", r.Replace('\\', '/'))
            .Replace("{parent}", parent.Replace('\\', '/'))
            .Replace("{repo}", repo)
            .Replace("{name}", name);
        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Infers a repo's <see cref="WorktreeLayout"/> from what is already on disk (W3 in docs/github-dashboard-plan.md),
/// so a PR worktree lands where the user's own worktrees do instead of in yet another convention. Pure.
///
/// <para>Each existing worktree votes for a template. The best evidence is its folder name rendering its branch
/// (<c>feature/x</c> as <c>feature-x</c>, <c>feature_x</c>, a nested <c>feature/x</c>, or just <c>x</c>), with
/// whatever surrounds it — <c>{parent}/web-</c>, <c>{root}/.worktrees/</c> — becoming the template, and the repo's
/// folder name inside it becoming <c>{repo}</c>. A worktree whose name doesn't follow its branch still votes for its
/// containing folder. The most-voted template wins. With no worktrees, ignore-file entries
/// (<c>.worktrees/</c>, <c>.claude/worktrees</c> …) are a hint; a bare layout puts them beside its <c>.bare</c>; and
/// the fallback is <see cref="WorktreeLayout.DefaultTemplate"/>. Perch's own old <c>.perch-worktrees</c> folders don't
/// vote.</para>
/// </summary>
public static class WorktreeLayoutInference
{
    // Ignore-file entries that announce a nested worktree folder, in the order they're preferred.
    private static readonly (string Entry, string Template)[] IgnoreHints =
    [
        (".claude/worktrees", "{root}/.claude/worktrees/{name}"),
        (".worktrees", "{root}/.worktrees/{name}"),
        ("worktrees", "{root}/worktrees/{name}"),
        (".trees", "{root}/.trees/{name}"),
    ];

    public static WorktreeLayout Infer(string root, bool bare, IEnumerable<GitWorktree> worktrees, params string?[] ignoreFiles)
    {
        var r = Norm(root);
        var parent = Parent(r);
        var repo = r[(r.LastIndexOf('/') + 1)..];

        var votes = new Dictionary<string, (int Count, int Strength)>(StringComparer.Ordinal);
        foreach (var wt in worktrees)
        {
            var p = Norm(wt.Path);
            if (RepoCheckoutResolver.IsPerchWorktree(p)) continue;
            if (Vote(p, wt.Branch, r, parent, repo) is not { } v) continue;
            var cur = votes.TryGetValue(v.Template, out var c) ? c : (Count: 0, Strength: 0);
            votes[v.Template] = (cur.Count + 1, Math.Max(cur.Strength, v.Strength));
        }
        if (votes.Count > 0)
        {
            var best = votes.OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Strength)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            return new WorktreeLayout(best.Key, WorktreeLayoutSource.Detected, best.Value.Count);
        }

        if (bare) return new WorktreeLayout("{root}/{name}", WorktreeLayoutSource.Default);

        var entries = ignoreFiles.Where(f => f is not null).SelectMany(f => f!.Split('\n'))
            .Select(l => l.Trim().Trim('/').TrimEnd('*').TrimEnd('/'))
            .Where(l => l.Length > 0 && l[0] != '#')
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, template) in IgnoreHints)
            if (entries.Contains(entry)) return new WorktreeLayout(template, WorktreeLayoutSource.IgnoreHint);

        return new WorktreeLayout(WorktreeLayout.DefaultTemplate, WorktreeLayoutSource.Default);
    }

    // One worktree's vote: a template, and how sure (2 = its folder renders its branch, 1 = just where it sits).
    private static (string Template, int Strength)? Vote(string path, string? branch, string root, string parent, string repo)
    {
        if (branch is { Length: > 0 })
            foreach (var rendered in BranchRenderings(branch))
                if (path.EndsWith(rendered, StringComparison.OrdinalIgnoreCase) && path.Length > rendered.Length)
                {
                    var head = path[..^rendered.Length];
                    // The branch must fill a whole folder name, or follow a separator in it ("web-" + "feature-x").
                    char before = head[^1];
                    if (before is not ('/' or '-' or '.' or '_' or '@')) continue;
                    if (Relative(head, root, parent, repo) is { } t) return (t + "{name}", 2);
                }

        // No name evidence: vote for the folder it sits in, keeping a "{repo}-" style prefix when there is one.
        var dir = Parent(path);
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        foreach (var sep in new[] { "-", ".", "_", "@" })
            if (leaf.StartsWith(repo + sep, StringComparison.OrdinalIgnoreCase) && leaf.Length > repo.Length + 1
                && Relative(dir + "/", root, parent, repo) is { } t1)
                return (t1 + "{repo}" + sep + "{name}", 1);
        return Relative(dir + "/", root, parent, repo) is { } t2 ? (t2 + "{name}", 1) : null;
    }

    // feature/x → feature/x (nested), feature-x, feature_x, x.
    private static IEnumerable<string> BranchRenderings(string branch)
    {
        var b = branch.Trim('/');
        yield return b;
        if (!b.Contains('/')) yield break;
        yield return b.Replace('/', '-');
        yield return b.Replace('/', '_');
        yield return b[(b.LastIndexOf('/') + 1)..];
    }

    // "<root>/x/" → "{root}/x/", "<parent>/web-" → "{parent}/{repo}-"; null for anywhere else.
    private static string? Relative(string head, string root, string parent, string repo)
    {
        if (head.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return "{root}/" + head[(root.Length + 1)..];
        if (head.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = head[(parent.Length + 1)..];
            // The repo's own name inside the prefix becomes {repo}, so the layout carries over to other repos.
            if (rest.StartsWith(repo, StringComparison.OrdinalIgnoreCase) && rest.Length > repo.Length)
                rest = "{repo}" + rest[repo.Length..];
            return "{parent}/" + rest;
        }
        return null;
    }

    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');

    private static string Parent(string path)
    {
        int i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : path;
    }
}
