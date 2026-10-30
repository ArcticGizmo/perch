using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// The graph card's toolbar: Pull (with GitKraken's four choices on its ▾), Push, Branch, Stash and Pop, all acting on
// this window's worktree. Each reports in the toast, with Undo where there's a safe one (a pull resets back, a stash
// pops back); a push never forces, so it has none.
internal sealed partial class GitTreeWindow
{
    private Button _pullBtn = null!, _pullMenuBtn = null!, _pushBtn = null!, _branchBtn = null!, _stashBtn = null!, _popBtn = null!;
    private readonly List<Button> _actionButtons = new();

    private Control BuildActionBar()
    {
        _pullBtn = ActionButton("Pull", () => Pull(_settings.GitTreePullMode));
        _pullMenuBtn = ActionButton("▾", ShowPullMenu);
        _pullMenuBtn.Padding = new Thickness(7, 6);
        ToolTip.SetTip(_pullMenuBtn, "Fetch, or pull another way");
        _pushBtn = ActionButton("Push", Push);
        _branchBtn = ActionButton("Branch", NewBranchHere);
        _stashBtn = ActionButton("Stash", Stash);
        _popBtn = ActionButton("Pop", Pop);
        var bar = Segment(_pullBtn, _pullMenuBtn, _pushBtn, _branchBtn, _stashBtn, _popBtn);
        bar.SetValue(DockPanel.DockProperty, Dock.Right);
        return bar;
    }

    private Button ActionButton(string label, Action act)
    {
        var b = SegButton(label);
        b.Padding = new Thickness(11, 6);
        b.Click += (_, _) => act();
        _actionButtons.Add(b);
        return b;
    }

    // Labels, tooltips and what's enabled, from the loaded snapshot and this worktree's status.
    private void UpdateActionBar()
    {
        if (_pullBtn is null) return;
        var here = _snap?.Current;
        var status = _lastStatus;
        string? branch = here?.Branch;
        int ahead = status?.Ahead ?? 0, behind = status?.Behind ?? 0, dirty = status?.Changes.Count ?? 0;
        bool hasUpstream = status?.Upstream is not null;
        int stashes = _snap?.Refs.Count(r => r.Kind == GraphRefKind.Stash) ?? 0;

        var mode = _settings.GitTreePullMode;
        _pullBtn.Content = behind > 0 && mode != PullMode.FetchAll ? $"Pull ↓{behind}" : mode == PullMode.FetchAll ? "Fetch" : "Pull";
        _pullBtn.IsEnabled = here is not null && (mode == PullMode.FetchAll || (branch is not null && hasUpstream));
        ToolTip.SetTip(_pullBtn, PullTip(mode, branch, hasUpstream, status?.Upstream));
        _pullMenuBtn.IsEnabled = here is not null;

        _pushBtn.Content = ahead > 0 ? $"Push ↑{ahead}" : "Push";
        _pushBtn.IsEnabled = branch is not null;
        ToolTip.SetTip(_pushBtn, branch is null ? "HEAD is detached: there's no branch to push"
            : hasUpstream ? $"Push {branch} to {status!.Value.Upstream}  (git push)"
            : $"Publish {branch} and track it  (git push -u origin {branch})");

        _branchBtn.IsEnabled = here is not null;
        ToolTip.SetTip(_branchBtn, "Create a branch here and check it out; your changes stay where they are");

        _stashBtn.IsEnabled = dirty > 0;
        ToolTip.SetTip(_stashBtn, dirty > 0 ? $"Stash the {dirty} change{(dirty == 1 ? "" : "s")}, untracked files included  (git stash push -u)" : "Nothing to stash");

        _popBtn.Content = stashes > 1 ? $"Pop ({stashes})" : "Pop";
        _popBtn.IsEnabled = here is not null && stashes > 0;
        ToolTip.SetTip(_popBtn, stashes > 0 ? "Apply the newest stash here and drop it  (git stash pop)" : "No stashes");

        foreach (var b in _actionButtons) b.Opacity = b.IsEnabled ? 1 : 0.45;
    }

    private static string PullTip(PullMode mode, string? branch, bool hasUpstream, string? upstream) => mode switch
    {
        PullMode.FetchAll => "Fetch every remote and prune  (git fetch --all --prune)",
        _ when branch is null => "HEAD is detached: there's nothing to pull into",
        _ when !hasUpstream => $"{branch} doesn't track a remote branch yet: Push publishes it",
        PullMode.Merge => $"Pull {upstream}, merging if it has diverged  (git pull --no-rebase)",
        PullMode.Rebase => $"Pull {upstream}, rebasing your commits on top  (git pull --rebase)",
        _ => $"Pull {upstream} if it only moved ahead  (git pull --ff-only)",
    };

    // The Pull button's ▾: pick a way to pull; the choice runs now and becomes what the button does.
    private void ShowPullMenu()
    {
        var current = _settings.GitTreePullMode;
        Control Choice(PullMode m, string label) => Item((m == current ? "✓  " : "     ") + label, () =>
        {
            _settings.GitTreePullMode = m;
            _settings.Save();
            UpdateActionBar();
            Pull(m);
        });
        ShowMenu(_pullMenuBtn, atPointer: false,
            Choice(PullMode.FetchAll, "Fetch all"),
            Choice(PullMode.FastForwardOnly, "Pull (fast-forward only)"),
            Choice(PullMode.Merge, "Pull (merge if diverged)"),
            Choice(PullMode.Rebase, "Pull (rebase)"));
    }

    private void Pull(PullMode mode)
    {
        bool fetch = mode == PullMode.FetchAll;
        RunHereAction(fetch ? "Fetching…" : "Pulling…", cover: !fetch, cwd => GitOps.Pull(cwd, mode),
            r => fetch ? "Fetched" : r.Message.Contains("up to date", StringComparison.OrdinalIgnoreCase) ? "Already up to date" : "Pulled",
            conflict: $"The pull's {(mode == PullMode.Rebase ? "rebase" : "merge")} stopped on conflicts: resolve them in Changes, stage them, then Continue.");
    }

    private void Push() =>
        RunHereAction("Pushing…", cover: false, cwd => (GitOps.Push(cwd), null), _ => "Pushed", conflict: null);

    private void Stash()
    {
        int dirty = _lastStatus?.Changes.Count ?? 0;
        string label = $"WIP on {_snap?.Current?.Branch ?? "detached HEAD"}";
        RunHereAction("Stashing…", cover: true, cwd =>
        {
            var r = GitOps.StashPush(cwd, label);
            if (!r.Ok || GitOps.StashTop(cwd) is not { } made) return (r, null);
            return (r, () => GitOps.StashPop(cwd, made));
        }, _ => $"Stashed {dirty} change{(dirty == 1 ? "" : "s")}", conflict: null);
    }

    private void Pop() =>
        RunHereAction("Popping the stash…", cover: true, cwd => (GitOps.StashPopTop(cwd), null), _ => "Stash applied and dropped",
            conflict: "The stash conflicted with your changes: resolve them in Changes. It stays in the stash list until you drop it.");

    // A branch at HEAD changes no files, so unlike checking out elsewhere there's nothing to ask about the changes.
    private void NewBranchHere()
    {
        if (_snap?.Current is not { } here) return;
        var plan = new CheckoutPlan(CheckoutAction.Detach, "HEAD", here.Head ?? "HEAD");
        OpenBranchBar($"New branch from {here.Branch ?? Short(here.Head ?? "HEAD")}", name => RunCheckout(plan, name, DirtyChoice.None));
    }

    // Runs one toolbar action in this worktree off the UI thread and reports it in the toast. A pull that stops on
    // conflicts leaves the in-progress banner to the refresh.
    private void RunHereAction(string busy, bool cover, Func<string, (GitOpResult Result, Func<GitOpResult>? Undo)> op,
        Func<GitOpResult, string> done, string? conflict)
    {
        if (_cwd is not { } cwd) return;
        ShowToast(busy, "", null, busy: true);
        if (cover) SetGraphBusy(busy);
        System.Threading.Tasks.Task.Run(() => op(cwd)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!t.IsCompletedSuccessfully) { ClearGraphBusy(); HideToast(); return; }
                if (!IsVisible) return;
                ClearBusyOnNextLoad();
                var (r, undo) = t.Result;
                if (r.Ok)
                    ShowToast(done(r), r.Command, undo is null ? null : () => RunDropUndo(undo));
                else if (r.Conflicted && conflict is not null)
                    ShowToast(conflict, r.Command, null, error: true);
                else
                    ShowToast(r.Message, r.Command, null, error: true);
                if (string.Equals(cwd, _cwd, StringComparison.Ordinal)) RefreshStatus(preserveSelection: true); else ClearGraphBusy();
            });
        });
    }
}
