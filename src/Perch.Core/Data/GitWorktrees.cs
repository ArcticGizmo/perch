namespace Perch.Data;

/// <summary>One linked worktree of a repository: its folder and the branch checked out there (null when detached).</summary>
public sealed record GitWorktree(string Path, string? Branch);

/// <summary>
/// A repository's working trees as git records them. <see cref="Root"/> is where git commands for the repo run: the
/// main working tree, or for a bare repo the folder holding it (the "<c>.bare</c> + worktrees" layout, whose root has
/// a <c>.git</c> file pointing at <c>./.bare</c>). <see cref="MainBranch"/> is the main working tree's branch (null
/// when bare or detached). <see cref="Linked"/> are the other worktrees that still exist on disk.
/// </summary>
public sealed record GitWorktreeSet(string Root, bool Bare, string? MainBranch, IReadOnlyList<GitWorktree> Linked)
{
    /// <summary>The worktree (main or linked) with <paramref name="branch"/> checked out, or null.</summary>
    public string? PathOfBranch(string branch)
    {
        if (string.IsNullOrEmpty(branch)) return null;
        if (!Bare && MainBranch == branch) return Root;
        return Linked.FirstOrDefault(w => w.Branch == branch)?.Path;
    }
}

/// <summary>
/// Reads a repository's worktrees straight from its shared git directory — the same records <c>git worktree list</c>
/// prints — with no git process: <c>&lt;common&gt;/worktrees/&lt;name&gt;/gitdir</c> names each linked worktree's
/// <c>.git</c> file, and each has its own <c>HEAD</c>. A worktree whose folder is gone (prunable) is left out.
///
/// <para>Blocking file IO; call it off the UI thread. Never throws: an unreadable repo is null.</para>
/// </summary>
public static class GitWorktreeScanner
{
    /// <summary>The worktree set of the repository <paramref name="checkoutRoot"/> belongs to (main or linked
    /// worktree, or a bare layout's root), or null when it isn't one.</summary>
    public static GitWorktreeSet? Read(string checkoutRoot)
    {
        try
        {
            if (CommonDir(checkoutRoot) is not { } common) return null;
            bool bare = IsBare(common);
            // Where git commands for the repo run: the main working tree (the folder above .git); for a bare repo,
            // the folder whose ".git" file points at it (the ".bare" layout), else the bare repo itself.
            var parent = Directory.GetParent(common)?.FullName;
            var root = !bare ? parent ?? checkoutRoot
                : parent is not null && File.Exists(System.IO.Path.Combine(parent, ".git")) ? parent
                : common;
            // A submodule's ".git" file also points into another repo's .git (modules/<name>), whose parent isn't a
            // working tree of it. Only accept a root whose own .git leads back to the same shared dir.
            if (root != common && !SamePath(CommonDir(root), common)) root = checkoutRoot;
            string? mainBranch = bare ? null : BranchOfHead(System.IO.Path.Combine(common, "HEAD"));

            var linked = new List<GitWorktree>();
            var dir = System.IO.Path.Combine(common, "worktrees");
            if (Directory.Exists(dir))
                foreach (var wt in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var gitdirFile = System.IO.Path.Combine(wt, "gitdir");
                    if (!File.Exists(gitdirFile)) continue;
                    var dotGit = File.ReadAllText(gitdirFile).Trim();
                    if (dotGit.Length == 0) continue;
                    dotGit = System.IO.Path.GetFullPath(System.IO.Path.Combine(wt, dotGit));
                    var path = System.IO.Path.GetDirectoryName(dotGit);
                    if (path is null || !File.Exists(dotGit)) continue;      // prunable: its folder is gone
                    linked.Add(new GitWorktree(path, BranchOfHead(System.IO.Path.Combine(wt, "HEAD"))));
                }
            return new GitWorktreeSet(root, bare, mainBranch, linked);
        }
        catch { return null; }
    }

    /// <summary>The repo's shared git directory: <c>.git</c> itself, or for a <c>.git</c> file the gitdir it names,
    /// followed through <c>commondir</c> for a linked worktree. Null when <paramref name="root"/> has no <c>.git</c>.</summary>
    internal static string? CommonDir(string root)
    {
        var git = System.IO.Path.Combine(root, ".git");
        if (Directory.Exists(git)) return System.IO.Path.GetFullPath(git);
        if (!File.Exists(git)) return null;
        var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, line["gitdir:".Length..].Trim()));
        var common = System.IO.Path.Combine(gitDir, "commondir");
        if (File.Exists(common) && File.ReadAllText(common).Trim() is { Length: > 0 } rel)
            gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(gitDir, rel));
        return Path.TrimEndingDirectorySeparator(gitDir);
    }

    // "ref: refs/heads/<branch>" → branch; detached (a bare sha) or unreadable → null.
    internal static string? BranchOfHead(string headFile)
    {
        if (!File.Exists(headFile)) return null;
        var line = File.ReadLines(headFile).FirstOrDefault()?.Trim() ?? "";
        const string prefix = "ref: refs/heads/";
        return line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : null;
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // core.bare = true in the shared config.
    private static bool IsBare(string common)
    {
        var config = System.IO.Path.Combine(common, "config");
        return File.Exists(config) && GitCheckoutScanner.ConfigValue(File.ReadAllText(config), "core", "bare") is { } v
            && v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
