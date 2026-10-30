using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.GitGraph;
using Shape = Avalonia.Controls.Shapes.Path;

namespace Perch.Avalonia.Windows;

// The graph card's toolbar, in three groups: Pull (with GitKraken's four choices on its ▾) and Push; Branch; Stash and
// Pop. They act on this window's worktree and report in the toast, with Undo where there's a safe one (a pull resets
// back, a stash pops back, a dropped stash is stored again); a push never forces, so it has none. Stash rows in the
// graph and the rail get their own menu here too.
internal sealed partial class GitTreeWindow
{
    // Stroke icons on a 14 px box, drawn because Inter has none of these glyphs.
    private const string PullIcon = "M7,1.5 V10 M3.5,6.5 L7,10 L10.5,6.5 M2,13 H12";
    private const string PushIcon = "M7,11 V2.5 M3.5,6 L7,2.5 L10.5,6 M2,13 H12";
    private const string BranchIcon = "M4,1.5 V12.5 M10,5 C10,8.5 4,7.5 4,11 M8.5,3.3 A1.5,1.5 0 1 0 11.5,3.3 A1.5,1.5 0 1 0 8.5,3.3";
    private const string StashIcon = "M1.5,7.5 V12.5 H12.5 V7.5 M7,1.5 V9 M4.5,6.5 L7,9 L9.5,6.5";
    private const string PopIcon = "M1.5,7.5 V12.5 H12.5 V7.5 M7,9.5 V1.5 M4.5,4 L7,1.5 L9.5,4";
    private const string ChevronIcon = "M1,1 L4.5,4.5 L8,1";

    private Button _pullBtn = null!, _pullMenuBtn = null!, _pushBtn = null!, _branchBtn = null!, _stashBtn = null!, _popBtn = null!;
    private Border _pullSplit = null!, _pullSplitDivider = null!;
    private readonly List<Button> _actionButtons = new();
    private readonly List<Border> _actionDividers = new();
    private readonly List<Shape> _actionIcons = new();
    private readonly Dictionary<Button, TextBlock> _actionLabels = new();
    private readonly HashSet<Button> _actionOff = new();

    private Control BuildActionBar()
    {
        _pullBtn = ActionButton("Pull", PullIcon, () => Pull(_settings.GitTreePullMode));
        _pullBtn.CornerRadius = new CornerRadius(6, 0, 0, 6);
        _pullBtn.VerticalAlignment = VerticalAlignment.Stretch;
        _pullMenuBtn = new Button
        {
            Content = ActionIcon(ChevronIcon, 9, 6), Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(0, 6, 6, 0),
            BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Stretch, Cursor = new Cursor(StandardCursorType.Hand),
        };
        _pullMenuBtn.Click += (_, _) => ShowPullMenu();
        ToolTip.SetTip(_pullMenuBtn, "Fetch, or pull another way");
        _pullBtn.BorderThickness = new Thickness(0);
        // Pull and its ▾ are one control: one rounded outline, split by a short inset rule rather than a full-height edge.
        _pullSplitDivider = new Border { Width = 1, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        _pullSplit = new Border
        {
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _pullBtn, _pullSplitDivider, _pullMenuBtn } },
        };

        _pushBtn = ActionButton("Push", PushIcon, Push);
        _branchBtn = ActionButton("Branch", BranchIcon, NewBranchHere);
        _stashBtn = ActionButton("Stash", StashIcon, () => Stash());
        _popBtn = ActionButton("Pop", PopIcon, Pop);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { _pullSplit, _pushBtn, ActionDivider(), _branchBtn, ActionDivider(), _stashBtn, _popBtn },
        };
        bar.SetValue(DockPanel.DockProperty, Dock.Right);
        return bar;
    }

    private Button ActionButton(string label, string icon, Action act)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ActionIcon(icon, 14, 14), text } },
            Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
        };
        // Dimmed rather than disabled when it can't run, so its tooltip can still say why.
        b.Click += (_, _) => { if (!_actionOff.Contains(b)) act(); };
        _actionButtons.Add(b);
        _actionLabels[b] = text;
        return b;
    }

    private Shape ActionIcon(string data, double w, double h)
    {
        var p = new Shape
        {
            Data = Geometry.Parse(data), Width = w, Height = h, StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
        };
        _actionIcons.Add(p);
        return p;
    }

    private Border ActionDivider()
    {
        var d = new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        _actionDividers.Add(d);
        return d;
    }

    // Outlines and rules use the card border rather than the separator: in the dark palette the separator is the
    // button fill itself, so it would vanish.
    private void ApplyActionPalette()
    {
        foreach (var b in _actionButtons)
        {
            b.Background = b == _pullBtn ? Brushes.Transparent : _pal.ButtonBg;
            b.BorderBrush = _pal.Border;
            b.Foreground = _pal.Fg;
        }
        _pullMenuBtn.Background = Brushes.Transparent;
        _pullSplit.Background = _pal.ButtonBg;
        _pullSplit.BorderBrush = _pal.Border;
        _pullSplitDivider.Background = _pal.Border;
        foreach (var d in _actionDividers) d.Background = _pal.Border;
        foreach (var i in _actionIcons) i.Stroke = _pal.Fg;
    }

    private void SetAction(Button b, bool on, string? label = null)
    {
        if (label is not null) _actionLabels[b].Text = label;
        if (on) _actionOff.Remove(b); else _actionOff.Add(b);
        b.Opacity = on ? 1 : 0.45;
        b.Cursor = new Cursor(on ? StandardCursorType.Hand : StandardCursorType.Arrow);
    }

    // Labels, tooltips and what can run, from the loaded snapshot and this worktree's status.
    private void UpdateActionBar()
    {
        if (_pullBtn is null) return;
        var here = _snap?.Current;
        var status = _lastStatus;
        string? branch = here?.Branch;
        int ahead = status?.Ahead ?? 0, behind = status?.Behind ?? 0, dirty = status?.Changes.Count ?? 0;
        bool hasUpstream = status?.Upstream is not null;
        int stashes = _snap?.Stashes.Count ?? 0;

        var mode = _settings.GitTreePullMode;
        SetAction(_pullBtn, here is not null && (mode == PullMode.FetchAll || (branch is not null && hasUpstream)),
            mode == PullMode.FetchAll ? "Fetch" : behind > 0 ? $"Pull ↓{behind}" : "Pull");
        ToolTip.SetTip(_pullBtn, PullTip(mode, branch, hasUpstream, status?.Upstream));
        SetAction(_pullMenuBtn, here is not null);

        SetAction(_pushBtn, branch is not null, ahead > 0 ? $"Push ↑{ahead}" : "Push");
        ToolTip.SetTip(_pushBtn, branch is null ? "HEAD is detached: there's no branch to push"
            : hasUpstream ? $"Push {branch} to {status!.Value.Upstream}  (git push)"
            : $"Publish {branch} and track it  (git push -u origin {branch})");

        SetAction(_branchBtn, here is not null);
        ToolTip.SetTip(_branchBtn, "Create a branch here and check it out; your changes stay where they are");

        SetAction(_stashBtn, dirty > 0);
        ToolTip.SetTip(_stashBtn, dirty > 0 ? $"Stash the {dirty} change{(dirty == 1 ? "" : "s")}, untracked files included  (git stash push -u)" : "Nothing to stash");

        SetAction(_popBtn, here is not null && stashes > 0, stashes > 1 ? $"Pop ({stashes})" : "Pop");
        ToolTip.SetTip(_popBtn, stashes > 0 ? "Apply the newest stash here and drop it  (git stash pop)" : "No stashes");
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
        if (_actionOff.Contains(_pullMenuBtn)) return;
        var current = _settings.GitTreePullMode;
        Control Choice(PullMode m, string label) => Item((m == current ? "✓  " : "     ") + label, () =>
        {
            _settings.GitTreePullMode = m;
            _settings.Save();
            UpdateActionBar();
            Pull(m);
        });
        ShowMenu(_pullSplit, atPointer: false,
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

    // This worktree's changes by default; another worktree's when stashed from its WIP row.
    private void Stash(GraphWorktree? where = null)
    {
        var wt = where ?? _snap?.Current;
        if (wt is null) return;
        int dirty = wt.IsCurrent ? _lastStatus?.Changes.Count ?? 0 : wt.Changes;
        string label = $"WIP on {wt.Branch ?? "detached HEAD"}";
        string changes = $"{dirty} change{(dirty == 1 ? "" : "s")}";
        RunHereAction("Stashing…", cover: true, cwd =>
        {
            var r = GitOps.StashPush(cwd, label);
            if (!r.Ok || GitOps.StashTop(cwd) is not { } made) return (r, null);
            return (r, () => GitOps.StashPop(cwd, made));
        }, _ => wt.IsCurrent ? $"Stashed {changes}" : $"Stashed {changes} in {wt.Name}", conflict: null,
            worktree: wt.IsCurrent ? null : wt.Path);
    }

    private void Pop() =>
        RunHereAction("Popping the stash…", cover: true, cwd => (GitOps.StashPopTop(cwd), null), _ => "Stash applied and dropped",
            conflict: StashConflict);

    private const string StashConflict = "The stash conflicted with your changes: resolve them in Changes. It stays in the stash list until you drop it.";

    // A branch at HEAD changes no files, so unlike checking out elsewhere there's nothing to ask about the changes.
    private void NewBranchHere()
    {
        if (_snap?.Current is not { } here) return;
        var plan = new CheckoutPlan(CheckoutAction.Detach, "HEAD", here.Head ?? "HEAD");
        OpenBranchBar($"New branch from {here.Branch ?? Short(here.Head ?? "HEAD")}", name => RunCheckout(plan, name, DirtyChoice.None));
    }

    // ---- stash rows ----

    // A stash row, its pill or its rail row: apply it here (keeping it), pop it here, or drop it.
    private void ShowStashMenu(GraphStash st, Control anchor)
    {
        string here = HereName();
        ShowMenu(anchor, atPointer: true,
            Note($"{st.Selector}: {st.Message}"),
            Item($"Apply to {here}, keeping the stash", () =>
                RunHereAction("Applying the stash…", cover: true, cwd => (GitOps.StashApply(cwd, st.Hash), null), _ => $"Applied {st.Selector}",
                    conflict: StashConflict)),
            Item($"Pop into {here} (apply, then drop it)", () =>
                RunHereAction("Popping the stash…", cover: true, cwd => (GitOps.StashPop(cwd, st.Hash), null), _ => $"Popped {st.Selector}",
                    conflict: StashConflict)),
            new Separator(),
            Item($"Drop {st.Selector}…", () => DropStash(st)),
            Item("Copy message", () => Clipboard?.SetTextAsync(st.Message)));
    }

    private async void DropStash(GraphStash st)
    {
        bool go = await ConfirmDialog.ShowAsync(this, "Drop stash",
            $"Drop {st.Selector} ({st.Message}) without applying it? Undo in the toast puts it back.", "Drop", "Keep it");
        if (!go || !IsVisible) return;
        RunHereAction("Dropping the stash…", cover: true, cwd =>
        {
            var r = GitOps.StashDrop(cwd, st.Hash);
            return (r, r.Ok ? () => GitOps.StashRestore(cwd, st.Hash, st.Message) : null);
        }, _ => $"Dropped {st.Selector}", conflict: null);
    }

    // Runs one action off the UI thread, in this window's worktree unless <paramref name="worktree"/> names another, and
    // reports it in the toast. A pull that stops on conflicts leaves the in-progress banner to the refresh.
    private void RunHereAction(string busy, bool cover, Func<string, (GitOpResult Result, Func<GitOpResult>? Undo)> op,
        Func<GitOpResult, string> done, string? conflict, string? worktree = null)
    {
        if (_cwd is not { } origin) return;
        string cwd = worktree ?? origin;
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
                if (string.Equals(origin, _cwd, StringComparison.Ordinal)) RefreshStatus(preserveSelection: true); else ClearGraphBusy();
            });
        });
    }
}
