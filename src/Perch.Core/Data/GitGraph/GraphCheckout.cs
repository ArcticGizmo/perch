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
    /// <summary>A double-click on a ref pill or a rail row.</summary>
    public static CheckoutPlan ForRef(GraphSnapshot snap, GraphRef r)
    {
        switch (r.Kind)
        {
            case GraphRefKind.LocalBranch:
                return ForBranch(snap, r.Name, r.Target);
            case GraphRefKind.RemoteBranch:
                // A local branch of the same name already exists: going there is what was meant; `switch --track`
                // would only fail on the name clash.
                int slash = r.Name.IndexOf('/');
                string local = slash >= 0 ? r.Name[(slash + 1)..] : r.Name;
                if (snap.Refs.FirstOrDefault(x => x.Kind == GraphRefKind.LocalBranch && x.Name == local) is { } l)
                    return ForBranch(snap, l.Name, l.Target);
                return new CheckoutPlan(CheckoutAction.Track, r.Name, r.Target);
            case GraphRefKind.Tag:
                return Detached(snap, r.Name, r.Target);
            default:
                return CheckoutPlan.Nothing;
        }
    }

    /// <summary>A double-click on a commit row (not on a pill): its local branch if it has one (this worktree's own
    /// first, then one no other worktree holds), else a remote branch to track, else the bare commit.</summary>
    public static CheckoutPlan ForCommit(GraphSnapshot snap, string hash)
    {
        if (hash.StartsWith(GraphWorktree.WipPrefix, StringComparison.Ordinal)) return CheckoutPlan.Nothing;
        var refs = snap.RefsAt(hash);
        var locals = refs.Where(x => x.Kind == GraphRefKind.LocalBranch).ToList();
        if (locals.Count > 0)
        {
            var current = snap.Current?.Branch;
            var pick = locals.FirstOrDefault(x => x.Name == current)
                       ?? locals.FirstOrDefault(x => HolderOf(snap, x.Name) is null)
                       ?? locals[0];
            return ForBranch(snap, pick.Name, pick.Target);
        }
        if (refs.FirstOrDefault(x => x.Kind == GraphRefKind.RemoteBranch) is { } remote)
            return ForRef(snap, remote);
        return Detached(snap, hash, hash);
    }

    private static CheckoutPlan ForBranch(GraphSnapshot snap, string branch, string commit)
    {
        if (snap.Current is { } cur && cur.Branch == branch)
            return new CheckoutPlan(CheckoutAction.AlreadyHere, branch, commit);
        if (HolderOf(snap, branch) is { } holder)
            return new CheckoutPlan(CheckoutAction.HeldElsewhere, branch, commit, holder);
        return new CheckoutPlan(CheckoutAction.Switch, branch, commit);
    }

    private static CheckoutPlan Detached(GraphSnapshot snap, string target, string commit) =>
        snap.Current is { Branch: null } cur && cur.Head == commit
            ? new CheckoutPlan(CheckoutAction.AlreadyHere, target, commit)
            : new CheckoutPlan(CheckoutAction.Detach, target, commit);

    private static GraphWorktree? HolderOf(GraphSnapshot snap, string branch) =>
        snap.Worktrees.FirstOrDefault(w => !w.IsCurrent && w.Branch == branch);
}
