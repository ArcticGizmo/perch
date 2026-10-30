using Avalonia.Controls;
using Avalonia.Input.Platform;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Right-click menus on the graph: a branch or tag (pill or rail row) and a commit row. Branching off a commit lives
// here rather than on double-click, which is kept for a commit detail view later.
internal sealed partial class GitTreeWindow
{
    private void ShowRefMenu(GraphRef r) => ShowRefMenu(r, _graph.Canvas);

    private void ShowRefMenu(GraphRef r, Control anchor)
    {
        if (_snap is not { } snap) return;
        bool branch = r.Kind is GraphRefKind.LocalBranch or GraphRefKind.RemoteBranch;
        var items = new List<Control>();
        if (branch || r.Kind == GraphRefKind.Tag)
        {
            items.Add(Item($"Check out {r.Name}", () => BeginCheckout(GraphCheckout.ForRef(snap, r), anchor)));
            items.Add(Item(branch ? $"Create a worktree for {r.Name}" : $"Create a worktree at {r.Name}…", () => NewWorktreeFrom(DragSource.Of(r))));
        }
        if (branch)
            items.Add(WhyNotHide(r) is { Length: > 0 } why ? Note($"Can't hide: {why}") : Item($"Hide {r.Name} from the graph", () => ToggleHidden(r, true)));
        if (r.Kind == GraphRefKind.LocalBranch)
        {
            items.Add(new Separator());
            items.Add(snap.Worktrees.FirstOrDefault(w => w.Branch == r.Name) is { } holder
                ? Note($"Can't delete: checked out in {holder.Name}")
                : Item($"Delete {r.Name}…", () => DeleteBranch(r)));
        }
        items.Add(Item("Copy name", () => Clipboard?.SetTextAsync(r.Name)));
        ShowMenu(anchor, atPointer: true, items.ToArray());
    }

    // A commit row: its branches to check out, a detached checkout, a branch or worktree starting there, and copying.
    // Another worktree's WIP row offers to open that worktree.
    private void ShowRowMenu(int row)
    {
        if (_snap is not { } snap || row < 0 || row >= snap.Commits.Count) return;
        var c = snap.Commits[row];
        var items = new List<Control>();
        if (c.IsWorkInProgress)
        {
            if (snap.WipAt(row) is not { IsCurrent: false } wt) return;
            items.Add(Item($"Open {wt.Name} in this window", () => Retarget(wt.Path, wt.Name, null, isActive: false)));
        }
        else
        {
            foreach (var b in snap.RefsAt(c.Hash).Where(x => x.Kind == GraphRefKind.LocalBranch).Take(4))
                items.Add(Item($"Check out {b.Name}", () => BeginCheckout(GraphCheckout.ForRef(snap, b), _graph.Canvas)));
            var detach = new CheckoutPlan(CheckoutAction.Detach, c.Hash, c.Hash);
            items.Add(Item($"Check out {c.ShortHash} (detached)", () => ConfirmAndRun(detach, null, _graph.Canvas, atPointer: true)));
            items.Add(Item($"Create a branch at {c.ShortHash}…", () =>
                OpenBranchBar($"New branch at {c.ShortHash}", name => ConfirmAndRun(detach, name, _branchBar, atPointer: false))));
            items.Add(Item($"Create a worktree at {c.ShortHash}…", () => NewWorktreeFrom(DragSource.Of(c))));
            items.Add(new Separator());
            items.Add(Item("Copy commit hash", () => Clipboard?.SetTextAsync(c.Hash)));
            items.Add(Item("Copy message", () => Clipboard?.SetTextAsync(c.Subject)));
        }
        ShowMenu(_graph.Canvas, atPointer: true, items.ToArray());
    }

    // The same thing as dropping it on "+ New worktree": a free branch goes straight in, anything else asks for a name.
    private void NewWorktreeFrom(DragSource src)
    {
        if (_snap is not { } snap) return;
        var menu = GraphDrop.Menu(snap, src, DropTarget.NewWorktreeZone, _layout, _mainRoot);
        if (menu.Options.FirstOrDefault() is { } option) ChooseDrop(src, option);
        else if (menu.Note is { } note) ShowToast(note, "", null);
    }

    // `git branch -d`; a branch with unmerged commits asks before `-D`. Undo puts the branch back at its commit.
    private async void DeleteBranch(GraphRef r)
    {
        if (_snap is not { } snap) return;
        var (root, name, hash) = (snap.Root, r.Name, r.Target);
        ShowToast($"Deleting {name}…", "", null, busy: true);
        var result = await System.Threading.Tasks.Task.Run(() => GitOps.DeleteBranch(root, name, force: false));
        if (!IsVisible) return;
        if (GitOps.IsUnmerged(result))
        {
            HideToast();
            bool go = await ConfirmDialog.ShowAsync(this, "Delete branch",
                $"{name} has commits that aren't merged into another branch. Delete it anyway? It points at {Short(hash)}, and Undo brings it back.",
                "Delete", "Keep it");
            if (!go || !IsVisible) return;
            result = await System.Threading.Tasks.Task.Run(() => GitOps.DeleteBranch(root, name, force: true));
            if (!IsVisible) return;
        }
        if (result.Ok)
            ShowToast($"Deleted {name}", result.Command, () => RunRepoOp(root, $"Restoring {name}…", repo => GitOps.CreateBranch(repo, name, hash, checkout: false)));
        else
            ShowToast(result.Message, result.Command, null, error: true);
        RefreshStatus(preserveSelection: true);
    }
}
