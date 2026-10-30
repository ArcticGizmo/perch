using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Data;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Drag to merge, rebase, fast-forward, cherry-pick, check out in another worktree or make a new worktree (CP12-CP14 in
// docs/git-graph-plan.md). Hand-rolled rather than OS drag and drop: the canvas or rail raises DragRequested past the
// threshold, the pointer is captured by the canvas for the rest of the gesture, and the window follows it with tunnel
// handlers, so it sees every move wherever the pointer is. What a drop offers is decided in Core (GraphDrop) and run by
// GitOps.RunDrop off the UI thread, reported in the toast with Undo.
internal sealed partial class GitTreeWindow
{
    private global::Avalonia.Controls.Canvas _dragLayer = null!;
    private Border _ghost = null!;
    private TextBlock _ghostLabel = null!, _ghostHint = null!;
    private DragSource? _dragSrc;
    private DropTarget? _dragTarget;
    private DropMenu? _dragMenu;
    private PointerEventArgs? _dragLast;
    private DispatcherTimer? _dragScroll;

    // Where new worktrees go for this repo (inferred from the existing ones) and the main worktree it renders against,
    // loaded off the UI thread per repo.
    private WorktreeLayout? _layout;
    private string? _mainRoot;

    // The session working in each worktree, by path, so a drop that writes there can warn first.
    private Dictionary<string, ClaudeSession> _sessionAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A new worktree's "Start a session there": the App opens a session window on the folder.</summary>
    public event Action<string>? StartSessionRequested;

    private Border _progressBar = null!;
    private TextBlock _progressText = null!;
    private Button _continueBtn = null!, _skipBtn = null!, _abortBtn = null!;
    private GitInProgress _inProgress;

    private void BuildDragUi()
    {
        _ghostLabel = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        _ghostHint = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        _ghost = new Border
        {
            IsVisible = false, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, OffsetY = 2, Color = Color.FromArgb(80, 0, 0, 0) }),
            Child = new StackPanel { Children = { _ghostLabel, _ghostHint } },
        };
        _dragLayer = new global::Avalonia.Controls.Canvas { IsHitTestVisible = false, ClipToBounds = true, Children = { _ghost } };

        AddHandler(PointerMovedEvent, OnDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        _progressText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _continueBtn = ProgressButton("Continue", (wt, op) => GitOps.Continue(wt, op));
        _skipBtn = ProgressButton("Skip commit", (wt, op) => GitOps.Skip(wt, op));
        _abortBtn = ProgressButton("Abort", (wt, op) => GitOps.Abort(wt, op));
        _progressBar = new Border
        {
            Padding = new Thickness(12, 7), IsVisible = false, BorderThickness = new Thickness(0, 1, 0, 1), [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { _abortBtn, _skipBtn, _continueBtn, _progressText } },
        };
    }

    private void ApplyDragPalette()
    {
        _ghost.Background = _pal.WindowBg;
        _ghost.BorderBrush = _pal.Border;
        _ghostLabel.Foreground = _pal.Title;
        _ghostHint.Foreground = _pal.Muted;
        _progressBar.Background = _pal.WindowBg;
        _progressBar.BorderBrush = _pal.Orange;
        _progressText.Foreground = _pal.Fg;
    }

    // ---- the gesture ----

    private void BeginDrag(DragSource src, IPointer pointer)
    {
        if (_snap is null) return;
        _dragSrc = src;
        _dragTarget = null;
        _dragMenu = null;
        pointer.Capture(_graph.Canvas);   // the rail row that started it may be rebuilt under the pointer
        _rail.SetDragging(true);
        _ghostLabel.Text = src.Ref is { } r ? r.Name : $"{src.Label}  {src.Commit!.Subject}";
        _ghostHint.Text = NoTargetHint;
        _ghost.IsVisible = true;
        _dragScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => DragAutoScroll());
        _dragScroll.Start();
    }

    private const string NoTargetHint = "Drop on a branch, a worktree or + New worktree. Esc cancels.";

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSrc is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }   // released elsewhere
        _dragLast = e;
        Track(e);
    }

    // Follows the pointer: the ghost, what's under it, and the hint for the first thing a drop there would do.
    private void Track(PointerEventArgs e)
    {
        var at = e.GetPosition(_dragLayer);
        global::Avalonia.Controls.Canvas.SetLeft(_ghost, Math.Min(at.X + 16, Math.Max(0, _dragLayer.Bounds.Width - _ghost.Bounds.Width - 4)));
        global::Avalonia.Controls.Canvas.SetTop(_ghost, at.Y + 14);

        var (target, railHit) = TargetUnder(e);
        _graph.Canvas.SetDropHighlight(railHit is null ? target : null);
        _rail.HighlightDrop(railHit);
        if (target == _dragTarget) return;
        _dragTarget = target;
        _dragMenu = target is not null && _snap is not null && _dragSrc is not null
            ? GraphDrop.Menu(_snap, _dragSrc, target, _layout, _mainRoot) : null;
        _ghostHint.Text = _dragMenu switch
        {
            null => NoTargetHint,
            { Options.Count: 0 } m => m.Note ?? "Nothing to do here.",
            var m => m.Options[0].Label + (m.Options.Count > 1 ? $"  ·  {m.Options.Count - 1} more on drop" : ""),
        };
    }

    // The graph canvas answers for itself; anywhere else, the rail control under the pointer carries a RailItem tag.
    private (DropTarget? Target, Control? RailHit) TargetUnder(PointerEventArgs e)
    {
        var pc = e.GetPosition(_graph.Canvas);
        if (new Rect(_graph.Canvas.Bounds.Size).Contains(pc)) return (_graph.Canvas.DropTargetAt(pc), null);
        for (var v = this.InputHitTest(e.GetPosition(this)) as Visual; v is not null; v = v.GetVisualParent())
            if (v is Control { Tag: Views.GraphRail.RailItem item } c)
                return item.Target is { } t ? (t, c) : (null, null);
        return (null, null);
    }

    private void DragAutoScroll()
    {
        if (_dragSrc is null || _dragLast is null) return;
        var pc = _dragLast.GetPosition(_graph.Canvas);
        if (pc.X < 0 || pc.X > _graph.Canvas.Bounds.Width) return;
        if (_graph.Canvas.AutoScroll(pc.Y)) Track(_dragLast);
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSrc is not { } src) return;
        var target = _dragTarget;
        var menu = _dragMenu;
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        if (target is null || menu is null) return;
        if (menu.Options.Count == 0)
        {
            if (menu.Note is { } note) ShowToast(note, "", null);
            return;
        }
        ShowDropMenu(src, menu);
    }

    private void EndDrag()
    {
        _dragSrc = null;
        _dragTarget = null;
        _dragMenu = null;
        _dragLast = null;
        _dragScroll?.Stop();
        _ghost.IsVisible = false;
        _graph.Canvas.SetDropHighlight(null);
        _rail.SetDragging(false);
    }

    private bool CancelDrag()
    {
        if (_dragSrc is null) return false;
        EndDrag();
        return true;
    }

    // ---- the drop ----

    // One item per choice, each naming where it runs and showing the git command under it. A session working where it
    // runs adds a warning line; an option that has to switch this worktree first waits for a clean tree.
    private void ShowDropMenu(DragSource src, DropMenu menu)
    {
        var items = new List<Control>();
        if (menu.Note is { } note) items.Add(Note(note));
        foreach (var where in menu.Options.Select(o => o.RunIn).OfType<GraphWorktree>().DistinctBy(w => w.Path))
            if (_sessionAt.TryGetValue(where.Path, out var s) && s.Status is SessionStatus.Running or SessionStatus.AwaitingInput)
                items.Add(Note($"A Claude session ({s.DisplayName}) is working in {where.Name}; this changes files under it"));
        int dirtyHere = _lastStatus?.Changes.Count ?? 0;
        foreach (var o in menu.Options)
        {
            bool blocked = o.SwitchFirst && dirtyHere > 0;
            var header = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = blocked ? $"{o.Label} · commit or stash this worktree's changes first" : o.Label },
                    // A long command keeps its tail (the path's folder name and the branch), per the path convention.
                    new TextBlock
                    {
                        Text = o.Command, FontFamily = Mono, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 1, 0, 0),
                        MaxWidth = 460, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
                    },
                },
            };
            var item = new MenuItem { Header = header, IsEnabled = !blocked };
            var option = o;
            item.Click += (_, _) => ChooseDrop(src, option);
            items.Add(item);
        }
        items.Add(Item("Cancel", () => { }));
        ShowMenu(_graph, atPointer: true, items.ToArray());
    }

    private void ChooseDrop(DragSource src, DropOption o)
    {
        if (_snap is not { } snap) return;
        if (o.Kind == DropKind.Checkout && o.RunIn is { } wt)
        {
            // A working session there was already warned about in the drop menu, so only the changes need asking about.
            int dirty = wt.IsCurrent ? _lastStatus?.Changes.Count ?? 0 : wt.Changes;
            if (dirty > 0)
            {
                AskAboutChanges(wt.Name, dirty, busy: false, CheckoutDest(o), choice => RunDrop(o, choice), _graph, atPointer: true);
                return;
            }
        }
        if (o.Kind == DropKind.NewWorktree && o.NeedsName)
        {
            OpenBranchBar($"New worktree on a new branch from {src.Label}", name =>
                RunDrop(o, newBranch: name, newPath: GraphDrop.NewWorktreePath(_mainRoot ?? snap.Root, _layout, name)));
            return;
        }
        RunDrop(o);
    }

    private static string CheckoutDest(DropOption o) => o.Checkout switch
    {
        CheckoutAction.Track => GraphCheckout.LocalName(o.Rev),
        CheckoutAction.Detach => Short(o.Rev),
        _ => o.Rev,
    };

    private void RunDrop(DropOption o, DirtyChoice dirty = DirtyChoice.None, string? newBranch = null, string? newPath = null)
    {
        if (_snap is not { } snap) return;
        var root = snap.Root;
        var path = newPath ?? o.NewPath;
        ShowToast($"{o.Label}…", o.Command, null, busy: true);
        SetGraphBusy($"{o.Label}…");
        System.Threading.Tasks.Task.Run(() => GitOps.RunDrop(root, o, dirty, newBranch, newPath)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!t.IsCompletedSuccessfully) { ClearGraphBusy(); return; }
                if (!IsVisible) return;
                ClearBusyOnNextLoad();
                var (r, undo) = t.Result;
                if (r.Ok && undo is not null)
                {
                    if (o.Kind == DropKind.NewWorktree && path is not null)
                        ShowToast($"Made worktree {System.IO.Path.GetFileName(path)} on {newBranch ?? o.Branch}", r.Command, () => RunDropUndo(undo),
                            extra: StartSessionRequested is null ? null : ("Start a session there", () => StartSessionRequested?.Invoke(path)));
                    else
                        ShowToast(Done(o), r.Command, () => RunDropUndo(undo));
                }
                else if (r.Conflicted && o.RunIn is { IsCurrent: false } there)
                {
                    ShowToast($"{Verb(o)} stopped on conflicts in {there.Name}.", r.Command, null, error: true,
                        extra: ($"Open {there.Name}", () => Retarget(there.Path, there.Name, null, isActive: false)));
                }
                else if (r.Conflicted)
                {
                    ShowToast($"{Verb(o)} stopped on conflicts: resolve them in Changes, stage them, then Continue.", r.Command, null, error: true);
                }
                else
                {
                    ShowToast(r.Message, r.Command, null, error: true);
                }
                RefreshStatus(preserveSelection: true);
            });
        });
    }

    private void RunDropUndo(Func<GitOpResult> undo)
    {
        ShowToast("Undoing…", "", null, busy: true);
        SetGraphBusy("Undoing…");
        System.Threading.Tasks.Task.Run(undo).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!t.IsCompletedSuccessfully) { ClearGraphBusy(); return; }
                if (!IsVisible) return;
                ClearBusyOnNextLoad();
                var r = t.Result;
                if (r.Ok) ShowToast("Undone", r.Command, null);
                else ShowToast(r.Message, r.Command, null, error: true);
                RefreshStatus(preserveSelection: true);
            });
        });
    }

    private static string Verb(DropOption o) => o.Kind switch
    {
        DropKind.Merge => "The merge",
        DropKind.Rebase => "The rebase",
        DropKind.CherryPick => "The cherry-pick",
        _ => "It",
    };

    // The menu label as a past-tense outcome: "Merge a into b in r" → "Merged a into b in r".
    private static string Done(DropOption o)
    {
        var label = o.Label;
        int paren = label.IndexOf(" (checks out", StringComparison.Ordinal);
        if (paren > 0) label = label[..paren];
        foreach (var (verb, past) in new[] { ("Merge ", "Merged "), ("Rebase ", "Rebased "), ("Cherry-pick ", "Cherry-picked "),
                     ("Fast-forward ", "Fast-forwarded "), ("Check out ", "Checked out ") })
            if (label.StartsWith(verb, StringComparison.Ordinal)) return past + label[verb.Length..];
        return label;
    }

    // ---- worktree card menu ----

    private void ShowWorktreeMenu(GraphWorktree wt, Control card)
    {
        var items = new List<Control>();
        if (!wt.IsCurrent)
            items.Add(Item($"Open {wt.Name} in this window", () => Retarget(wt.Path, wt.Name, null, isActive: false)));
        if (StartSessionRequested is not null)
            items.Add(Item($"Start a Claude session in {wt.Name}", () => StartSessionRequested?.Invoke(wt.Path)));
        // git itself refuses to remove the main worktree, with a message the toast shows.
        if (!wt.IsCurrent && _snap is { } snap && !string.Equals(wt.Path, _mainRoot, StringComparison.OrdinalIgnoreCase))
            items.Add(Item($"Remove {wt.Name}…", () => RemoveWorktree(snap.Root, wt)));
        if (_snap is { } s)
            items.Add(Item("Prune worktrees whose folders are gone", () => RunRepoOp(s.Root, "Pruning…", r => GitOps.WorktreePrune(r))));
        ShowMenu(card, atPointer: true, items.ToArray());
    }

    // A clean worktree goes; one with changes, or a session working in it, asks first (removing it loses those changes).
    private async void RemoveWorktree(string root, GraphWorktree wt)
    {
        bool working = _sessionAt.TryGetValue(wt.Path, out var s) && s.Status is SessionStatus.Running or SessionStatus.AwaitingInput;
        if (wt.Changes > 0 || working)
        {
            var why = wt.Changes > 0
                ? $"{wt.Name} has {wt.Changes} uncommitted change{(wt.Changes == 1 ? "" : "s")}, which removing it deletes for good."
                : $"A Claude session ({s!.DisplayName}) is working in {wt.Name}.";
            bool go = await ConfirmDialog.ShowAsync(this, "Remove worktree", $"{why} Remove {wt.Path} anyway? Its branch stays.",
                "Remove", "Keep it");
            if (!go || !IsVisible) return;
        }
        bool force = wt.Changes > 0;
        RunRepoOp(root, $"Removing {wt.Name}…", r => GitOps.WorktreeRemove(r, wt.Path, force));
    }

    private void RunRepoOp(string root, string busy, Func<string, GitOpResult> op)
    {
        ShowToast(busy, "", null, busy: true);
        System.Threading.Tasks.Task.Run(() => op(root)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                var r = t.Result;
                if (r.Ok) ShowToast("Done", r.Command, null);
                else ShowToast(r.Message, r.Command, null, error: true);
                RefreshStatus(preserveSelection: true);
            });
        });
    }

    // ---- in-progress banner ----

    // A merge, rebase or cherry-pick this worktree stopped in the middle of: what it is, how many files still conflict,
    // and Continue / Skip / Abort.
    private void UpdateInProgress(GitInProgress op)
    {
        _inProgress = op;
        _progressBar.IsVisible = op != GitInProgress.None;
        if (op == GitInProgress.None) return;
        int conflicted = _lastStatus?.Changes.Count(c => c.Staged == GitChangeKind.Unmerged || c.Unstaged == GitChangeKind.Unmerged) ?? 0;
        string what = op switch
        {
            GitInProgress.Merge => "A merge",
            GitInProgress.Rebase => "A rebase",
            GitInProgress.CherryPick => "A cherry-pick",
            _ => "A revert",
        };
        _progressText.Text = conflicted > 0
            ? $"{what} is in progress in {HereName()}: {conflicted} conflicted file{(conflicted == 1 ? "" : "s")}. Resolve and stage them in Changes, then Continue."
            : $"{what} is in progress in {HereName()}, with no conflicts left. Continue to finish it.";
        _skipBtn.IsVisible = op is GitInProgress.Rebase or GitInProgress.CherryPick;
    }

    private Button ProgressButton(string text, Func<string, GitInProgress, GitOpResult> op)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        b.Click += (_, _) =>
        {
            if (_cwd is not { } cwd || _inProgress == GitInProgress.None) return;
            var what = _inProgress;
            ShowToast($"{text}…", "", null, busy: true);
            System.Threading.Tasks.Task.Run(() => op(cwd, what)).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    var r = t.Result;
                    if (r.Ok) ShowToast(text == "Abort" ? "Aborted; back where it started" : "Done", r.Command, null);
                    else ShowToast(r.Conflicted ? "More conflicts: resolve and stage them, then Continue." : r.Message, r.Command, null, error: true);
                    RefreshStatus(preserveSelection: true);
                });
            });
        };
        return b;
    }

    // ---- headless render hooks ----

    /// <summary>For the headless render: the window position of the <paramref name="index"/>th local branch pill the graph
    /// painted that isn't this worktree's own branch, to drive a drag with synthetic pointer input.</summary>
    internal Point? PreviewPill(int index)
    {
        var current = _snap?.Current?.Branch;
        var pill = _graph.Canvas.PaintedPills.Where(p => p.Ref.Kind == GraphRefKind.LocalBranch && p.Ref.Name != current)
            .Skip(index).Select(p => (Rect?)p.Rect).FirstOrDefault();
        return pill is { } r ? _graph.Canvas.TranslatePoint(r.Center, this) : null;
    }

    /// <summary>For the headless render: where the rail's "+ New worktree" zone is (only while a drag is on).</summary>
    internal Point? PreviewNewWorktreeZone() =>
        this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Tag is Views.GraphRail.RailItem { NewWorktree: true }) is { } zone
            ? zone.TranslatePoint(new Point(zone.Bounds.Width / 2, zone.Bounds.Height / 2), this)
            : null;

    internal bool PreviewDragging => _dragSrc is not null;
    internal string PreviewGhostHint => _ghostHint.Text ?? "";

    // ---- per-repo context ----

    // The layout new worktrees follow, inferred from the existing ones (what PR worktrees use too).
    private void LoadLayout(string cwd)
    {
        _layout = null;
        _mainRoot = null;
        System.Threading.Tasks.Task.Run(() => PrWorktree.Inspect(GitCheckoutScanner.FindRoot(cwd) ?? cwd)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || t.Result is not { } ctx) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.Equals(cwd, _cwd, StringComparison.Ordinal)) return;
                _layout = ctx.Layout;
                _mainRoot = ctx.Set.Root;
            });
        });
    }
}
