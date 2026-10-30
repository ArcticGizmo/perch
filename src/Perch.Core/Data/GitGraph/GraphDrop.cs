namespace Perch.Data.GitGraph;

/// <summary>What is being dragged: a branch (local or remote) or a commit.</summary>
public sealed record DragSource(GraphRef? Ref, GraphCommit? Commit)
{
    public static DragSource Of(GraphRef r) => new(r, null);
    public static DragSource Of(GraphCommit c) => new(null, c);

    /// <summary>What git is given: the branch name, or the commit's hash.</summary>
    public string Rev => Ref?.Name ?? Commit!.Hash;

    /// <summary>The commit it points at.</summary>
    public string Hash => Ref?.Target ?? Commit!.Hash;

    /// <summary>How menus name it: the branch, or the short hash.</summary>
    public string Label => Ref?.Name ?? Commit!.ShortHash;

    public bool IsLocalBranch => Ref?.Kind == GraphRefKind.LocalBranch;
    public bool IsRemoteBranch => Ref?.Kind == GraphRefKind.RemoteBranch;
}

/// <summary>Where it was dropped: a branch, a worktree (its card or its WIP row), or the "New worktree" zone.</summary>
public sealed record DropTarget(GraphRef? Ref, GraphWorktree? Worktree, bool NewWorktree = false)
{
    public static DropTarget Of(GraphRef r) => new(r, null);
    public static DropTarget Of(GraphWorktree w) => new(null, w);
    public static readonly DropTarget NewWorktreeZone = new(null, null, true);
}

public enum DropKind
{
    /// <summary><c>git merge &lt;Rev&gt;</c> in <see cref="DropOption.Branch"/>'s worktree.</summary>
    Merge,

    /// <summary>Moves <see cref="DropOption.Branch"/> forward to <see cref="DropOption.Rev"/>: <c>merge --ff-only</c> in its
    /// worktree, or <c>fetch . rev:branch</c> when no worktree has it checked out.</summary>
    FastForward,

    /// <summary><c>git rebase &lt;Rev&gt;</c> in <see cref="DropOption.Branch"/>'s worktree.</summary>
    Rebase,

    /// <summary><c>git cherry-pick &lt;Rev&gt;</c> onto <see cref="DropOption.Branch"/>.</summary>
    CherryPick,

    /// <summary>Check <see cref="DropOption.Rev"/> out in <see cref="DropOption.RunIn"/> (a <see cref="CheckoutAction"/>).</summary>
    Checkout,

    /// <summary><c>git worktree add</c> at <see cref="DropOption.NewPath"/>, on <see cref="DropOption.Branch"/> (or a branch
    /// named by the user when <see cref="DropOption.NeedsName"/>).</summary>
    NewWorktree,
}

/// <summary>
/// One choice in a drop menu. <see cref="RunIn"/> is the worktree git runs in (null for a fast-forward of a branch no
/// worktree has checked out, which runs in the repo root). <see cref="SwitchFirst"/>: the branch isn't checked out
/// anywhere, so the window's own worktree switches to it first (which needs a clean tree). <see cref="Command"/> is
/// what the menu and the toast show.
/// </summary>
public sealed record DropOption(DropKind Kind, string Label, string Command, GraphWorktree? RunIn, string Rev, string? Branch,
    bool SwitchFirst = false)
{
    /// <summary>For <see cref="DropKind.Checkout"/>: how (switch, track or detach).</summary>
    public CheckoutAction Checkout { get; init; }

    /// <summary>For <see cref="DropKind.NewWorktree"/>: where it goes.</summary>
    public string? NewPath { get; init; }

    /// <summary>For <see cref="DropKind.NewWorktree"/>: the branch is held elsewhere or the source is a commit, so the
    /// user names a new branch first.</summary>
    public bool NeedsName { get; init; }

    /// <summary>For <see cref="DropKind.NewWorktree"/> from a remote branch: the new branch tracks it.</summary>
    public bool Track { get; init; }
}

/// <summary>A drop's menu: a line saying why nothing (or what) is on offer, and the choices.</summary>
public sealed record DropMenu(string? Note, IReadOnlyList<DropOption> Options)
{
    public static readonly DropMenu Empty = new(null, []);
}

/// <summary>
/// What dropping one thing on another offers (the table in <c>docs/git-graph-design.html</c>): a branch onto a branch
/// merges, fast-forwards or rebases; a commit onto a branch cherry-picks; anything onto a worktree checks it out there;
/// onto "New worktree" makes one. Each operation runs in the worktree that owns the branch it changes, so nothing has
/// to be switched first unless no worktree has it. Pure: reads the snapshot (and the layout, for new worktree paths).
/// </summary>
internal static class GraphDrop
{
    /// <param name="layout">How the repo lays out worktrees, and <paramref name="mainRoot"/> the main worktree's folder it
    /// renders against (the snapshot's root is the window's own worktree, which may be a linked one).</param>
    public static DropMenu Menu(GraphSnapshot snap, DragSource src, DropTarget target, WorktreeLayout? layout = null,
        string? mainRoot = null)
    {
        if (target.NewWorktree) return NewWorktree(snap, src, layout, mainRoot ?? snap.Root);
        if (target.Worktree is { } wt) return OntoWorktree(snap, src, wt);
        if (target.Ref is not { } t) return DropMenu.Empty;
        if (src.Ref is { } s && s.FullName == t.FullName) return DropMenu.Empty;
        return t.Kind switch
        {
            GraphRefKind.LocalBranch => OntoBranch(snap, src, t),
            GraphRefKind.RemoteBranch => OntoRemote(snap, src, t),
            _ => new DropMenu($"Drop on a branch or a worktree; {t.Name} is a {(t.Kind == GraphRefKind.Tag ? "tag" : "stash")}.", []),
        };
    }

    // A branch onto a local branch: fast-forward when the target is behind, else merge and (for a local source) rebase.
    // A commit cherry-picks.
    private static DropMenu OntoBranch(GraphSnapshot snap, DragSource src, GraphRef t)
    {
        var holder = WorktreeOf(snap, t.Name);
        if (src.Commit is not null)
        {
            if (IsAncestor(snap, src.Hash, t.Target) == true)
                return new DropMenu($"{t.Name} already contains {src.Label}.", []);
            return new DropMenu(null, [ChangeBranch(snap, DropKind.CherryPick, $"Cherry-pick {src.Label} onto {t.Name}",
                $"git cherry-pick {src.Label}", t.Name, src.Rev, holder)]);
        }

        if (IsAncestor(snap, src.Hash, t.Target) == true)
            return new DropMenu($"{t.Name} already contains {src.Label}.", []);
        if (IsAncestor(snap, t.Target, src.Hash) == true)
            return new DropMenu(null, [FastForward(t.Name, src.Rev, holder)]);

        var options = new List<DropOption>
        {
            ChangeBranch(snap, DropKind.Merge, $"Merge {src.Label} into {t.Name}", $"git merge {src.Label}", t.Name, src.Rev, holder),
        };
        if (src.IsLocalBranch)
            options.Add(ChangeBranch(snap, DropKind.Rebase, $"Rebase {src.Label} onto {t.Name}", $"git rebase {t.Name}",
                src.Rev, t.Name, WorktreeOf(snap, src.Rev)));
        return new DropMenu(null, options);
    }

    // A local branch onto a remote branch: rebase onto it, or fast-forward to it when the local one is just behind.
    private static DropMenu OntoRemote(GraphSnapshot snap, DragSource src, GraphRef t)
    {
        if (!src.IsLocalBranch)
            return new DropMenu($"{t.Name} is a remote branch; drop a local branch on it to rebase onto it.", []);
        var own = WorktreeOf(snap, src.Rev);
        if (IsAncestor(snap, t.Target, src.Hash) == true)
            return new DropMenu($"{src.Label} already contains {t.Name}.", []);
        if (IsAncestor(snap, src.Hash, t.Target) == true)
            return new DropMenu(null, [FastForward(src.Rev, t.Name, own)]);
        return new DropMenu(null, [ChangeBranch(snap, DropKind.Rebase, $"Rebase {src.Label} onto {t.Name}", $"git rebase {t.Name}",
            src.Rev, t.Name, own)]);
    }

    // Anything onto a worktree: check it out there. A commit can also be cherry-picked onto that worktree's branch.
    private static DropMenu OntoWorktree(GraphSnapshot snap, DragSource src, GraphWorktree wt)
    {
        var options = new List<DropOption>();
        string? note = null;
        if (src.Ref is { } r)
        {
            var plan = GraphCheckout.ForRef(snap, r, wt);
            switch (plan.Action)
            {
                case CheckoutAction.AlreadyHere:
                    return new DropMenu($"{wt.Name} is already on {plan.Target}.", []);
                case CheckoutAction.HeldElsewhere:
                    note = $"{plan.Target} is checked out in {plan.Holder!.Name}, and a branch can be in only one worktree.";
                    options.Add(Checkout(wt, CheckoutAction.Detach, plan.Commit, $"Check out {Short(plan.Commit)} in {wt.Name}, detached"));
                    break;
                case CheckoutAction.Switch:
                    options.Add(Checkout(wt, CheckoutAction.Switch, plan.Target, $"Check out {plan.Target} in {wt.Name}"));
                    break;
                case CheckoutAction.Track:
                    options.Add(Checkout(wt, CheckoutAction.Track, plan.Target,
                        $"Check out {GraphCheckout.LocalName(plan.Target)} (tracking {plan.Target}) in {wt.Name}"));
                    break;
                case CheckoutAction.Detach:
                    options.Add(Checkout(wt, CheckoutAction.Detach, plan.Target, $"Check out {plan.Target} in {wt.Name}, detached"));
                    break;
            }
        }
        else if (src.Commit is { } c)
        {
            if (wt.Branch is { } b && IsAncestor(snap, c.Hash, wt.Head ?? "") != true)
                options.Add(new DropOption(DropKind.CherryPick, $"Cherry-pick {c.ShortHash} onto {b} in {wt.Name}",
                    $"git cherry-pick {c.ShortHash}", wt, c.Hash, b));
            if (wt.Head != c.Hash || wt.Branch is not null)
                options.Add(Checkout(wt, CheckoutAction.Detach, c.Hash, $"Check out {c.ShortHash} in {wt.Name}, detached"));
        }
        return new DropMenu(note, options);
    }

    // A worktree of its own: an unheld local branch goes straight in; a remote branch gets a tracking branch; a held
    // branch or a commit needs a new branch, named by the user.
    private static DropMenu NewWorktree(GraphSnapshot snap, DragSource src, WorktreeLayout? layout, string mainRoot)
    {
        string Path(string name) => NewWorktreePath(mainRoot, layout, name);

        if (src.Ref is { Kind: GraphRefKind.RemoteBranch } remote && GraphCheckout.LocalOf(snap, remote) is { } local)
            src = DragSource.Of(local);

        switch (src.Ref?.Kind)
        {
            case GraphRefKind.LocalBranch when WorktreeOf(snap, src.Rev) is null:
            {
                var path = Path(src.Rev);
                return new DropMenu(null, [new DropOption(DropKind.NewWorktree, $"New worktree for {src.Rev}",
                    $"git worktree add {Shown(snap, path)} {src.Rev}", null, src.Rev, src.Rev) { NewPath = path }]);
            }
            case GraphRefKind.RemoteBranch:
            {
                var name = GraphCheckout.LocalName(src.Rev);
                var path = Path(name);
                return new DropMenu(null, [new DropOption(DropKind.NewWorktree, $"New worktree for {name} (tracking {src.Rev})",
                    $"git worktree add --track -b {name} {Shown(snap, path)} {src.Rev}", null, src.Rev, name) { NewPath = path, Track = true }]);
            }
            default:
            {
                string? note = src.IsLocalBranch ? $"{src.Rev} is checked out in {WorktreeOf(snap, src.Rev)!.Name}, so the new worktree needs a branch of its own." : null;
                return new DropMenu(note, [new DropOption(DropKind.NewWorktree, $"New worktree on a new branch from {src.Label}…",
                    $"git worktree add -b <name> <path> {src.Label}", null, src.Rev, null) { NeedsName = true }]);
            }
        }
    }

    /// <summary>Where the graph puts new worktrees when the repo shows no layout of its own: beside the repo, as
    /// <c>..\repo--branch</c> (decision Q9 in docs/git-graph-plan.md).</summary>
    public const string SiblingTemplate = "{parent}/{repo}--{name}";

    /// <summary>The worktree path a new branch <paramref name="name"/> would get beside the main worktree at
    /// <paramref name="mainRoot"/>: the layout of the worktrees the user already made, else <see cref="SiblingTemplate"/>.
    /// An ignore-file hint isn't enough: Claude Code adds <c>.claude/worktrees</c> to ignore files itself.</summary>
    public static string NewWorktreePath(string mainRoot, WorktreeLayout? layout, string name) =>
        (layout is { Source: WorktreeLayoutSource.Detected or WorktreeLayoutSource.Chosen } ? layout
            : new WorktreeLayout(SiblingTemplate, WorktreeLayoutSource.Default)).Render(mainRoot, FolderName(name));

    // How a command shows a new worktree's path: relative to the repo, where git runs.
    private static string Shown(GraphSnapshot snap, string path)
    {
        var rel = System.IO.Path.GetRelativePath(snap.Root, path);
        return rel.Contains(' ') ? $"\"{rel}\"" : rel;
    }

    // feature/x → feature-x: one folder, not a nested pair.
    private static string FolderName(string branch) => branch.Replace('/', '-').Replace('\\', '-');

    // An operation that moves `branch`: in the worktree that has it, else the window's own worktree after switching.
    private static DropOption ChangeBranch(GraphSnapshot snap, DropKind kind, string label, string command, string branch,
        string rev, GraphWorktree? holder)
    {
        if (holder is not null)
            return new DropOption(kind, $"{label} in {holder.Name}", command, holder, rev, branch);
        var here = snap.Current;
        string switched = kind == DropKind.Rebase ? $"git rebase {rev} {branch}" : $"git switch {branch} && {command}";
        return new DropOption(kind, $"{label} (checks out {branch} in {here?.Name ?? "this worktree"} first)", switched, here, rev, branch,
            SwitchFirst: true);
    }

    private static DropOption FastForward(string branch, string rev, GraphWorktree? holder) => holder is not null
        ? new DropOption(DropKind.FastForward, $"Fast-forward {branch} to {rev} in {holder.Name}", $"git merge --ff-only {rev}", holder, rev, branch)
        : new DropOption(DropKind.FastForward, $"Fast-forward {branch} to {rev}", $"git fetch . {rev}:{branch}", null, rev, branch);

    private static DropOption Checkout(GraphWorktree wt, CheckoutAction action, string rev, string label) =>
        new(DropKind.Checkout, label, action switch
        {
            CheckoutAction.Track => $"git switch --track {rev}",
            CheckoutAction.Detach => $"git switch --detach {Short(rev)}",
            _ => $"git switch {rev}",
        }, wt, rev, null) { Checkout = action };

    private static GraphWorktree? WorktreeOf(GraphSnapshot snap, string branch) =>
        snap.Worktrees.FirstOrDefault(w => w.Branch == branch);

    /// <summary>
    /// Whether <paramref name="ancestor"/> is reachable from <paramref name="descendant"/>: true, false, or null when one
    /// of them isn't loaded (git would have to say). The loaded rows are a prefix of a topological order, so an
    /// ancestor's row is always below its descendant's, and nothing past the loaded rows can lead back into them: the
    /// walk only needs the rows between the two.
    /// </summary>
    public static bool? IsAncestor(GraphSnapshot snap, string ancestor, string descendant)
    {
        if (ancestor == descendant) return true;
        int target = snap.RowOf(ancestor), start = snap.RowOf(descendant);
        if (start < 0 || target < 0) return null;
        if (target < start) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal) { descendant };
        var stack = new Stack<string>();
        stack.Push(descendant);
        while (stack.Count > 0)
        {
            foreach (var p in snap.Commits[snap.RowOf(stack.Pop())].Parents)
            {
                if (p == ancestor) return true;
                int row = snap.RowOf(p);
                if (row >= 0 && row < target && seen.Add(p)) stack.Push(p);
            }
        }
        return false;
    }

    private static string Short(string rev) => rev.Length == 40 && rev.All(Uri.IsHexDigit) ? rev[..7] : rev;
}
