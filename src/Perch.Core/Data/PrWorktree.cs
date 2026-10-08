namespace Perch.Data;

/// <summary>Where a PR's worktree goes and the local branch it checks out.</summary>
public sealed record PrWorktreePlan(string Path, string Branch);

/// <summary>The outcome of <see cref="PrWorktree.Ensure"/>: the worktree folder, whether it already existed, or an
/// error line (in which case <see cref="Path"/> is null and nothing was left behind).</summary>
public sealed record PrWorktreeResult(string? Path, bool Reused, string? Error);

/// <summary>
/// A per-PR <c>git worktree</c> beside the user's checkout, so a background session works on the PR without touching
/// the user's working copy (S2 in docs/github-dashboard-plan.md). Layout: <c>&lt;parent&gt;/.perch-worktrees/
/// &lt;repo&gt;-pr-&lt;n&gt;</c> on a local branch <c>perch/pr-&lt;n&gt;</c>.
///
/// <para>Git only, through <see cref="GitRunner"/> as a <see cref="GitRunner.Trust.UserAction"/> (the user asked for
/// it, so their hooks and LFS behave as in a terminal): <c>git fetch &lt;remote&gt; pull/&lt;n&gt;/head:perch/pr-&lt;n&gt;</c>
/// from whichever remote names the PR's repo (works for PRs from forks too), then <c>git worktree add</c>. A plain
/// (non-forced) fetch refuses to move a branch that has local commits, so a previous session's work is never
/// discarded; that surfaces as an error instead. An existing worktree is reused as it is, local commits and all.</para>
///
/// <para>Blocking; call it off the UI thread. Never throws.</para>
/// </summary>
public static class PrWorktree
{
    private const int FetchTimeoutMs = 120_000;
    private const int AddTimeoutMs = 120_000;

    public static PrWorktreePlan? PlanFor(string checkoutRoot, GitRepoRef repo, int number)
    {
        if (number <= 0 || !Path.IsPathFullyQualified(checkoutRoot)) return null;
        // A checkout at a drive/file-system root has nowhere beside it.
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(checkoutRoot))?.FullName;
        if (parent is null) return null;
        var safeRepo = new string(repo.Repo.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        return new PrWorktreePlan(
            Path.Combine(parent, RepoCheckoutResolver.WorktreeFolderName, $"{safeRepo}-pr-{number}"),
            $"perch/pr-{number}");
    }

    /// <summary>The remote of <paramref name="remotes"/> that names <paramref name="repo"/>: origin when it does,
    /// else the first that does; null when none does.</summary>
    public static string? RemoteFor(IReadOnlyList<GitRemoteEntry> remotes, GitRepoRef repo)
    {
        var names = remotes.Where(r => GitRemote.Parse(r.Url) is { } p && p.SameRepo(repo)).Select(r => r.Name).ToList();
        return names.FirstOrDefault(n => n.Equals("origin", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
    }

    public static PrWorktreeResult Ensure(string checkoutRoot, GitRepoRef repo, int number)
    {
        try
        {
            if (PlanFor(checkoutRoot, repo, number) is not { } plan)
                return new(null, false, "No room for a worktree beside this checkout");

            if (Directory.Exists(plan.Path))
            {
                // Ours from an earlier launch: a linked worktree has a ".git" file, not a directory.
                return File.Exists(Path.Combine(plan.Path, ".git"))
                    ? new(plan.Path, true, null)
                    : new(null, false, $"{plan.Path} exists and isn't a Perch worktree");
            }

            if (RemoteFor(GitCheckoutScanner.ReadRemotes(checkoutRoot), repo) is not { } remote)
                return new(null, false, $"No remote in {checkoutRoot} points at {repo.Slug}");

            var fetch = GitRunner.Run(checkoutRoot, FetchTimeoutMs, GitRunner.Trust.UserAction, null,
                "fetch", "--no-tags", remote, $"pull/{number}/head:{plan.Branch}");
            if (fetch.Exit != 0)
                return new(null, false, Describe("Couldn't fetch the PR", fetch));

            Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
            var add = GitRunner.Run(checkoutRoot, AddTimeoutMs, GitRunner.Trust.UserAction, null,
                "worktree", "add", plan.Path, plan.Branch);
            if (add.Exit != 0)
            {
                // A half-made worktree would be mistaken for ours next time; take it back out.
                GitRunner.Run(checkoutRoot, AddTimeoutMs, GitRunner.Trust.UserAction, null, "worktree", "remove", "--force", plan.Path);
                return new(null, false, Describe("Couldn't create the worktree", add));
            }
            return new(plan.Path, false, null);
        }
        catch (Exception ex)
        {
            return new(null, false, ex.Message);
        }
    }

    // "Couldn't fetch the PR: <git's first error line>".
    private static string Describe(string what, GitRunner.Result r)
    {
        if (r.Exit == -1) return $"{what} (git didn't respond)";
        var line = r.Stderr.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal) || l.StartsWith("! ", StringComparison.Ordinal))
            ?? r.Stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? what : $"{what}: {line}";
    }
}
