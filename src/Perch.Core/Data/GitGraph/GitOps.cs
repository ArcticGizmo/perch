using System.Collections.Concurrent;

namespace Perch.Data.GitGraph;

/// <summary>The outcome of a git operation: whether it worked, git's message (its first stderr line on failure),
/// and the command line, for the toast and the log.</summary>
public sealed record GitOpResult(bool Ok, string Message, string Command)
{
    /// <summary>The operation stopped on conflicts (a merge, rebase or cherry-pick now in progress).</summary>
    public bool Conflicted { get; init; }
}

/// <summary>Which multi-step operation a worktree is in the middle of.</summary>
public enum GitInProgress
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>Where a worktree's HEAD was before an operation, so the toast's Undo can put it back.</summary>
public sealed record GitHeadState(string? Branch, string Hash);

/// <summary>
/// The git graph's write side: checkout, branch, stash, merge, rebase, fast-forward, cherry-pick and worktree add,
/// each run in a named worktree (the one that owns the branch, so nothing has to be switched first). These are
/// things the user asked for, so they run with <see cref="GitRunner.Trust.UserAction"/>: the repo's hooks and
/// filters behave as in a terminal. Writes to one repo are serialised through a per-repo gate, a held
/// <c>index.lock</c> (a session's own git, usually) gets one retry and then a readable message, and nothing here
/// ever forces a push or discards work. Blocking: call off the UI thread. Never throws.
/// </summary>
internal static class GitOps
{
    private const int TimeoutMs = 120_000;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static GitOpResult Switch(string worktree, string branch) => Refuse(branch) ?? Run(worktree, "switch", branch);

    /// <summary>Switches carrying local changes across (<c>--merge</c>); conflicts are left in the tree.</summary>
    public static GitOpResult SwitchMerge(string worktree, string branch) => Refuse(branch) ?? Run(worktree, "switch", "--merge", branch);

    public static GitOpResult SwitchDetach(string worktree, string rev) => Refuse(rev) ?? Run(worktree, "switch", "--detach", rev);

    /// <summary>A local branch tracking <paramref name="remoteBranch"/> (<c>origin/x</c>), checked out here.</summary>
    public static GitOpResult SwitchTrack(string worktree, string remoteBranch) =>
        Refuse(remoteBranch) ?? Run(worktree, "switch", "--track", remoteBranch);

    public static GitOpResult CreateBranch(string worktree, string name, string at, bool checkout) =>
        Refuse(name, at) ?? (checkout ? Run(worktree, "switch", "-c", name, at) : Run(worktree, "branch", name, at));

    /// <summary>Stashes every change including untracked files, labelled so it can be found in the rail.</summary>
    public static GitOpResult StashPush(string worktree, string message) => Run(worktree, "stash", "push", "-u", "-m", message);

    public static GitOpResult Merge(string worktree, string rev) => Refuse(rev) ?? Run(worktree, "merge", "--no-edit", rev);

    public static GitOpResult FastForward(string worktree, string rev) => Refuse(rev) ?? Run(worktree, "merge", "--ff-only", rev);

    public static GitOpResult Rebase(string worktree, string onto) => Refuse(onto) ?? Run(worktree, "rebase", onto);

    public static GitOpResult CherryPick(string worktree, string hash, bool noCommit) =>
        Refuse(hash) ?? (noCommit ? Run(worktree, "cherry-pick", "-n", hash) : Run(worktree, "cherry-pick", hash));

    /// <summary>Continues the operation in progress, taking git's default message rather than opening an editor.</summary>
    public static GitOpResult Continue(string worktree, GitInProgress op) => op switch
    {
        GitInProgress.Merge => Run(worktree, "-c", "core.editor=true", "merge", "--continue"),
        GitInProgress.Rebase => Run(worktree, "-c", "core.editor=true", "rebase", "--continue"),
        GitInProgress.CherryPick => Run(worktree, "-c", "core.editor=true", "cherry-pick", "--continue"),
        GitInProgress.Revert => Run(worktree, "-c", "core.editor=true", "revert", "--continue"),
        _ => new GitOpResult(false, "Nothing is in progress.", ""),
    };

    public static GitOpResult Abort(string worktree, GitInProgress op) => op switch
    {
        GitInProgress.Merge => Run(worktree, "merge", "--abort"),
        GitInProgress.Rebase => Run(worktree, "rebase", "--abort"),
        GitInProgress.CherryPick => Run(worktree, "cherry-pick", "--abort"),
        GitInProgress.Revert => Run(worktree, "revert", "--abort"),
        _ => new GitOpResult(false, "Nothing is in progress.", ""),
    };

    /// <summary><c>git worktree add</c>: a new worktree at <paramref name="path"/> on <paramref name="branch"/>, or on a
    /// new branch <paramref name="newBranch"/> from <paramref name="startPoint"/>.</summary>
    public static GitOpResult WorktreeAdd(string repo, string path, string? branch, string? newBranch, string? startPoint) =>
        Refuse(path, branch, newBranch, startPoint) ?? (newBranch is not null
            ? Run(repo, "worktree", "add", "-b", newBranch, path, startPoint ?? "HEAD")
            : Run(repo, "worktree", "add", path, branch ?? "HEAD"));

    /// <summary>Where HEAD is now (branch, or null when detached, plus the commit), for Undo. Null on failure.</summary>
    public static GitHeadState? Head(string worktree)
    {
        var hash = GitRunner.Run(worktree, 6000, "rev-parse", "HEAD");
        if (hash.Exit != 0) return null;
        var branch = GitRunner.Run(worktree, 6000, "symbolic-ref", "--quiet", "--short", "HEAD");
        return new GitHeadState(branch.Exit == 0 ? branch.Stdout.Trim() : null, hash.Stdout.Trim());
    }

    /// <summary>Puts HEAD back where <paramref name="before"/> recorded it.</summary>
    public static GitOpResult Restore(string worktree, GitHeadState before) =>
        before.Branch is { } b ? Switch(worktree, b) : SwitchDetach(worktree, before.Hash);

    /// <summary>What <paramref name="worktree"/> is in the middle of, read from the marker files in its git dir.</summary>
    public static GitInProgress InProgress(string worktree)
    {
        if (GraphLoader.GitDirOf(worktree) is not { } dir) return GitInProgress.None;
        if (Directory.Exists(Path.Combine(dir, "rebase-merge")) || Directory.Exists(Path.Combine(dir, "rebase-apply")))
            return GitInProgress.Rebase;
        if (File.Exists(Path.Combine(dir, "MERGE_HEAD"))) return GitInProgress.Merge;
        if (File.Exists(Path.Combine(dir, "CHERRY_PICK_HEAD"))) return GitInProgress.CherryPick;
        if (File.Exists(Path.Combine(dir, "REVERT_HEAD"))) return GitInProgress.Revert;
        return GitInProgress.None;
    }

    // Ref names can't start with '-' (check-ref-format), but a hash or name typed by a person could, and git would
    // read it as an option. Refuse rather than pass it on.
    private static GitOpResult? Refuse(params string?[] values) =>
        values.FirstOrDefault(v => v is { Length: > 0 } && v[0] == '-') is { } bad
            ? new GitOpResult(false, $"'{bad}' looks like an option, not a branch or commit.", "")
            : null;

    private static GitOpResult Run(string worktree, params string[] args)
    {
        var command = "git " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        try
        {
            var gate = Gates.GetOrAdd(GitCheckoutScanner.FindRoot(worktree) is { } root && GitWorktreeScanner.CommonDir(root) is { } common
                ? common : worktree, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(TimeoutMs))
                return new GitOpResult(false, "Another git operation in this repo is still running.", command);
            try
            {
                var r = GitRunner.Run(worktree, TimeoutMs, GitRunner.Trust.UserAction, null, args);
                if (r.Exit != 0 && IsLockHeld(r.Stderr))
                {
                    Thread.Sleep(700);
                    r = GitRunner.Run(worktree, TimeoutMs, GitRunner.Trust.UserAction, null, args);
                }
                if (r.Exit == 0)
                    return new GitOpResult(true, FirstLine(r.Stdout, ""), command);
                var message = IsLockHeld(r.Stderr)
                    ? $"Another git process is using {Path.GetFileName(worktree)} (index.lock is held). Try again in a moment."
                    : FirstLine(r.Stderr, r.Exit == -1 ? "git didn't finish in time." : "git failed.");
                return new GitOpResult(false, message, command) { Conflicted = IsConflict(r.Stdout + r.Stderr) };
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception e)
        {
            return new GitOpResult(false, e.Message, command);
        }
    }

    internal static bool IsLockHeld(string stderr) => stderr.Contains("index.lock", StringComparison.Ordinal);

    internal static bool IsConflict(string output) =>
        output.Contains("CONFLICT", StringComparison.Ordinal) || output.Contains("could not apply", StringComparison.Ordinal)
        || output.Contains("fix conflicts", StringComparison.OrdinalIgnoreCase);

    // git's useful line: the first that isn't a "hint:" (stderr leads with hints when a command is refused).
    internal static string FirstLine(string text, string fallback)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("hint:", StringComparison.Ordinal)) continue;
            return line.StartsWith("error: ", StringComparison.Ordinal) ? line[7..]
                : line.StartsWith("fatal: ", StringComparison.Ordinal) ? line[7..] : line;
        }
        return fallback;
    }
}
