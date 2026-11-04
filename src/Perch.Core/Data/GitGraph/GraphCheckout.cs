namespace Perch.Data.GitGraph;

/// <summary>What double-clicking a branch or commit in the graph would do in the window's worktree.</summary>
public enum CheckoutAction
{
    /// <summary>Nothing to check out (a stash, a WIP row, or nothing loaded).</summary>
    None,

    /// <summary>The worktree is already there.</summary>
    AlreadyHere,

    /// <summary><c>git switch &lt;branch&gt;</c>.</summary>
    Switch,

    /// <summary><c>git switch --track &lt;remote/branch&gt;</c>: no local branch of that name yet.</summary>
    Track,

    /// <summary>No branch to land on (a bare commit or a tag): detach, or create a branch there.</summary>
    Detach,

    /// <summary>The branch is checked out in another worktree, and git allows a branch in only one.</summary>
    HeldElsewhere,
}

/// <summary>
/// A double-click resolved against the graph: the <see cref="Action"/>, the <see cref="Target"/> git is given (a
/// branch, <c>origin/x</c>, a tag or a hash), the <see cref="Commit"/> it lands on, and for
/// <see cref="CheckoutAction.HeldElsewhere"/> the worktree that holds it.
/// </summary>
public sealed record CheckoutPlan(CheckoutAction Action, string Target, string Commit, GraphWorktree? Holder = null)
{
    public static readonly CheckoutPlan Nothing = new(CheckoutAction.None, "", "");

    /// <summary>Whether carrying it out moves the window's worktree (so its uncommitted changes matter).</summary>
    public bool MovesHead => Action is CheckoutAction.Switch or CheckoutAction.Track or CheckoutAction.Detach;
}

/// <summary>Decides what a double-click means. Pure: reads only the snapshot, so it's unit-tested.</summary>
internal static class GraphCheckout
{
    /// <summary>A double-click on a ref pill or a rail row, checking out in <paramref name="here"/> (the window's own
    /// worktree when null; a drag onto a worktree card names another).</summary>
    public static CheckoutPlan ForRef(GraphSnapshot snap, GraphRef r, GraphWorktree? here = null)
    {
        here ??= snap.Current;
        switch (r.Kind)
        {
            case GraphRefKind.LocalBranch:
                return ForBranch(snap, here, r.Name, r.Target);
            case GraphRefKind.RemoteBranch:
                // A local branch of the same name already exists: going there is what was meant; `switch --track`
                // would only fail on the name clash.
                if (LocalOf(snap, r) is { } l)
                    return ForBranch(snap, here, l.Name, l.Target);
                return new CheckoutPlan(CheckoutAction.Track, r.Name, r.Target);
            case GraphRefKind.Tag:
                return Detached(here, r.Name, r.Target);
            default:
                return CheckoutPlan.Nothing;
        }
    }

    /// <summary>The local branch named like remote branch <paramref name="remote"/> (<c>origin/x</c> → <c>x</c>), if any.</summary>
    public static GraphRef? LocalOf(GraphSnapshot snap, GraphRef remote)
    {
        string local = LocalName(remote.Name);
        return snap.Refs.FirstOrDefault(x => x.Kind == GraphRefKind.LocalBranch && x.Name == local);
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be a new branch, as the dialog says it, or null when it can: git's
    /// <c>check-ref-format --branch</c> rules, checked here so the dialog can answer as the name is typed instead of
    /// after git has run, plus a local branch of that name already existing.
    /// </summary>
    public static string? BranchNameProblem(string name, IReadOnlyList<GraphRef> refs) =>
        RefNameProblem(name, "Branch")
        ?? (refs.Any(r => r.Kind == GraphRefKind.LocalBranch && r.Name == name) ? $"{name} already exists." : null);

    /// <summary>The same rules for a new tag, plus a tag of that name already existing.</summary>
    public static string? TagNameProblem(string name, IReadOnlyList<GraphRef> refs) =>
        RefNameProblem(name, "Tag")
        ?? (refs.Any(r => r.Kind == GraphRefKind.Tag && r.Name == name) ? $"A tag called {name} already exists." : null);

    private static string? RefNameProblem(string name, string what)
    {
        string lower = what.ToLowerInvariant();
        if (name.Length == 0) return "Type a name.";
        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return $"{what} names can't contain spaces.";
        if (name.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) is var i and >= 0) return $"{what} names can't contain '{name[i]}'.";
        if (name[0] == '-') return $"{what} names can't start with '-'.";
        if (name == "HEAD") return $"HEAD can't be a {lower} name.";
        if (name == "@" || name.Contains("@{", StringComparison.Ordinal)) return $"{what} names can't be '@' or contain '@{{'.";
        if (name.Contains("..", StringComparison.Ordinal)) return $"{what} names can't contain '..'.";
        if (name.EndsWith('/') || name.EndsWith('.')) return $"{what} names can't end with '{name[^1]}'.";
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0) return $"{what} names can't contain '//' or start with '/'.";
            if (part[0] == '.') return $"No part of a {lower} name can start with '.'.";
            if (part.EndsWith(".lock", StringComparison.Ordinal)) return $"No part of a {lower} name can end with '.lock'.";
        }
        return null;
    }

    /// <summary><c>origin/x</c> → <c>x</c>.</summary>
    public static string LocalName(string remoteBranch) =>
        remoteBranch.IndexOf('/') is var i and >= 0 ? remoteBranch[(i + 1)..] : remoteBranch;

    /// <summary>A double-click on a commit row (not on a pill): its local branch if it has one (this worktree's own
    /// first, then one no other worktree holds), else a remote branch to track, else the bare commit.</summary>
    public static CheckoutPlan ForCommit(GraphSnapshot snap, string hash)
    {
        if (hash.StartsWith(GraphWorktree.WipPrefix, StringComparison.Ordinal) || hash.StartsWith(GraphStash.Prefix, StringComparison.Ordinal))
            return CheckoutPlan.Nothing;
        var refs = snap.RefsAt(hash);
        var locals = refs.Where(x => x.Kind == GraphRefKind.LocalBranch).ToList();
        if (locals.Count > 0)
        {
            var current = snap.Current?.Branch;
            var pick = locals.FirstOrDefault(x => x.Name == current)
                       ?? locals.FirstOrDefault(x => HolderOf(snap, snap.Current, x.Name) is null)
                       ?? locals[0];
            return ForBranch(snap, snap.Current, pick.Name, pick.Target);
        }
        if (refs.FirstOrDefault(x => x.Kind == GraphRefKind.RemoteBranch) is { } remote)
            return ForRef(snap, remote);
        return Detached(snap.Current, hash, hash);
    }

    private static CheckoutPlan ForBranch(GraphSnapshot snap, GraphWorktree? here, string branch, string commit)
    {
        if (here is not null && here.Branch == branch)
            return new CheckoutPlan(CheckoutAction.AlreadyHere, branch, commit);
        if (HolderOf(snap, here, branch) is { } holder)
            return new CheckoutPlan(CheckoutAction.HeldElsewhere, branch, commit, holder);
        return new CheckoutPlan(CheckoutAction.Switch, branch, commit);
    }

    private static CheckoutPlan Detached(GraphWorktree? here, string target, string commit) =>
        here is { Branch: null } && here.Head == commit
            ? new CheckoutPlan(CheckoutAction.AlreadyHere, target, commit)
            : new CheckoutPlan(CheckoutAction.Detach, target, commit);

    private static GraphWorktree? HolderOf(GraphSnapshot snap, GraphWorktree? here, string branch) =>
        snap.Worktrees.FirstOrDefault(w => w.Branch == branch && !string.Equals(w.Path, here?.Path, StringComparison.OrdinalIgnoreCase));
}
