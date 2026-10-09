namespace Perch.Data;

/// <summary>Where a PR's worktree goes and the local branch it checks out.</summary>
public sealed record PrWorktreePlan(string Path, string Branch);

/// <summary>The outcome of <see cref="PrWorktree.Ensure"/>: the worktree folder, whether it already existed, or an
/// error line (in which case <see cref="Path"/> is null and nothing was left behind).</summary>
public sealed record PrWorktreeResult(string? Path, bool Reused, string? Error);

/// <summary>A checkout's worktree picture for the dialog: the repo's worktrees and the layout new ones follow.</summary>
public sealed record PrWorktreeContext(GitWorktreeSet Set, WorktreeLayout Layout);

/// <summary>
/// A per-PR <c>git worktree</c>, so a session works on the PR without touching the user's working copy (S2/W4 in
/// docs/github-dashboard-plan.md). It goes where the user's own worktrees go (<see cref="WorktreeLayoutInference"/>),
/// named <c>pr-&lt;n&gt;</c>, on a local branch <c>perch/pr-&lt;n&gt;</c>. A layout nested inside the checkout gets a
/// line in the repo's <c>.git/info/exclude</c> — local, never committed — so the worktree doesn't show as untracked.
///
/// <para>Git only, through <see cref="GitRunner"/> as a <see cref="GitRunner.Trust.UserAction"/> (the user asked for
/// it, so their hooks and LFS behave as in a terminal): <c>git fetch &lt;remote&gt; pull/&lt;n&gt;/head:perch/pr-&lt;n&gt;</c>
/// from whichever remote names the PR's repo (works for PRs from forks too), then <c>git worktree add</c>. A plain
/// (non-forced) fetch refuses to move a branch that has local commits, so a previous session's work is never
/// discarded; that surfaces as an error instead. A worktree already on <c>perch/pr-&lt;n&gt;</c> is reused as it is,
/// wherever it is (including the old <c>.perch-worktrees</c> location).</para>
///
/// <para>Blocking; call it off the UI thread. Never throws.</para>
/// </summary>
public static class PrWorktree
{
    private const int FetchTimeoutMs = 120_000;
    private const int AddTimeoutMs = 120_000;

    public static string BranchFor(int number) => $"perch/pr-{number}";

    /// <summary>The worktree set and inferred layout for the checkout at <paramref name="checkoutRoot"/>, or null when
    /// it isn't a git checkout. Reads the repo's <c>.gitignore</c> and <c>info/exclude</c> as layout hints.</summary>
    public static PrWorktreeContext? Inspect(string checkoutRoot)
    {
        try
        {
            if (GitWorktreeScanner.Read(checkoutRoot) is not { } set) return null;
            var common = GitWorktreeScanner.CommonDir(set.Root);
            string? Text(string? path) => path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
            var layout = WorktreeLayoutInference.Infer(set.Root, set.Bare, set.Linked,
                Text(Path.Combine(set.Root, ".gitignore")),
                Text(common is null ? null : Path.Combine(common, "info", "exclude")));
            return new PrWorktreeContext(set, layout);
        }
        catch { return null; }
    }

    /// <summary>Where the worktree for PR <paramref name="number"/> goes: an existing worktree already on its branch,
    /// else the layout's place for <c>pr-&lt;n&gt;</c>.</summary>
    public static PrWorktreePlan? PlanFor(PrWorktreeContext ctx, int number)
    {
        if (number <= 0) return null;
        var branch = BranchFor(number);
        var path = ctx.Set.PathOfBranch(branch) ?? ctx.Layout.Render(ctx.Set.Root, $"pr-{number}");
        return new PrWorktreePlan(path, branch);
    }

    /// <summary>The remote of <paramref name="remotes"/> that names <paramref name="repo"/>: origin when it does,
    /// else the first that does; null when none does.</summary>
    public static string? RemoteFor(IReadOnlyList<GitRemoteEntry> remotes, GitRepoRef repo)
    {
        var names = remotes.Where(r => GitRemote.Parse(r.Url) is { } p && p.SameRepo(repo)).Select(r => r.Name).ToList();
        return names.FirstOrDefault(n => n.Equals("origin", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
    }

    /// <summary><paramref name="exclude"/> (an <c>info/exclude</c> file's text, or null) with <paramref name="entry"/>
    /// appended, or null when an equivalent line is already there. Pure.</summary>
    public static string? WithExcludeEntry(string? exclude, string entry)
    {
        static string Key(string line) => line.Trim().Trim('/');
        var text = exclude ?? "";
        if (text.Split('\n').Any(l => Key(l).Equals(Key(entry), StringComparison.OrdinalIgnoreCase))) return null;
        var sep = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        return text + sep + "# Perch: worktrees for pull request sessions\n" + entry + "\n";
    }

    /// <summary>Makes (or finds) the PR's worktree. <paramref name="layout"/> overrides the inferred layout, when the
    /// user picked another in the dialog.</summary>
    public static PrWorktreeResult Ensure(string checkoutRoot, GitRepoRef repo, int number, WorktreeLayout? layout = null)
    {
        try
        {
            if (Inspect(checkoutRoot) is not { } inspected) return new(null, false, "That folder isn't a git checkout");
            var ctx = layout is null ? inspected : inspected with { Layout = layout };
            if (PlanFor(ctx, number) is not { } plan)
                return new(null, false, "That folder isn't a git checkout");
            var root = ctx.Set.Root;

            // Already on perch/pr-<n> somewhere (an earlier launch, wherever the layout put it): reuse it as it is.
            if (ctx.Set.PathOfBranch(plan.Branch) is { } existing)
                return new(existing, true, null);
            if (Directory.Exists(plan.Path) && Directory.EnumerateFileSystemEntries(plan.Path).Any())
                return new(null, false, $"{plan.Path} already exists and isn't this PR's worktree");

            if (RemoteFor(GitCheckoutScanner.ReadRemotes(root), repo) is not { } remote)
                return new(null, false, $"No remote in {root} points at {repo.Slug}");

            var fetch = GitRunner.Run(root, FetchTimeoutMs, GitRunner.Trust.UserAction, null,
                "fetch", "--no-tags", remote, $"pull/{number}/head:{plan.Branch}");
            if (fetch.Exit != 0)
                return new(null, false, Describe("Couldn't fetch the PR", fetch));

            // A nested layout: keep the worktree out of `git status` locally, without touching the repo's .gitignore.
            if (ctx.Layout.ExcludeEntry(ctx.Set.Bare) is { } entry && GitWorktreeScanner.CommonDir(root) is { } common)
            {
                var excludePath = Path.Combine(common, "info", "exclude");
                var current = File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;
                if (WithExcludeEntry(current, entry) is { } updated)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
                    File.WriteAllText(excludePath, updated);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(plan.Path)!);
            var add = GitRunner.Run(root, AddTimeoutMs, GitRunner.Trust.UserAction, null,
                "worktree", "add", plan.Path, plan.Branch);
            if (add.Exit != 0)
            {
                // A half-made worktree would be mistaken for ours next time; take it back out.
                GitRunner.Run(root, AddTimeoutMs, GitRunner.Trust.UserAction, null, "worktree", "remove", "--force", plan.Path);
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
