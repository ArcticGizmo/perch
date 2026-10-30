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

/// <summary>What to do with a dirty worktree's changes when checking out.</summary>
public enum DirtyChoice
{
    /// <summary>The tree is clean (or the caller lets git refuse).</summary>
    None,

    /// <summary>Stash everything (untracked too) first.</summary>
    Stash,

    /// <summary>Carry the changes across (<c>switch --merge</c>).</summary>
    Carry,
}

/// <summary>What the graph's Pull button does: GitKraken's four choices.</summary>
public enum PullMode
{
    /// <summary>Fetch every remote and prune; no branch moves.</summary>
    FetchAll,

    /// <summary><c>pull --ff-only</c>: refuses when the branch has diverged.</summary>
    FastForwardOnly,

    Merge,
    Rebase,
}

/// <summary>How to undo a checkout: where HEAD was, the stash entry it made (popped back on Undo), and whether the
/// changes were carried across.</summary>
public sealed record CheckoutUndo(GitHeadState Before, string? Stash, bool Carried);

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

    /// <summary>The newest stash entry's commit, so an Undo can pop exactly the entry a checkout made.</summary>
    public static string? StashTop(string worktree)
    {
        var r = GitRunner.Run(worktree, 6000, "rev-parse", "--verify", "--quiet", "refs/stash");
        return r.Exit == 0 ? r.Stdout.Trim() : null;
    }

    /// <summary>Pops the stash entry whose commit is <paramref name="stashHash"/>, wherever it now sits in the list
    /// (another stash may have been pushed on top since).</summary>
    public static GitOpResult StashPop(string worktree, string stashHash) => StashByHash(worktree, "pop", stashHash);

    /// <summary>Applies the stash entry <paramref name="stashHash"/> and keeps it in the list.</summary>
    public static GitOpResult StashApply(string worktree, string stashHash) => StashByHash(worktree, "apply", stashHash);

    /// <summary>Deletes the stash entry <paramref name="stashHash"/> without applying it.</summary>
    public static GitOpResult StashDrop(string worktree, string stashHash) => StashByHash(worktree, "drop", stashHash);

    /// <summary>Puts a dropped entry back in the list (its commit lingers until git collects it), for Undo.</summary>
    public static GitOpResult StashRestore(string worktree, string stashHash, string message) =>
        Refuse(stashHash) ?? Run(worktree, "stash", "store", "-m", message, stashHash);

    // Entries are addressed by position, which moves as others are pushed and dropped, so find this one's now.
    private static GitOpResult StashByHash(string worktree, string verb, string stashHash)
    {
        var list = GitRunner.Run(worktree, 6000, "stash", "list", "--format=%H");
        int index = list.Exit == 0
            ? Array.IndexOf(list.Stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries), stashHash)
            : -1;
        return index < 0
            ? new GitOpResult(false, "That stash is no longer there.", $"git stash {verb}")
            : Run(worktree, "stash", verb, $"stash@{{{index}}}");
    }

    /// <summary>Pops the newest stash entry. A pop that conflicts leaves the entry in the list, as git does.</summary>
    public static GitOpResult StashPopTop(string worktree) => Run(worktree, "stash", "pop");

    /// <summary>
    /// Pushes this worktree's branch. One with an upstream pushes there; one without is published to
    /// <c>origin</c> (else the only remote) and set to track it. Never forces.
    /// </summary>
    public static GitOpResult Push(string worktree)
    {
        if (Head(worktree) is not { Branch: { } branch })
            return new GitOpResult(false, "HEAD is detached, so there's no branch to push.", "git push");
        var upstream = GitRunner.Run(worktree, 6000, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
        if (upstream.Exit == 0) return RunNetwork(worktree, "push");
        if (PushRemote(worktree) is not { } remote)
            return new GitOpResult(false, "This repo has no remote to push to.", "git push");
        return Refuse(branch) ?? RunNetwork(worktree, "push", "-u", remote, branch);
    }

    // Where a branch with no upstream is published: origin when there is one, else the only remote.
    private static string? PushRemote(string worktree)
    {
        var r = GitRunner.Run(worktree, 6000, "remote");
        if (r.Exit != 0) return null;
        var remotes = r.Stdout.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return remotes.Contains("origin") ? "origin" : remotes.Length == 1 ? remotes[0] : null;
    }

    /// <summary>
    /// Fetches, or pulls into this worktree's branch as <paramref name="mode"/> says. When the branch moved, the undo
    /// puts it back where it was (<c>reset --keep</c>, which refuses rather than lose local changes); a fetch has none.
    /// </summary>
    public static (GitOpResult Result, Func<GitOpResult>? Undo) Pull(string worktree, PullMode mode)
    {
        if (mode == PullMode.FetchAll) return (RunNetwork(worktree, "fetch", "--all", "--prune"), null);
        var before = Head(worktree);
        var r = mode switch
        {
            PullMode.Merge => RunNetwork(worktree, "pull", "--no-rebase", "--no-edit"),
            PullMode.Rebase => RunNetwork(worktree, "pull", "--rebase"),
            _ => RunNetwork(worktree, "pull", "--ff-only"),
        };
        if (!r.Ok || before is not { Branch: { } branch } || Head(worktree) is not { } after || after.Hash == before.Hash)
            return (r, null);
        return (r, () => Head(worktree)?.Branch != branch
            ? new GitOpResult(false, $"{branch} isn't checked out here any more, so the pull can't be undone.", "")
            : Run(worktree, "reset", "--keep", before.Hash));
    }

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

    /// <summary>Drops the commit a rebase or cherry-pick stopped on and carries on with the rest.</summary>
    public static GitOpResult Skip(string worktree, GitInProgress op) => op switch
    {
        GitInProgress.Rebase => Run(worktree, "rebase", "--skip"),
        GitInProgress.CherryPick => Run(worktree, "cherry-pick", "--skip"),
        _ => new GitOpResult(false, "Only a rebase or cherry-pick can skip a commit.", ""),
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

    /// <summary>
    /// Carries out a checkout <paramref name="action"/> (<see cref="CheckoutAction.Switch"/>, <see cref="CheckoutAction.Track"/>
    /// or <see cref="CheckoutAction.Detach"/>; with <paramref name="newBranch"/>, a new branch at <paramref name="target"/>)
    /// in <paramref name="worktree"/>, dealing with its changes as <paramref name="dirty"/> says. A stash made for a
    /// checkout that then fails is popped straight back, so a failure leaves the tree as it was. On success, the
    /// <see cref="CheckoutUndo"/> puts everything back.
    /// </summary>
    public static (GitOpResult Result, CheckoutUndo? Undo) Checkout(string worktree, CheckoutAction action, string target,
        DirtyChoice dirty, string? newBranch = null)
    {
        if (Head(worktree) is not { } before)
            return (new GitOpResult(false, "Couldn't read this worktree's HEAD.", "git rev-parse HEAD"), null);

        string? stash = null;
        if (dirty == DirtyChoice.Stash)
        {
            var label = $"Perch: before checking out {newBranch ?? target}";
            var pushed = StashPush(worktree, label);
            if (!pushed.Ok) return (pushed, null);
            stash = StashTop(worktree);
        }

        bool carry = dirty == DirtyChoice.Carry;
        var r = (newBranch, action, carry) switch
        {
            ({ } name, _, _) => CreateBranch(worktree, name, target, checkout: true),
            (null, CheckoutAction.Switch, false) => Switch(worktree, target),
            (null, CheckoutAction.Switch, true) => SwitchMerge(worktree, target),
            (null, CheckoutAction.Track, _) => Refuse(target) ?? (carry
                ? Run(worktree, "switch", "--merge", "--track", target)
                : SwitchTrack(worktree, target)),
            (null, CheckoutAction.Detach, _) => Refuse(target) ?? (carry
                ? Run(worktree, "switch", "--merge", "--detach", target)
                : SwitchDetach(worktree, target)),
            _ => new GitOpResult(false, "Nothing to check out.", ""),
        };

        if (!r.Ok)
        {
            if (stash is not null) StashPop(worktree, stash);
            return (r, null);
        }
        return (r, new CheckoutUndo(before, stash, carry));
    }

    /// <summary>Undoes a <see cref="Checkout"/>: HEAD back where it was, then the stash it made popped back.</summary>
    public static GitOpResult Undo(string worktree, CheckoutUndo undo)
    {
        var r = Restore(worktree, undo.Before, undo.Carried);
        if (!r.Ok || undo.Stash is null) return r;
        var popped = StashPop(worktree, undo.Stash);
        return popped.Ok ? r : popped;
    }

    /// <summary>
    /// Carries out a drop menu's <paramref name="o"/> for the repo at <paramref name="root"/>. <paramref name="dirty"/>
    /// applies to a checkout; <paramref name="newBranch"/>/<paramref name="newPath"/> to a new worktree that needed a
    /// name. On success the undo puts it back: a moved branch is reset to where it was (<c>reset --keep</c>, which
    /// refuses rather than lose local changes; <c>update-ref</c> with the expected value for a branch no worktree has),
    /// a switch made first is switched back, a new worktree is removed (and the branch made for it deleted, if it
    /// hasn't moved). A failure that didn't stop on conflicts leaves things as they were.
    /// </summary>
    public static (GitOpResult Result, Func<GitOpResult>? Undo) RunDrop(string root, DropOption o, DirtyChoice dirty = DirtyChoice.None,
        string? newBranch = null, string? newPath = null)
    {
        try
        {
            switch (o.Kind)
            {
                case DropKind.Checkout:
                {
                    var wt = o.RunIn!.Path;
                    var (r, undo) = Checkout(wt, o.Checkout, o.Rev, dirty);
                    return (r, undo is null ? null : () => Undo(wt, undo));
                }
                case DropKind.NewWorktree:
                    return AddWorktree(root, o, newBranch, newPath);
                case DropKind.FastForward when o.RunIn is null:
                {
                    var branch = o.Branch!;
                    if (RevParse(root, "refs/heads/" + branch) is not { } old)
                        return (new GitOpResult(false, $"Couldn't find {branch}.", ""), null);
                    if (Refuse(o.Rev, branch) is { } refused) return (refused, null);
                    var r = Run(root, "fetch", ".", $"{o.Rev}:refs/heads/{branch}");
                    if (!r.Ok || RevParse(root, "refs/heads/" + branch) is not { } moved) return (r, null);
                    return (r, () => Run(root, "update-ref", "refs/heads/" + branch, old, moved));
                }
                default:
                    return MoveBranch(o);
            }
        }
        catch (Exception e)
        {
            return (new GitOpResult(false, e.Message, o.Command), null);
        }
    }

    // Merge, rebase, cherry-pick or fast-forward in the worktree that has the branch (switching to it first when none did).
    private static (GitOpResult, Func<GitOpResult>?) MoveBranch(DropOption o)
    {
        var wt = o.RunIn!.Path;
        var branch = o.Branch!;
        if (Refuse(o.Rev, branch) is { } refused) return (refused, null);
        var before = Head(wt);
        if (before is null) return (new GitOpResult(false, "Couldn't read this worktree's HEAD.", ""), null);
        if (RevParse(wt, "refs/heads/" + branch) is not { } old)
            return (new GitOpResult(false, $"Couldn't find {branch}.", ""), null);

        GitHeadState? switchedFrom = null;
        if (o.SwitchFirst)
        {
            switchedFrom = before;
            if (o.Kind != DropKind.Rebase)   // `rebase <onto> <branch>` does its own switch
            {
                var sw = Switch(wt, branch);
                if (!sw.Ok) return (sw, null);
            }
        }

        var r = o.Kind switch
        {
            DropKind.Merge => Merge(wt, o.Rev),
            DropKind.FastForward => FastForward(wt, o.Rev),
            DropKind.Rebase => o.SwitchFirst ? Run(wt, "rebase", o.Rev, branch) : Rebase(wt, o.Rev),
            DropKind.CherryPick => CherryPick(wt, o.Rev, noCommit: false),
            _ => new GitOpResult(false, "Not a branch operation.", o.Command),
        };
        if (!r.Ok)
        {
            if (!r.Conflicted && switchedFrom is not null && Head(wt)?.Branch != switchedFrom.Branch)
                Restore(wt, switchedFrom);
            return (r, null);
        }

        return (r, () =>
        {
            if (Head(wt)?.Branch != branch)
                return new GitOpResult(false, $"{branch} isn't checked out in {Path.GetFileName(wt)} any more, so it can't be put back.", "");
            var reset = Run(wt, "reset", "--keep", old);
            if (!reset.Ok || switchedFrom is null) return reset;
            var back = Restore(wt, switchedFrom);
            return back.Ok ? reset : back;
        });
    }

    private static (GitOpResult, Func<GitOpResult>?) AddWorktree(string root, DropOption o, string? newBranch, string? newPath)
    {
        var path = newPath ?? o.NewPath;
        var branch = o.NeedsName ? newBranch : o.Branch;
        if (path is null || branch is not { Length: > 0 })
            return (new GitOpResult(false, "The new worktree needs a branch name.", ""), null);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            return (new GitOpResult(false, $"{path} already exists.", ""), null);
        if (PrWorktree.Inspect(root) is { } ctx)
            PrWorktree.EnsureExcluded(ctx.Set.Root, ctx.Layout, ctx.Set.Bare);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        bool created = o.NeedsName || o.Track;
        var r = o.NeedsName ? WorktreeAdd(root, path, null, branch, o.Rev)
            : o.Track ? Refuse(path, branch, o.Rev) ?? Run(root, "worktree", "add", "--track", "-b", branch, path, o.Rev)
            : WorktreeAdd(root, path, branch, null, null);
        if (!r.Ok) return (r, null);
        var made = created ? RevParse(root, "refs/heads/" + branch) : null;

        return (r, () =>
        {
            var removed = Run(root, "worktree", "remove", path);
            if (!removed.Ok || made is null) return removed;
            var deleted = Run(root, "update-ref", "-d", "refs/heads/" + branch, made);
            return deleted.Ok ? removed : deleted;
        });
    }

    private static string? RevParse(string dir, string rev)
    {
        var r = GitRunner.Run(dir, 6000, "rev-parse", "--verify", "--quiet", rev);
        return r.Exit == 0 ? r.Stdout.Trim() : null;
    }

    /// <summary><c>git branch -d</c> (or <c>-D</c> with <paramref name="force"/>): a local branch only. Without force git
    /// refuses one with commits merged nowhere, which <see cref="IsUnmerged"/> spots so the caller can ask first.</summary>
    public static GitOpResult DeleteBranch(string repo, string branch, bool force) =>
        Refuse(branch) ?? Run(repo, "branch", force ? "-D" : "-d", branch);

    /// <summary>The refusal <c>git branch -d</c> gives for a branch whose commits aren't merged anywhere.</summary>
    public static bool IsUnmerged(GitOpResult r) => !r.Ok && r.Message.Contains("not fully merged", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>git worktree remove</c>. Without <paramref name="force"/> git refuses a worktree with changes, which
    /// is the point: the caller asks the user before forcing.</summary>
    public static GitOpResult WorktreeRemove(string repo, string path, bool force) =>
        Refuse(path) ?? (force ? Run(repo, "worktree", "remove", "--force", path) : Run(repo, "worktree", "remove", path));

    /// <summary><c>git worktree prune</c>: forgets worktrees whose folders were deleted by hand.</summary>
    public static GitOpResult WorktreePrune(string repo) => Run(repo, "worktree", "prune", "-v");

    /// <summary>Where HEAD is now (branch, or null when detached, plus the commit), for Undo. Null on failure.</summary>
    public static GitHeadState? Head(string worktree)
    {
        var hash = GitRunner.Run(worktree, 6000, "rev-parse", "HEAD");
        if (hash.Exit != 0) return null;
        var branch = GitRunner.Run(worktree, 6000, "symbolic-ref", "--quiet", "--short", "HEAD");
        return new GitHeadState(branch.Exit == 0 ? branch.Stdout.Trim() : null, hash.Stdout.Trim());
    }

    /// <summary>Puts HEAD back where <paramref name="before"/> recorded it, carrying local changes back across when the
    /// operation being undone carried them over.</summary>
    public static GitOpResult Restore(string worktree, GitHeadState before, bool carryChanges = false) =>
        (before.Branch, carryChanges) switch
        {
            ({ } b, false) => Switch(worktree, b),
            ({ } b, true) => SwitchMerge(worktree, b),
            (null, false) => SwitchDetach(worktree, before.Hash),
            (null, true) => Refuse(before.Hash) ?? Run(worktree, "switch", "--merge", "--detach", before.Hash),
        };

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

    // A fetch, pull or push. Its stdin is redirected and closed, so a credential prompt that would read the terminal a
    // tray started from fails at once instead of hanging; a credential manager's own window still works.
    private static GitOpResult RunNetwork(string worktree, params string[] args) => Run(worktree, "", args);

    private static GitOpResult Run(string worktree, params string[] args) => Run(worktree, null, args);

    private static GitOpResult Run(string worktree, string? stdin, string[] args)
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
                var r = GitRunner.Run(worktree, TimeoutMs, GitRunner.Trust.UserAction, stdin, args);
                if (r.Exit != 0 && IsLockHeld(r.Stderr))
                {
                    Thread.Sleep(700);
                    r = GitRunner.Run(worktree, TimeoutMs, GitRunner.Trust.UserAction, stdin, args);
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
