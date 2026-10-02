using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;
using Perch.Data.Roost;
using FlyoutBase = global::Avalonia.Controls.Primitives.FlyoutBase;
using PlacementMode = global::Avalonia.Controls.PlacementMode;
using ScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Roost (docs/roost-plan.md, docs/roost-tabs-plan.md): a tmux-style view of every live session — a rail on the
/// left (grouped by status, or A–Z) and, on the right, <b>tabs</b>, each a painted <see cref="RoostGridLayout"/> with a
/// <see cref="SessionPane"/> per region. The fixed first tab, Focus, is one full region for sessions in no tab.
///
/// <para>Placement is entirely the user's (D2): drag a rail row or a pane header onto a region, or click an empty
/// region to pick a session; a rail click goes to the session's tab, or shows it in Focus. Nothing moves by itself.
/// The tabs and the session list are app-owned (<see cref="RoostTabSet"/>, <see cref="RoostRoster"/>), so they survive
/// closing the window; this window owns only the per-pane feeds (<see cref="RoostFeed"/>), which live while it's open.</para>
///
/// <para>Every tab's panes share one <see cref="RoostTilePanel"/>, drawn in the active tab's shape: a pane in another
/// tab is a hidden host, so switching tabs (or moving a pane between them) is a visibility flip, never a re-parent —
/// a pane keeps its focus, scroll and measured thread. Hidden hosts stay warm up to <see cref="WarmLimit"/>; past
/// that the oldest is parked (no materialised thread).</para>
///
/// <para>Split by area: this file holds the window's build, its public surface, the layout pass, keys and the pulse;
/// <c>.Tabs</c> the tab strip and painter; <c>.Rail</c> the rail, its footer and the bottom-bar pills;
/// <c>.Placement</c> the empty-region picker and drag-to-place; <c>.RenderHooks</c> the HeadlessRenderer hooks.</para>
/// </summary>
internal sealed partial class RoostWindow : Window
{
    private const double StagePad = 14, CellGap = 14;

    private readonly RoostRoster _roster;
    private readonly RoostTabSet _tabs;
    private readonly RoostLayoutLibrary _layouts;
    // The layout painter, while a tab's layout is being edited (it covers the stage), or null.
    private RoostLayoutPainter? _painter;
    private readonly Func<RoostPane, RoostFeed?> _feedFactory;
    private readonly SessionPalette _p;

    private readonly Dictionary<string, RoostFeed?> _feeds = new(StringComparer.Ordinal);
    // One view per session (D1: a session is in one place, so a move within or between tabs keeps its view).
    // Lifting D1 would make this one view per placement, the feed shared.
    private readonly Dictionary<string, SessionPane> _views = new(StringComparer.Ordinal);
    // The panes on stage right now: the active tab's (just the zoomed one while zoomed).
    private readonly HashSet<string> _placed = new(StringComparer.Ordinal);
    // Sessions that have been in a tab (or Focus) since this window opened: a rail row without one reads "new".
    private readonly HashSet<string> _everPlaced = new(StringComparer.Ordinal);

    private readonly StackPanel _tabStrip;
    private readonly StackPanel _rail;
    private readonly Panel _stage;
    private readonly RoostTilePanel _grid;
    // One border per region of the active tab, at its slot: the empty-region placeholder, and what a drag
    // hit-tests against (a pane's host sits on top of an occupied one).
    private readonly RegionCell[] _cells = new RegionCell[RoostGridLayout.MaxRegions];
    // Every refresh re-sets the empty cells' cursor: one shared instance, so an unchanged one raises no change.
    private static Cursor? _handCursor;
    private static Cursor HandCursor => _handCursor ??= new Cursor(StandardCursorType.Hand);
    private readonly Dictionary<string, Border> _paneHosts = new(StringComparer.Ordinal);
    private const int WarmLimit = 8;
    private readonly List<string> _warm = [];
    // The active tab's regions in slot order (slot i = region _slotRegions[i]), as last laid out.
    private IReadOnlyList<int> _slotRegions = [];

    private readonly Border _unplacedPill, _elsewherePill;
    private readonly TextBlock _unplacedText, _elsewhereText;
    private readonly Ellipse _unplacedDot, _elsewhereDot;
    private readonly Border _hiddenRow;
    private readonly TextBlock _hiddenText;

    // Drag a pane by its header (or a rail row) onto a region: the ghost chip and the drop mark ride an overlay.
    private readonly Canvas _overlay;
    private readonly Border _dropMark;
    // The press that may become a drag: a session (rail row / pane header) or, with IsTab, a tab header. Source is
    // the control holding the pointer — the root, once it's a drag.
    private readonly record struct Press(string Id, bool IsTab, Point Start, Control Source);
    private const double DragThreshold = 6;
    private Press? _press;
    private Border? _ghost;
    private int _dropSlot = -1;
    private string? _dropTab;
    private readonly Panel _root;
    // A session drag resting on another tab's header switches to that tab, so it can be dropped on a region there.
    private const int HoverSwitchMs = 550;
    private readonly DispatcherTimer _hoverTimer;
    private string? _hoverTab;

    private bool DragOwnsRoot => ReferenceEquals(_press?.Source, _root);

    // The session / tab being pressed or dragged, if that's what it is.
    private string? PressedSession => _press is { IsTab: false } p ? p.Id : null;
    private string? PressedTab => _press is { IsTab: true } p ? p.Id : null;

    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _clockTimer;

    private RoostRailSort _railSort;
    private readonly Dictionary<RoostRailSort, Border> _sortSegments = new();
    private string? _focused;
    // The user put focus on _focused (a click in the pane, the rail, Alt+N, a drop, zoom), rather than it falling
    // there (window open, a tab switch, the focused pane leaving). Bare Enter / Esc answer a permission only then.
    private bool _focusChosen;
    // "+ New session" was clicked: the first new Perch pane to appear before the deadline is shown — in the region
    // it was started from (the empty-region picker), else Focus.
    private HashSet<string>? _keysAtNewSession;
    private DateTime _newSessionUntil;
    private RoostPlacement? _newSessionTarget;

    public RoostWindow(RoostRoster roster, RoostTabSet tabs, Func<RoostPane, RoostFeed?> feedFactory,
        SessionPalette? palette = null, RoostRailSort railSort = RoostRailSort.Status, RoostLayoutLibrary? layouts = null)
    {
        _roster = roster;
        _tabs = tabs;
        _layouts = layouts ?? new RoostLayoutLibrary();
        _feedFactory = feedFactory;
        _p = palette ?? SessionPalette.Current;
        _railSort = RoostRoster.Normalize(railSort);

        Title = "Roost";
        Width = 1280;
        Height = 800;
        MinWidth = 760;
        MinHeight = 480;
        Background = _p.Ground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Title bar: name ········ + New session ──
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                RoostGlyph(_p.Brand, 18),
                new TextBlock { Text = "Roost", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = _p.Title, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var newSession = BarButton("+ New session", () => StartNewSession(null));
        newSession[DockPanel.DockProperty] = Dock.Right;
        var bar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10), [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel { LastChildFill = true, Children = { newSession, title } },
        };

        // ── Rail: the sort toggle, the sessions, then "N hidden" and the keys cheat-sheet ──
        _rail = new StackPanel { Spacing = 14, Margin = new Thickness(8, 4, 8, 12) };
        (_hiddenRow, _hiddenText) = RailFooterRow("", "Closed panes — click to reopen");
        _hiddenRow.OnLeftClick(() => ShowHiddenMenu(_hiddenRow));
        var (keysRow, _) = RailFooterRow("⌨  Keys", "Every Roost shortcut");
        keysRow.OnLeftClick(() => ShowKeys(keysRow));
        var railFooter = new Border
        {
            BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6),
            [DockPanel.DockProperty] = Dock.Bottom,
            Child = new StackPanel { Spacing = 2, Children = { _hiddenRow, keysRow } },
        };
        var railHost = new Border
        {
            Width = 216, Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    RailSortToggle(),
                    railFooter,
                    new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rail },
                },
            },
        };

        // ── Tab strip (over the stage only) ──
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom };
        var stripHost = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(StagePad - 4, 6, StagePad, 0), [DockPanel.DockProperty] = Dock.Top,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _tabStrip,
            },
        };

        // ── Stage: region cells, then the pane hosts on top ──
        // Each cell carries half a gap on every side, so the grid's own margin is the pad less that half.
        _grid = new RoostTilePanel { Margin = new Thickness(StagePad - CellGap / 2) };
        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i] = RegionCell.Create(_p, CellGap / 2);
            int slot = i;
            _cells[i].Host.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
                if (slot < _slotRegions.Count && _tabs.Active.At(_slotRegions[slot]) is null) ShowPicker(slot);
            };
            _grid.Children.Add(_cells[i].Host);
        }
        _stage = new Panel { Background = _p.Ground, Children = { _grid } };

        // ── Bottom bar: key hints · the "not in a tab" / "other tabs" pills ──
        (_unplacedPill, _unplacedText, _unplacedDot) = Pill();
        _unplacedPill[ToolTip.TipProperty] = "Sessions in no tab — click to focus the most urgent";
        _unplacedPill.OnLeftClick(() =>
        {
            var unplaced = _tabs.Unplaced(_roster.Panes).Where(p => !p.Ended).Select(p => p.Key);
            if (RoostTabStatus.MostUrgent(unplaced, _roster.Panes) is { } key) FocusPane(key);
        });
        (_elsewherePill, _elsewhereText, _elsewhereDot) = Pill();
        _elsewherePill[ToolTip.TipProperty] = "Waiting in another tab — click to go there";
        _elsewherePill.OnLeftClick(() =>
        {
            if (RoostTabStatus.MostUrgent(_tabs.ElsewhereKeys(), _roster.Panes) is { } key) FocusPane(key);
        });
        var hints = new TextBlock
        {
            Text = RoostKeys.HintLine + "  ·  drag to place  ·  ⌨ more",
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Every Roost shortcut",
        };
        hints.OnLeftClick(() => ShowKeys(hints));
        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { _elsewherePill, _unplacedPill },
            [DockPanel.DockProperty] = Dock.Right,
        };
        var bottomBar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 5), MinHeight = 36, [DockPanel.DockProperty] = Dock.Bottom,
            Child = new DockPanel { LastChildFill = true, Children = { pills, hints } },
        };
        var stageColumn = new DockPanel { LastChildFill = true, Children = { stripHost, bottomBar, _stage } };

        _dropMark = new Border
        {
            BorderBrush = _p.Brand, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(12),
            Background = _p.BrandWash, Opacity = 0.7, IsVisible = false,
        };
        _overlay = new Canvas { IsHitTestVisible = false, Children = { _dropMark } };
        _root = new Panel
        {
            Children = { new DockPanel { LastChildFill = true, Children = { bar, railHost, stageColumn } }, _overlay },
        };
        Content = _root;
        // A drag under way holds the pointer here, not on the row or header it started from: a hover-switch to
        // another tab hides that header, and a hidden control can't keep the capture.
        _root.PointerMoved += (_, e) => { if (DragOwnsRoot) MoveGhost(e.GetPosition(_overlay)); };
        _root.PointerReleased += (_, e) =>
        {
            if (!DragOwnsRoot) return;
            e.Handled = true;
            EndDrag(drop: true);
            e.Pointer.Capture(null);
        };
        _root.PointerCaptureLost += (_, _) => { if (DragOwnsRoot) EndDrag(drop: false); };
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverSwitchMs) };
        _hoverTimer.Tick += (_, _) => HoverSwitch();

        // Window chords tunnel (so a focused thread can't swallow them); Enter / Esc bubble, so a focused control
        // takes them first.
        AddHandler(KeyDownEvent, OnChordKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnPageKeyDown;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pulseTimer.Tick += (_, _) => PulseFrame();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockTick();
        _clockTimer.Start();

        Closed += (_, _) =>
        {
            _pulseTimer.Stop();
            _clockTimer.Stop();
            _hoverTimer.Stop();
            foreach (var v in _views.Values) v.Park();
            foreach (var f in _feeds.Values) f?.Dispose();
            _feeds.Clear();
        };

        foreach (var tab in _tabs.All) foreach (var k in tab.Cells.Values) _everPlaced.Add(k);
        Refresh();
    }

    /// <summary>"+ New session" in the title bar (or an empty region's picker).</summary>
    public event Action? NewSessionRequested;

    /// <summary>A pane asked to open its session (the Perch window, or focus the terminal).</summary>
    public event Action<ClaudeSession>? OpenSessionRequested;

    /// <summary>A done-review pane was focused — acknowledge it (by pid), as clicking its overlay row does.</summary>
    public event Action<string>? AcknowledgeRequested;

    /// <summary>The rail's sort toggle moved (persist it).</summary>
    public event Action<RoostRailSort>? RailSortChanged;

    /// <summary>A permission card in a Perch pane was answered: (session id, item, allow, switch mode).</summary>
    public event Action<string, PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>Esc on a focused Perch pane with a turn running: interrupt it (session id).</summary>
    public event Action<string>? InterruptRequested;

    /// <summary>A Perch pane's composer sent a reply: (session id, text).</summary>
    public event Action<string, string>? PromptSubmitted;

    /// <summary>The set of panes on screen changed (a tab switch, a placement, a zoom, a scan) — a toast the Roost
    /// swallowed as "seen" may now be owed.</summary>
    public event Action? PlacementChanged;

    /// <summary>"Take over in Perch" on a terminal pane (the app confirms before stopping anything).</summary>
    public event Action<ClaudeSession>? TakeOverRequested;

    /// <summary>Which sessions offer "Take over in Perch" — the app supplies the overlay's Elevate rule. Null =
    /// none.</summary>
    public Func<ClaudeSession, bool>? CanTakeOver { get; set; }

    /// <summary>A question card in a Perch pane was answered: (session id, item, answers).</summary>
    public event Action<string, PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    public RoostRailSort RailSort => _railSort;

    /// <summary>Whether the session's pane is on screen right now: in the active tab and not hidden behind another
    /// pane's zoom. With <see cref="Window.IsActive"/> it's the Roost half of <see cref="AttentionSeen"/>.</summary>
    public bool IsOnScreen(string sessionId) =>
        _roster.Panes.FirstOrDefault(p => p.Session.SessionId == sessionId) is { } pane && _placed.Contains(pane.Key);

    /// <summary>The roster changed (a monitor scan landed) — re-sync feeds, panes and layout, and flash the
    /// taskbar when a session newly needs the user while the Roost isn't the active window.</summary>
    public void RosterChanged()
    {
        Refresh();
        // Each scan nudges every feed: a tailed pane re-reads even if its watcher missed the write (or never
        // armed, the folder not existing yet), and one whose transcript didn't exist yet looks for it again.
        foreach (var f in _feeds.Values) f?.Poke();
        if (_roster.NeedsYouArrivals.Count > 0 && !IsActive && TryGetPlatformHandle() is { } handle)
            PlatformServices.WindowChrome.FlashTaskbar(handle.Handle);
    }

    /// <summary>Goes to a session (a rail click, Ctrl+., a pill): its tab, or Focus when it's in none. Acknowledges
    /// a done-review session.</summary>
    public void FocusPane(string key)
    {
        if (_roster.Find(key) is not { } pane) return;
        // Every press inside a pane lands here; one on the pane that's already focused and on screen changes
        // nothing, so it skips the layout pass.
        bool unchanged = _focused == key && _placed.Contains(key);
        if (!unchanged)
        {
            _tabs.Show(key);
            _everPlaced.Add(key);
        }
        _focused = key;
        _focusChosen = true;
        _tabs.NoteFocus(key);
        if (pane is { Ended: false, Session.Status: SessionStatus.NeedsAttention })
            AcknowledgeRequested?.Invoke(pane.Session.Pid);
        if (!unchanged) Refresh();
    }

    /// <summary>Switches tab (the strip, Ctrl+0–9, Ctrl+Tab). Focus moves to the tab's first session.</summary>
    public void ActivateTab(string id)
    {
        if (_tabs.Activate(id)) Refresh();   // Refresh moves focus into the tab
    }

    // The session focus falls to in a tab: the zoomed one, else the first in reading order.
    private static string? FirstIn(RoostTab tab) =>
        tab.Zoomed is { } z && tab.At(z) is { } zoomed
            ? zoomed
            : tab.Layout.ReadingOrder.Select(r => tab.At(r.Id)).FirstOrDefault(k => k is not null);

    /// <summary>Re-orders the rail (its header toggle).</summary>
    public void SetRailSort(RoostRailSort sort)
    {
        sort = RoostRoster.Normalize(sort);
        if (sort == _railSort) return;
        _railSort = sort;
        RefreshSortSegments();
        RailSortChanged?.Invoke(sort);
        RefreshRail();
    }

    /// <summary>Puts <paramref name="key"/> in the active tab's region at <paramref name="slot"/> (a drop, the
    /// picker): it moves there, and the region's occupant takes its old place, or goes back to the rail.</summary>
    public void PlacePane(string key, int slot)
    {
        if (_roster.Find(key) is null || slot < 0 || slot >= _slotRegions.Count) return;
        var tab = _tabs.Active;
        _tabs.Assign(tab.Id, _slotRegions[slot], key);
        _everPlaced.Add(key);
        _focused = key;
        _focusChosen = true;
        Refresh();
    }

    // "+ New session": remember what's here, so the Perch session it starts can be shown when it appears.
    private void StartNewSession(RoostPlacement? target)
    {
        _keysAtNewSession = _roster.Panes.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        _newSessionUntil = Clock.Now.AddMinutes(1);
        _newSessionTarget = target;
        NewSessionRequested?.Invoke();
    }

    private FlyoutBase? _openFlyout;

    // ── Layout pass ───────────────────────────────────────────────────────────

    private void Refresh()
    {
        var all = _roster.Panes;
        // A close / reopen from this window. The app feeds adoptions and settles persisted cells with each scan; this
        // sync may run before the first one (an early open), so it leaves those cells alone.
        _tabs.Sync(all, resolveSeeds: false);
        // The painter edits the active tab only: switching away (or the tab closing) cancels the edit.
        if (_painter is { } painter && painter.TabId != _tabs.ActiveId) ClosePainter(null, refresh: false);
        SyncFeedsAndViews(all);
        AdmitStartedSession(all);
        // Focus is always in the active tab: a tab switch, a close, or a pane leaving (dropped on another tab,
        // removed, ended) hands it to the tab's first session.
        // Focus that falls somewhere wasn't chosen there, so it arms no Enter / Esc until the user picks a pane.
        if (_focused is not { } f || !_tabs.Active.Cells.Values.Contains(f))
        {
            _focused = FirstIn(_tabs.Active);
            _focusChosen = false;
        }

        _placed.Clear();
        LayoutStage(_tabs.Active);

        foreach (var pane in all)
        {
            var view = _views[pane.Key];
            view.CanTakeOver = !pane.Ended && CanTakeOver?.Invoke(pane.Session) == true;
            view.Update(pane, _feeds[pane.Key]);
            view.SetFocused(pane.Key == _focused);
            if (_placed.Contains(pane.Key)) view.Show();
            else if (!_warm.Contains(pane.Key)) view.Park();   // a warm pane keeps its thread, hidden
        }

        RefreshCells(all);
        RefreshTabStrip();
        RefreshRail();
        RefreshPills(all);
        RefreshHiddenRow();
        UpdatePulse();

        var placedSig = string.Join(",", _placed.Order(StringComparer.Ordinal));
        if (placedSig != _placedSig) { _placedSig = placedSig; PlacementChanged?.Invoke(); }
    }

    private string _placedSig = "";

    // Lays the active tab out: its shape on the tile panel (one full slot while a region is zoomed), a cell per
    // region, and each of its sessions' hosts at its region's slot. Hosts of panes elsewhere hide and stay warm.
    private void LayoutStage(RoostTab tab)
    {
        var layout = tab.Layout;
        int? zoom = tab.Zoomed is { } z && tab.At(z) is not null ? z : null;
        _grid.Layout = zoom is null ? layout : RoostGridLayout.Full;
        _slotRegions = zoom is null ? layout.Regions.Select(r => r.Id).ToList() : [];

        for (int i = 0; i < _cells.Length; i++)
        {
            _cells[i].Host.IsVisible = i < _slotRegions.Count;
            RoostTilePanel.SetSlot(_cells[i].Host, i < _slotRegions.Count ? i : -1);
        }

        var shown = zoom is { } zr
            ? [(tab.At(zr)!, 0)]
            : layout.Regions.Select((r, i) => (Key: tab.At(r.Id), Slot: i)).Where(x => x.Key is not null)
                .Select(x => (x.Key!, x.Slot)).ToList();
        var onStage = shown.Select(s => s.Item1).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, host) in _paneHosts)
        {
            if (onStage.Contains(key) || _warm.Contains(key)) continue;
            host.IsVisible = false;
            RoostTilePanel.SetSlot(host, -1);
            _warm.Add(key);
        }
        // Coming back on stage leaves the warm list first: a tab switched back to holds the oldest warm hosts, and
        // trimming before this would evict exactly the panes about to show (a full re-parent per switch).
        _warm.RemoveAll(onStage.Contains);
        while (_warm.Count > WarmLimit) RemovePaneHost(_warm[0]);   // parked by the refresh that follows
        foreach (var (key, slot) in shown)
        {
            if (!_views.TryGetValue(key, out var view)) continue;
            if (!_paneHosts.TryGetValue(key, out var host))
            {
                host = new Border { Margin = new Thickness(CellGap / 2), Child = view };
                _paneHosts[key] = host;
                _grid.Children.Add(host);
            }
            host.IsVisible = true;
            if (RoostTilePanel.GetSlot(host) != slot) RoostTilePanel.SetSlot(host, slot);
            _placed.Add(key);
        }
    }

    // The empty-region placeholders: shown where a region holds no session, with the words for the tab.
    private void RefreshCells(IReadOnlyList<RoostPane> all)
    {
        var tab = _tabs.Active;
        for (int slot = 0; slot < _slotRegions.Count; slot++)
        {
            bool empty = tab.At(_slotRegions[slot]) is null;
            var cell = _cells[slot];
            cell.Placeholder.IsVisible = empty;
            if (!empty) continue;
            (cell.Title.Text, cell.Body.Text) = tab.IsFocus
                ? all.Count == 0
                    ? ("No live sessions", "Sessions appear in the rail as soon as they start — or start one with + New session.")
                    : ("Focus", "Click a session in the rail to look at it here.")
                : ("Empty region", "Drop a session here, or click to pick one.");
            // Alt+N counts regions in reading order, not in slot (creation) order.
            cell.Chord.Text = tab.IsFocus ? "" : RoostKeys.ChordFor(RoostCommand.Region, tab.Layout.NumberOf(_slotRegions[slot])) ?? "";
            cell.Host.Cursor = tab.IsFocus ? Cursor.Default : HandCursor;
        }
    }

    // A session the user started with "+ New session" is shown when it first appears: in the region it was started
    // from if that's still empty, else wherever a rail click would put it (Focus).
    private void AdmitStartedSession(IReadOnlyList<RoostPane> all)
    {
        if (_keysAtNewSession is not { } before) return;
        if (Clock.Now > _newSessionUntil) { _keysAtNewSession = null; return; }
        if (all.FirstOrDefault(p => p.Session.IsPerchControlled && !p.Ended && !before.Contains(p.Key)) is not { } started) return;
        _keysAtNewSession = null;
        if (_newSessionTarget is { } t && _tabs.Find(t.TabId) is { } tab && tab.Layout.Find(t.RegionId) is not null && tab.At(t.RegionId) is null)
        {
            _tabs.Assign(t.TabId, t.RegionId, started.Key);
            _tabs.Activate(t.TabId);
        }
        else _tabs.Show(started.Key);
        _newSessionTarget = null;
        _everPlaced.Add(started.Key);
        // It can turn up long after the click, mid-way through reading another pane: shown, but not armed.
        _focused = started.Key;
        _focusChosen = false;
    }

    private void RemovePaneHost(string key)
    {
        _warm.Remove(key);
        if (!_paneHosts.Remove(key, out var host)) return;
        host.Child = null;
        _grid.Children.Remove(host);
    }

    // Creates a feed + view for each new pane, recreates a tailed feed whose session id moved (/clear), and
    // disposes both for panes the roster dropped.
    private void SyncFeedsAndViews(IReadOnlyList<RoostPane> panes)
    {
        var live = new HashSet<string>(panes.Select(p => p.Key), StringComparer.Ordinal);
        foreach (var gone in _views.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _views[gone].Park();
            _views.Remove(gone);
            if (_feeds.Remove(gone, out var f)) f?.Dispose();
            if (_focused == gone) _focused = null;
            RemovePaneHost(gone);
        }

        foreach (var pane in panes)
        {
            if (_feeds.TryGetValue(pane.Key, out var feed)
                && feed is { IsControlled: false } && feed.SessionId != pane.Session.SessionId && !pane.Ended)
            {
                feed.Dispose();
                _feeds.Remove(pane.Key);
            }
            if (!_feeds.ContainsKey(pane.Key)) _feeds[pane.Key] = _feedFactory(pane);

            if (!_views.ContainsKey(pane.Key))
            {
                var view = new SessionPane(_p, pane.Key);
                var key = pane.Key;
                // The session id is looked up at event time — a /clear swaps it under the same pane.
                string? Sid() => _roster.Find(key)?.Session.SessionId;
                view.Activated += FocusPane;
                view.ActionRequested += OnPaneAction;
                view.PermissionAnswered += (item, allow, mode) => { if (Sid() is { } s) PermissionAnswered?.Invoke(s, item, allow, mode); };
                view.QuestionAnswered += (item, answers) => { if (Sid() is { } s) QuestionAnswered?.Invoke(s, item, answers); };
                view.InterruptRequested += _ => { if (Sid() is { } s) InterruptRequested?.Invoke(s); };
                view.PromptSubmitted += OnPromptSubmitted;
                AttachDragSource(view.Header, key, isTab: false);
                _views[pane.Key] = view;
            }
        }
    }

    private void OnPromptSubmitted(string key, string text)
    {
        if (_roster.Find(key) is not { Ended: false } pane) return;
        PromptSubmitted?.Invoke(pane.Session.SessionId, text);
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private static RoostMods ToMods(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? RoostMods.Ctrl : 0)
        | (m.HasFlag(KeyModifiers.Shift) ? RoostMods.Shift : 0)
        | (m.HasFlag(KeyModifiers.Alt) ? RoostMods.Alt : 0);

    // Every window chord comes from RoostKeys — the same table the cheat-sheet and hint line read.
    private void OnChordKeyDown(object? sender, KeyEventArgs e)
    {
        // Unbound keys (typing included) resolve to nothing and pass straight through.
        if (RoostKeys.Resolve(e.Key.ToString(), ToMods(e.KeyModifiers)) is not { } hit) return;
        e.Handled = Run(hit.Command, hit.Arg);
    }

    private bool Run(RoostCommand command, int arg)
    {
        switch (command)
        {
            case RoostCommand.FocusTab:
                ActivateTab(RoostTabSet.FocusId);
                return true;
            case RoostCommand.Tab:
                if (arg <= _tabs.Tabs.Count) ActivateTab(_tabs.Tabs[arg - 1].Id);
                return true;
            case RoostCommand.NextTab:
                ActivateTab(_tabs.Cycle(+1));
                return true;
            case RoostCommand.PreviousTab:
                ActivateTab(_tabs.Cycle(-1));
                return true;
            case RoostCommand.NewTab:
                AddTab();
                return true;
            case RoostCommand.Region:
                // FocusPane un-zooms a tab zoomed on another region (RoostTabSet.Show) and refreshes if anything moved.
                if (_tabs.Active.Layout.ByNumber(arg) is { } region && _tabs.Active.At(region.Id) is { } key) FocusPane(key);
                return true;
            case RoostCommand.Zoom:
                if (_focused is { } focused) OnPaneAction(focused, RoostPaneAction.Zoom);
                return true;
            case RoostCommand.NextNeedingYou:
                if (_roster.NextNeedingYou(_focused) is { } next) FocusPane(next);
                return true;
            case RoostCommand.RenameTab:
                if (_tabs.Active.IsFocus || _renaming is not null) return false;
                BeginRename(_tabs.ActiveId);
                return true;
            default:
                return false;
        }
    }

    // Bubbling keys — anything a focused control (a thread, a composer) didn't already take.
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape && _ghost is not null) { EndDrag(drop: false); e.Handled = true; return; }
        // The painter takes Esc (Cancel) and Enter (Done) — and Enter must never reach a pane it covers, where it
        // would allow a permission the user can't see.
        if (_painter is { } painter && e.Key is Key.Escape or Key.Enter)
        {
            if (e.Key == Key.Escape) painter.Cancel();
            else painter.Done();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.Escape) e.Handled = FocusedPerchKey(e.Key);
        if (!e.Handled && e.Key == Key.Escape && _tabs.Active.Zoomed is not null)
        {
            _tabs.ToggleZoom(_tabs.ActiveId, null);
            Refresh();
            e.Handled = true;
        }
    }

    // The focused Perch pane's permission keys, as in SessionWindow (and the TUI): Enter allows a pending
    // permission (a question card is answered by picking, never a bare Enter); Esc denies it, or with nothing
    // pending interrupts a running turn. Terminal/IDE panes have no control channel, so these do nothing there.
    // Only for a pane the user can actually see (in the active tab, not behind a zoom) and chose (_focusChosen) —
    // else a bare Enter could approve a command never seen, or one in a pane focus merely fell to.
    private bool FocusedPerchKey(Key key)
    {
        if (!_focusChosen || _focused is not { } k || _roster.Find(k) is not { Ended: false } pane) return false;
        if (!_placed.Contains(k)) return false;
        if (!_feeds.TryGetValue(k, out var feed) || feed is not { IsControlled: true }) return false;
        var conv = feed.Conversation;
        var sid = pane.Session.SessionId;
        if (key == Key.Enter)
        {
            if (conv.PendingPermission is not { IsQuestion: false } allow) return false;
            PermissionAnswered?.Invoke(sid, allow, true, false);
            return true;
        }
        if (conv.PendingPermission is { } deny) { PermissionAnswered?.Invoke(sid, deny, false, false); return true; }
        if (conv.TurnActive) { InterruptRequested?.Invoke(sid); return true; }
        return false;
    }

    private void OnPaneAction(string key, RoostPaneAction action)
    {
        if (_roster.Find(key) is not { } pane) return;
        switch (action)
        {
            case RoostPaneAction.RemoveFromTab:
                if (_tabs.Unassign(key))
                {
                    if (_focused == key) _focused = null;
                    Refresh();
                }
                break;
            case RoostPaneAction.Zoom:
                if (_tabs.Locate(key).FirstOrDefault(p => p.TabId == _tabs.ActiveId) is { TabId: not null } at)
                {
                    _focused = key;
                    _focusChosen = true;
                    _tabs.ToggleZoom(at.TabId, at.RegionId);
                    Refresh();
                }
                break;
            case RoostPaneAction.OpenSession:
                OpenSessionRequested?.Invoke(pane.Session);
                break;
            case RoostPaneAction.CopyResume:
                _ = CopyAsync($"claude --resume {pane.Session.SessionId}");
                break;
            case RoostPaneAction.TakeOver:
                if (!pane.Ended && CanTakeOver?.Invoke(pane.Session) == true) TakeOverRequested?.Invoke(pane.Session);
                break;
            case RoostPaneAction.Close:
                if (_roster.Close(key)) Refresh();   // the roster's ClosedChanged persists it; Sync empties its region
                break;
        }
    }

    private async Task CopyAsync(string text)
    {
        try { if (Clipboard is { } clip) await clip.SetTextAsync(text); }
        catch { /* best-effort */ }
    }

    // ── Pulse ─────────────────────────────────────────────────────────────────

    // Placed panes that need you breathe their ring, and so do the dots of background tabs that need you. Nothing
    // breathes while the window is minimised — a session can sit waiting for hours — and restoring picks it back up.
    private void UpdatePulse()
    {
        bool any = WindowState != WindowState.Minimized && (_pulsingTabs.Count > 0 || _placed.Any(k => _views[k].Pulsing));
        if (any) PulseFrame();   // paint a frame now, so a fresh light doesn't wait for the timer
        if (any && !Pulse.ReduceMotion) { if (!_pulseTimer.IsEnabled) _pulseTimer.Start(); }
        else if (_pulseTimer.IsEnabled) _pulseTimer.Stop();
    }

    // The 1s clock: the time-bearing text ("Working · 41s", the rail's elapsed). Idle while minimised.
    private void ClockTick()
    {
        if (WindowState == WindowState.Minimized) return;
        foreach (var k in _placed) _views[k].Tick();
        RefreshRail();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        // Minimising stops the pulse; restoring restarts it and catches the clock's text up.
        UpdatePulse();
        ClockTick();
    }

    private void PulseFrame()
    {
        bool still = Pulse.ReduceMotion;
        double i = still ? 1 : Pulse.Intensity(2200);
        foreach (var k in _placed) _views[k].PulseTick(i, still);
        foreach (var id in _pulsingTabs)
            if (_tabViews.TryGetValue(id, out var v)) v.Halo.Opacity = still ? 0.3 : 0.12 + 0.4 * i;
        if (still) _pulseTimer.Stop();
    }

    private Border BarButton(string label, Action onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 5), BorderThickness = new Thickness(1),
            BorderBrush = _p.Border, Background = _p.Surface, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = _p.Text },
        };
        b.PointerEntered += (_, _) => b.BorderBrush = _p.BrandLine;
        b.PointerExited += (_, _) => b.BorderBrush = _p.Border;
        return b.OnLeftClick(onClick);
    }

    /// <summary>The Roost's "split panes" mark: a rounded rect split into a tall left pane and two stacked right
    /// panes (tmux main-vertical). The overlay's entry-point glyph (CP9) draws the same shape owner-drawn.</summary>
    internal static Control RoostGlyph(IBrush stroke, double size) => new global::Avalonia.Controls.Shapes.Path
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = stroke, StrokeThickness = 1.5,
        VerticalAlignment = VerticalAlignment.Center,
        Data = Geometry.Parse("M3,2 H13 A1.5,1.5 0 0 1 14.5,3.5 V12.5 A1.5,1.5 0 0 1 13,14 H3 A1.5,1.5 0 0 1 1.5,12.5 V3.5 A1.5,1.5 0 0 1 3,2 Z M7.5,2 V14 M7.5,8 H14.5"),
    };

    // One region's cell: a host border at the region's slot holding a dashed placeholder (shown while the region
    // is empty) — the pane host for an occupied region sits on top of it.
    private sealed record RegionCell(Border Host, Panel Placeholder, TextBlock Title, TextBlock Body, TextBlock Chord)
    {
        public static RegionCell Create(SessionPalette p, double margin)
        {
            var title = new TextBlock { FontFamily = p.Display, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = p.Muted, HorizontalAlignment = HorizontalAlignment.Center };
            var body = new TextBlock
            {
                FontFamily = p.Body, FontSize = 12, Foreground = p.Faint, HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
            };
            var chord = new TextBlock
            {
                FontFamily = p.Mono, FontSize = 10.5, Foreground = p.Faint, Margin = new Thickness(12, 10),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            };
            var placeholder = new Panel
            {
                Children =
                {
                    new Rectangle
                    {
                        Stroke = p.Border, StrokeThickness = 1.5, StrokeDashArray = new AvaloniaList<double> { 4, 3 }, RadiusX = 12, RadiusY = 12,
                        Fill = p.Surface, Opacity = 0.9,
                    },
                    new StackPanel
                    {
                        Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(16),
                        Children = { new TextBlock { Text = "+", FontSize = 22, Foreground = p.Faint, HorizontalAlignment = HorizontalAlignment.Center }, title, body },
                    },
                    chord,
                },
            };
            var host = new Border { Margin = new Thickness(margin), Background = Brushes.Transparent, Child = placeholder };
            return new RegionCell(host, placeholder, title, body, chord);
        }
    }
}
