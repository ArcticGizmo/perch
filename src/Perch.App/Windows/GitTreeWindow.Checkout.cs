using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Data.GitGraph;

namespace Perch.Avalonia.Windows;

// Double-click to check out (CP11 in docs/git-graph-plan.md): what a double-click means is decided in Core
// (GraphCheckout), the git work runs through GitOps.Checkout off the UI thread, and every outcome lands in the toast,
// with Undo when it worked. A dirty tree or a session working here asks first; neither blocks.
internal sealed partial class GitTreeWindow
{
    private const int ToastMs = 6000, ToastErrorMs = 10000;

    private Border _toast = null!;
    private TextBlock _toastMsg = null!, _toastCmd = null!;
    private Button _toastUndo = null!, _toastClose = null!, _toastExtra = null!;
    private Action? _toastUndoAction, _toastExtraAction;
    private DispatcherTimer? _toastTimer;
    private bool _toastError, _toastBusy;

    private Border _branchBar = null!;
    private TextBox _branchBox = null!;
    private TextBlock _branchLabel = null!;
    private Action<string>? _branchCreate;

    private void BuildCheckoutUi()
    {
        _toastMsg = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        _toastCmd = new TextBlock { FontFamily = Mono, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _toastUndo = new Button { Content = "Undo", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        _toastUndo.Click += (_, _) => { var undo = _toastUndoAction; HideToast(); undo?.Invoke(); };
        _toastClose = new Button
        {
            Content = "✕", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), [DockPanel.DockProperty] = Dock.Right,
        };
        _toastClose.Click += (_, _) => HideToast();
        _toastExtra = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        _toastExtra.Click += (_, _) => { var act = _toastExtraAction; HideToast(); act?.Invoke(); };
        _toast = new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 16, 16), Padding = new Thickness(14, 9, 10, 9), MaxWidth = 560,
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 3, Color = Color.FromArgb(90, 0, 0, 0) }),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { _toastClose, _toastUndo, _toastExtra, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _toastMsg, _toastCmd } } },
            },
        };
        // Reading the toast (or reaching for Undo) holds it open.
        _toast.PointerEntered += (_, _) => _toastTimer?.Stop();
        _toast.PointerExited += (_, _) => { if (_toast.IsVisible && !_toastBusy) _toastTimer?.Start(); };

        _branchLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), [DockPanel.DockProperty] = Dock.Left };
        _branchBox = new TextBox { PlaceholderText = "Branch name", MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        _branchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CreateBranchFromBar(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseBranchBar(); e.Handled = true; }
        };
        var create = new Button { Content = "Create and check out", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        create.Click += (_, _) => CreateBranchFromBar();
        var close = new Button { Content = "✕", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        close.Click += (_, _) => CloseBranchBar();
        _branchBar = new Border
        {
            Padding = new Thickness(10, 6), IsVisible = false, [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { close, create, _branchLabel, _branchBox } },
        };
    }

    private void ApplyCheckoutPalette()
    {
        _toast.Background = _pal.WindowBg;
        _toast.BorderBrush = _toastError ? _pal.Red : _pal.Border;
        _toastMsg.Foreground = _toastError ? _pal.Red : _pal.Title;
        _toastCmd.Foreground = _pal.Muted;
        _toastClose.Foreground = _pal.Muted;
        _branchBar.Background = _pal.WindowBg;
        _branchLabel.Foreground = _pal.Muted;
    }

    // ---- the gesture ----

    // A double-click resolved to a plan. Going to a branch nothing else holds runs at once; a branch another worktree
    // holds, or a place with no branch, asks what was meant first.
    private void BeginCheckout(CheckoutPlan plan, Control anchor)
    {
        if (_snap is null || _cwd is null) return;
        switch (plan.Action)
        {
            case CheckoutAction.None:
                return;
            case CheckoutAction.AlreadyHere:
                ShowToast($"{HereName()} is already on {Dest(plan, null)}.", "", null);
                return;
            case CheckoutAction.HeldElsewhere when plan.Holder is { } holder:
                ShowMenu(anchor, atPointer: true,
                    Note($"{plan.Target} is checked out in {holder.Name}, and a branch can be in only one worktree"),
                    Item($"Open {holder.Name} in this window", () => Retarget(holder.Path, holder.Name, null, isActive: false)),
                    Item($"Check out {Short(plan.Commit)} here, detached", () =>
                        ConfirmAndRun(new CheckoutPlan(CheckoutAction.Detach, plan.Commit, plan.Commit), null, anchor, atPointer: true)));
                return;
            case CheckoutAction.Detach:
                ShowMenu(anchor, atPointer: true,
                    Note(plan.Target == plan.Commit ? $"No branch points at {Short(plan.Commit)}" : $"{plan.Target} is a tag"),
                    Item($"Check out {Dest(plan, null)} here, detached", () => ConfirmAndRun(plan, null, anchor, atPointer: true)),
                    Item($"Create a branch at {Short(plan.Commit)}…", () =>
                        OpenBranchBar($"New branch at {Dest(plan, null)}", name => ConfirmAndRun(plan, name, _branchBar, atPointer: false))));
                return;
            default:
                ConfirmAndRun(plan, null, anchor, atPointer: true);
                return;
        }
    }

    // Uncommitted changes here, or a session working here, get a menu (stash / bring along / cancel, with the session
    // as a warning line); otherwise the checkout just runs.
    private void ConfirmAndRun(CheckoutPlan plan, string? newBranch, Control anchor, bool atPointer)
    {
        int dirty = _lastStatus?.Changes.Count ?? 0;
        if (dirty == 0 && !_isActive)
        {
            RunCheckout(plan, newBranch, DirtyChoice.None);
            return;
        }
        AskAboutChanges(HereName(), dirty, _isActive, Dest(plan, newBranch), choice => RunCheckout(plan, newBranch, choice), anchor, atPointer);
    }

    // The checkout-with-changes menu, for any worktree: stash / bring along / cancel, or "anyway" when only a working
    // session is the concern (it warns, never blocks).
    private void AskAboutChanges(string where, int dirty, bool busy, string dest, Action<DirtyChoice> run, Control anchor, bool atPointer)
    {
        string changes = $"{dirty} change{(dirty == 1 ? "" : "s")}";
        var items = new List<Control>();
        if (busy)
            items.Add(Note($"A Claude session is working in {where}; checking out under it can confuse its edits"));
        if (dirty > 0)
        {
            items.Add(Note($"{where} has {changes} not committed"));
            items.Add(Item($"Stash the {changes}, then check out {dest}", () => run(DirtyChoice.Stash)));
            items.Add(Item($"Bring the {changes} to {dest}", () => run(DirtyChoice.Carry)));
        }
        else
        {
            items.Add(Item($"Check out {dest} anyway", () => run(DirtyChoice.None)));
        }
        items.Add(Item("Cancel", () => { }));
        ShowMenu(anchor, atPointer, items.ToArray());
    }

    private void RunCheckout(CheckoutPlan plan, string? newBranch, DirtyChoice dirty)
    {
        if (_cwd is not { } cwd) return;
        string dest = Dest(plan, newBranch);
        int changes = _lastStatus?.Changes.Count ?? 0;
        ShowToast($"Checking out {dest}…", "", null, busy: true);
        System.Threading.Tasks.Task.Run(() => GitOps.Checkout(cwd, plan.Action, plan.Target, dirty, newBranch)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                var (r, undo) = t.Result;
                if (r.Ok && undo is not null)
                {
                    string what = plan.Action == CheckoutAction.Detach && newBranch is null
                        ? $"HEAD is detached at {dest}" : $"Checked out {dest}";
                    string extra = dirty switch
                    {
                        DirtyChoice.Stash => $"; {changes} change{(changes == 1 ? "" : "s")} stashed",
                        DirtyChoice.Carry => ", changes brought along",
                        _ => "",
                    };
                    ShowToast(what + extra, r.Command, () => RunUndo(cwd, undo));
                }
                else
                {
                    ShowToast(r.Message, r.Command, null, error: true);
                }
                if (string.Equals(cwd, _cwd, StringComparison.Ordinal)) RefreshStatus(preserveSelection: false);
            });
        });
    }

    private void RunUndo(string cwd, CheckoutUndo undo)
    {
        string back = undo.Before.Branch ?? Short(undo.Before.Hash);
        ShowToast($"Going back to {back}…", "", null, busy: true);
        System.Threading.Tasks.Task.Run(() => GitOps.Undo(cwd, undo)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                var r = t.Result;
                if (r.Ok) ShowToast($"Back on {back}{(undo.Stash is null ? "" : ", stash restored")}", r.Command, null);
                else ShowToast(r.Message, r.Command, null, error: true);
                if (string.Equals(cwd, _cwd, StringComparison.Ordinal)) RefreshStatus(preserveSelection: false);
            });
        });
    }

    // ---- new branch ----

    // The inline "name a new branch" bar, for a branch at a commit or a new worktree's branch.
    private void OpenBranchBar(string label, Action<string> create)
    {
        _branchCreate = create;
        _branchLabel.Text = label;
        _branchBar.IsVisible = true;
        _branchBox.Text = "";
        Dispatcher.UIThread.Post(() => _branchBox.Focus());
    }

    private void CloseBranchBar()
    {
        _branchBar.IsVisible = false;
        _branchCreate = null;
        _graph.Canvas.Focus();
    }

    private void CreateBranchFromBar()
    {
        string name = _branchBox.Text?.Trim() ?? "";
        if (name.Length == 0 || _branchCreate is not { } create) return;
        _branchBar.IsVisible = false;
        _branchCreate = null;
        create(name);
    }

    // ---- toast ----

    // Busy toasts stay until replaced; the rest hide on their own (errors linger longer), unless the pointer is on them.
    // <paramref name="extra"/> is a second button for a follow-up (open the worktree that has the conflicts, start a session).
    private void ShowToast(string message, string command, Action? undo, bool error = false, bool busy = false,
        (string Label, Action Act)? extra = null)
    {
        _toastExtra.Content = extra?.Label;
        _toastExtra.IsVisible = extra is not null;
        _toastExtraAction = extra?.Act;
        _toastError = error;
        _toastMsg.Text = message;
        _toastCmd.Text = command;
        _toastCmd.IsVisible = command.Length > 0;
        _toastBusy = busy;
        _toastUndoAction = undo;
        _toastUndo.IsVisible = undo is not null;
        _toastClose.IsVisible = !busy;
        ApplyCheckoutPalette();
        _toast.IsVisible = true;

        _toastTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(ToastMs), DispatcherPriority.Background, (_, _) => HideToast());
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(error ? ToastErrorMs : ToastMs);
        if (!busy && !_toast.IsPointerOver) _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        _toast.IsVisible = false;
        _toastUndoAction = null;
        _toastExtraAction = null;
    }

    /// <summary>For the headless render: the toast with Undo, the new-branch bar and the in-progress banner, which
    /// otherwise need a gesture or a conflict.</summary>
    internal void PreviewCheckoutChrome()
    {
        UpdateInProgress(GitInProgress.Merge);
        var at = _snap?.Current?.Head ?? "0000000";
        OpenBranchBar($"New branch at {Short(at)}", _ => { });
        ShowToast("Checked out feeds; 3 changes stashed", "git switch feeds", () => { });
    }

    // ---- helpers ----

    private void ShowMenu(Control anchor, bool atPointer, params Control[] items)
    {
        // Posted, so a menu opened from another menu's item isn't swallowed by the first one closing.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.ShowAt(anchor, showAtPointer: atPointer);
        });
    }

    private static MenuItem Item(string header, Action act)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => act();
        return m;
    }

    // A line that explains rather than acts: what the choices below are about.
    private static MenuItem Note(string text) => new() { Header = text, IsEnabled = false, FontStyle = FontStyle.Italic };

    // Where a plan lands, as the menus and toasts say it: the branch, "x (tracking origin/x)", the tag, or a short hash.
    private static string Dest(CheckoutPlan plan, string? newBranch)
    {
        if (newBranch is not null) return newBranch;
        return plan.Action switch
        {
            CheckoutAction.Track => $"{GraphCheckout.LocalName(plan.Target)} (tracking {plan.Target})",
            _ when plan.Target == plan.Commit => Short(plan.Commit),
            _ => plan.Target,
        };
    }

    private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;

    private string HereName() => _snap?.Current?.Name ?? System.IO.Path.GetFileName(_cwd ?? "") ?? "This worktree";
}
