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
/// </summary>
internal sealed class RoostWindow : Window
{
    private const double StagePad = 14, CellGap = 14;

    private readonly RoostRoster _roster;
    private readonly RoostTabSet _tabs;
    private readonly Func<RoostPane, RoostFeed?> _feedFactory;
    private readonly SessionPalette _p;

    private readonly Dictionary<string, RoostFeed?> _feeds = new(StringComparer.Ordinal);
    // One view per session (D1: a session is in one place, so a move within or between tabs keeps its view).
    // Lifting D1 would make this one view per placement, the feed shared.
    private readonly Dictionary<string, SessionPane> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoostPaneSize> _placed = new(StringComparer.Ordinal);
    // Sessions that have been in a tab (or Focus) since this window opened: a rail row without one reads "new".
    private readonly HashSet<string> _everPlaced = new(StringComparer.Ordinal);

    private readonly StackPanel _tabStrip;
    private readonly StackPanel _rail;
    private readonly Panel _stage;
    private readonly RoostTilePanel _grid;
    // One border per region of the active tab, at its slot: the empty-region placeholder, and what a drag
    // hit-tests against (a pane's host sits on top of an occupied one).
    private readonly RegionCell[] _cells = new RegionCell[RoostGridLayout.MaxRegions];
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
    private (string Key, Point Start, Control Source)? _press;
    private (string TabId, Point Start, Control Source)? _tabPress;
    private Border? _ghost;
    private int _dropSlot = -1;
    private string? _dropTab;

    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _clockTimer;

    private RoostRailSort _railSort;
    private readonly Dictionary<RoostRailSort, Border> _sortSegments = new();
    private string? _focused;
    // "+ New session" was clicked: the first new Perch pane to appear before the deadline is shown — in the region
    // it was started from (the empty-region picker), else Focus.
    private HashSet<string>? _keysAtNewSession;
    private DateTime _newSessionUntil;
    private RoostPlacement? _newSessionTarget;

    public RoostWindow(RoostRoster roster, RoostTabSet tabs, Func<RoostPane, RoostFeed?> feedFactory,
        SessionPalette? palette = null, RoostRailSort railSort = RoostRailSort.Status)
    {
        _roster = roster;
        _tabs = tabs;
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
        _hiddenRow.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowHiddenMenu(_hiddenRow); };
        var (keysRow, _) = RailFooterRow("⌨  Keys", "Every Roost shortcut");
        keysRow.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowKeys(keysRow); };
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
                    new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _rail },
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
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
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
        _unplacedPill.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (MostUrgent(_tabs.Unplaced(_roster.Panes).Where(p => !p.Ended).Select(p => p.Key)) is { } key) FocusPane(key);
        };
        (_elsewherePill, _elsewhereText, _elsewhereDot) = Pill();
        _elsewherePill[ToolTip.TipProperty] = "Waiting in another tab — click to go there";
        _elsewherePill.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var elsewhere = _tabs.All.Where(t => t.Id != _tabs.ActiveId).SelectMany(t => t.Cells.Values);
            if (MostUrgent(elsewhere) is { } key) FocusPane(key);
        };
        var hints = new TextBlock
        {
            Text = RoostKeys.HintLine + "  ·  drag to place  ·  ⌨ more",
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Every Roost shortcut",
        };
        hints.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowKeys(hints); };
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
        Content = new Panel
        {
            Children = { new DockPanel { LastChildFill = true, Children = { bar, railHost, stageColumn } }, _overlay },
        };

        // Window chords tunnel (so a focused thread can't swallow them); Enter / Esc bubble, so a focused control
        // takes them first.
        AddHandler(KeyDownEvent, OnChordKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnPageKeyDown;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pulseTimer.Tick += (_, _) => PulseFrame();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { foreach (var k in _placed.Keys) _views[k].Tick(); TickRail(); };
        _clockTimer.Start();

        Closed += (_, _) =>
        {
            _pulseTimer.Stop();
            _clockTimer.Stop();
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
        _roster.Panes.FirstOrDefault(p => p.Session.SessionId == sessionId) is { } pane && _placed.ContainsKey(pane.Key);

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
        bool unchanged = _focused == key && _placed.ContainsKey(key);
        if (!unchanged)
        {
            _tabs.Show(key);
            _everPlaced.Add(key);
        }
        _focused = key;
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

    /// <summary>HeadlessRenderer hook: "+ New session" clicked.</summary>
    internal void StartNewSessionForRender() => StartNewSession(null);

    /// <summary>HeadlessRenderer hook: whether pane <paramref name="key"/> is placed right now.</summary>
    internal bool IsOnScreenKey(string key) => _placed.ContainsKey(key);

    /// <summary>HeadlessRenderer hook: a drag of pane <paramref name="key"/> hovering the active tab's region at
    /// <paramref name="slot"/> (the ghost at its centre). <see cref="DropForRender"/> lets go.</summary>
    internal void DragForRender(string key, int slot)
    {
        _press = (key, default, this);
        StartGhost(key);
        if (slot < 0 || slot >= _cells.Length || !_cells[slot].Host.IsVisible) return;
        var host = _cells[slot].Host;
        if (host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), _overlay) is { } at) MoveGhost(at);
    }

    /// <summary>HeadlessRenderer hook: a drag of pane <paramref name="key"/> (or, with <paramref name="key"/> null, of
    /// tab <paramref name="tabId"/>) hovering tab <paramref name="ontoId"/>'s header.</summary>
    internal void DragOntoTabForRender(string? key, string? tabId, string ontoId)
    {
        if (key is not null) { _press = (key, default, this); StartGhost(key); }
        else { _tabPress = (tabId!, default, this); StartGhost(null, _tabs.Find(tabId!)?.Name); }
        var b = _tabViews[ontoId].Button;
        if (b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), _overlay) is { } at) MoveGhost(at);
    }

    internal void DropForRender() => EndDrag(drop: true);

    /// <summary>HeadlessRenderer hook: tab <paramref name="id"/>'s name being edited, with <paramref name="text"/>
    /// typed so far.</summary>
    internal void RenameForRender(string id, string text)
    {
        BeginRename(id);
        if (_tabViews.TryGetValue(id, out var v)) { v.Editor.Text = text; v.Editor.CaretIndex = text.Length; }
    }

    internal void EndRenameForRender(bool commit) => EndRename(commit);

    /// <summary>HeadlessRenderer hook: tab <paramref name="id"/>'s right-click menu.</summary>
    internal void OpenTabMenuForRender(string id)
    {
        if (_tabViews.TryGetValue(id, out var v) && v.Button.ContextFlyout is { } menu) { _openFlyout = menu; menu.ShowAt(v.Button); }
    }

    /// <summary>HeadlessRenderer hook: close a pane as its menu would.</summary>
    internal void ClosePaneForRender(string key) => OnPaneAction(key, RoostPaneAction.Close);

    /// <summary>HeadlessRenderer hook: zoom a pane as a header double-click would.</summary>
    internal void ZoomForRender(string key) => OnPaneAction(key, RoostPaneAction.Zoom);

    /// <summary>HeadlessRenderer hook: open the empty-region picker on slot <paramref name="slot"/>.</summary>
    internal void OpenPickerForRender(int slot) => ShowPicker(slot);

    /// <summary>HeadlessRenderer hook: open the keys cheat-sheet.</summary>
    internal void OpenKeysForRender() => ShowKeys(_hiddenRow.Parent as Control ?? this);

    /// <summary>HeadlessRenderer hook: dismiss the picker / cheat-sheet.</summary>
    internal void CloseFlyoutForRender() { _openFlyout?.Hide(); _openFlyout = null; }

    private global::Avalonia.Controls.Primitives.FlyoutBase? _openFlyout;

    // ── Layout pass ───────────────────────────────────────────────────────────

    private void Refresh()
    {
        var all = _roster.Panes;
        _tabs.Sync(all);   // a close / reopen from this window (the app feeds adoptions with each scan)
        SyncFeedsAndViews(all);
        AdmitStartedSession(all);
        // Focus is always in the active tab: a tab switch, a close, or a pane leaving (dropped on another tab,
        // removed, ended) hands it to the tab's first session.
        if (_focused is not { } f || !_tabs.Active.Cells.Values.Contains(f)) _focused = FirstIn(_tabs.Active);

        _placed.Clear();
        LayoutStage(_tabs.Active);

        foreach (var pane in all)
        {
            var view = _views[pane.Key];
            view.CanTakeOver = !pane.Ended && CanTakeOver?.Invoke(pane.Session) == true;
            view.Update(pane, _feeds[pane.Key]);
            view.SetFocused(pane.Key == _focused);
            if (_placed.TryGetValue(pane.Key, out var size)) view.SetSize(size, held: false);
            else if (!_warm.Contains(pane.Key)) view.Park();   // a warm pane keeps its thread, hidden
        }

        RefreshCells(all);
        RefreshTabStrip();
        RefreshRail();
        RefreshPills(all);
        RefreshHiddenRow();
        UpdatePulse();

        var placedSig = string.Join(",", _placed.Keys.Order(StringComparer.Ordinal));
        if (placedSig != _placedSig) { _placedSig = placedSig; PlacementChanged?.Invoke(); }
    }

    private string _placedSig = "";

    // Lays the active tab out: its shape on the tile panel (one full slot while a region is zoomed), a cell per
    // region, and each of its sessions' hosts at its region's slot. Hosts of panes elsewhere hide and stay warm.
    private void LayoutStage(RoostTab tab)
    {
        var layout = tab.Layout;
        int? zoom = tab.Zoomed is { } z && tab.At(z) is not null ? z : null;
        _grid.Shape = zoom is null ? layout.ToShape() : RoostTemplates.Shape(RoostSnapTemplate.Full);
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
            _warm.Remove(key);
            host.IsVisible = true;
            if (RoostTilePanel.GetSlot(host) != slot) RoostTilePanel.SetSlot(host, slot);
            _placed[key] = RoostPaneSize.Expanded;
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
            cell.Chord.Text = tab.IsFocus ? "" : RoostKeys.ChordFor(RoostCommand.Region, slot + 1) ?? "";
            cell.Host.Cursor = tab.IsFocus ? Cursor.Default : new Cursor(StandardCursorType.Hand);
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
        _focused = started.Key;
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
                AttachDragSource(view.Header, key);
                _views[pane.Key] = view;
            }
        }
    }

    private void OnPromptSubmitted(string key, string text)
    {
        if (_roster.Find(key) is not { Ended: false } pane) return;
        PromptSubmitted?.Invoke(pane.Session.SessionId, text);
    }

    // The most urgent of some sessions (by tab light, then roster order), or null.
    private string? MostUrgent(IEnumerable<string> keys)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        return _roster.Panes.Where(p => wanted.Contains(p.Key))
            .OrderByDescending(RoostTabStatus.LightOf).Select(p => p.Key).FirstOrDefault();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private static RoostMods ToMods(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Control) ? RoostMods.Ctrl : 0)
        | (m.HasFlag(KeyModifiers.Shift) ? RoostMods.Shift : 0)
        | (m.HasFlag(KeyModifiers.Alt) ? RoostMods.Alt : 0);

    // Every window chord comes from RoostKeys — the same table the cheat-sheet and hint line read.
    private void OnChordKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = ToMods(e.KeyModifiers);
        if (mods == RoostMods.None && e.Key != Key.F2) return;
        if (RoostKeys.Resolve(e.Key.ToString(), mods) is not { } hit) return;
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
            case RoostCommand.PreviousTab:
            {
                var ids = _tabs.All.Select(t => t.Id).ToList();
                int at = ids.IndexOf(_tabs.ActiveId);
                ActivateTab(ids[(at + (command == RoostCommand.NextTab ? 1 : ids.Count - 1)) % ids.Count]);
                return true;
            }
            case RoostCommand.NewTab:
                AddTab();
                return true;
            case RoostCommand.Region:
            {
                var tab = _tabs.Active;
                var order = tab.Layout.ReadingOrder;
                if (arg <= order.Count && tab.At(order[arg - 1].Id) is { } key)
                {
                    if (tab.Zoomed is { } z && z != order[arg - 1].Id) _tabs.ToggleZoom(tab.Id, null);
                    FocusPane(key);
                    Refresh();
                }
                return true;
            }
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
    // Only for a pane the user can actually see (in the active tab, not behind a zoom) — else a bare Enter could
    // approve a command never seen.
    private bool FocusedPerchKey(Key key)
    {
        if (_focused is not { } k || _roster.Find(k) is not { Ended: false } pane) return false;
        if (!_placed.TryGetValue(k, out var size) || size != RoostPaneSize.Expanded) return false;
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

    // Placed panes that need you breathe their ring, and so do the dots of background tabs that need you.
    private void UpdatePulse()
    {
        bool any = _pulsingTabs.Count > 0 || _placed.Keys.Any(k => _views[k].Pulsing);
        if (any) PulseFrame();   // paint a frame now, so a fresh light doesn't wait for the timer
        if (any && !Pulse.ReduceMotion) { if (!_pulseTimer.IsEnabled) _pulseTimer.Start(); }
        else if (_pulseTimer.IsEnabled) _pulseTimer.Stop();
    }

    private void PulseFrame()
    {
        bool still = Pulse.ReduceMotion;
        double i = still ? 1 : Pulse.Intensity(2200);
        foreach (var k in _placed.Keys) _views[k].PulseTick(i, still);
        foreach (var id in _pulsingTabs)
            if (_tabViews.TryGetValue(id, out var v)) v.Halo.Opacity = still ? 0.3 : 0.12 + 0.4 * i;
        if (still) _pulseTimer.Stop();
    }

    // ── Tab strip ─────────────────────────────────────────────────────────────

    private sealed record TabView(Border Button, Ellipse Dot, Ellipse Halo, TextBlock Name, TextBox Editor, Border CountPill, TextBlock Count);

    private readonly Dictionary<string, TabView> _tabViews = new(StringComparer.Ordinal);
    private string? _stripSig;
    // Background tabs whose light is needs-you (awaiting input or an API error): their dot's halo breathes.
    private readonly HashSet<string> _pulsingTabs = new(StringComparer.Ordinal);
    // The tab whose name is being edited in place (double-click, F2, its menu, or a new tab), or null.
    private string? _renaming;

    // The strip is rebuilt only when its tabs change (added, closed, renamed, reordered); every other pass just
    // re-lights the existing buttons. Never mid-drag: that would drop the captured tab.
    private void RefreshTabStrip()
    {
        var sig = string.Join("|", _tabs.All.Select(t => $"{t.Id}:{t.Name}"));
        if (sig != _stripSig && _tabPress is null)
        {
            _stripSig = sig;
            _tabStrip.Children.Clear();
            _tabViews.Clear();
            _renaming = null;
            int n = 0;
            foreach (var tab in _tabs.All)
            {
                var view = TabButton(tab, tab.IsFocus ? 0 : ++n);
                _tabViews[tab.Id] = view;
                _tabStrip.Children.Add(view.Button);
            }
            if (_tabs.Tabs.Count < RoostTabSet.MaxTabs) _tabStrip.Children.Add(NewTabButton());
        }
        _pulsingTabs.Clear();
        foreach (var tab in _tabs.All)
            if (_tabViews.TryGetValue(tab.Id, out var view)) UpdateTabButton(view, tab);
    }

    private TabView TabButton(RoostTab tab, int number)
    {
        var dot = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var halo = new Ellipse { Width = 15, Height = 15, Opacity = 0, IsHitTestVisible = false };
        var name = new TextBlock
        {
            Text = tab.IsFocus ? "◻ Focus" : tab.Name, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var editor = new TextBox
        {
            Text = tab.Name, PlaceholderText = "Tab name", MaxLength = RoostTabSet.MaxNameLength, FontFamily = _p.Body,
            FontWeight = FontWeight.SemiBold, FontSize = 12.5, MinHeight = 0, MinWidth = 90, MaxWidth = 180,
            // The negative margin keeps the editor (taller than the name: its border and padding) from growing the strip.
            Padding = new Thickness(5, 1), Margin = new Thickness(0, -4), VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        var count = new TextBlock { FontFamily = _p.Mono, FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var countPill = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(6, 0), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = count,
        };
        var chord = tab.IsFocus ? RoostKeys.ChordFor(RoostCommand.FocusTab) : number <= 9 ? RoostKeys.ChordFor(RoostCommand.Tab, number) : null;
        var button = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(9, 6, 11, 7), BorderThickness = new Thickness(1, 1, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 32,
            [ToolTip.TipProperty] = (tab.IsFocus ? "Focus — sessions in no tab open here" : $"{tab.Name} — double-click to rename")
                + (chord is null ? "" : $"  ·  {chord}"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children =
                {
                    new Panel { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Children = { halo, dot } },
                    name, editor, countPill,
                },
            },
        };
        var id = tab.Id;
        button.PointerEntered += (_, _) => { if (id != _tabs.ActiveId) button.Background = _p.Raised2; };
        button.PointerExited += (_, _) => { if (id != _tabs.ActiveId) button.Background = Brushes.Transparent; };
        if (!tab.IsFocus)
        {
            AttachTabDragSource(button, id);   // first, so a reorder marks the release handled before the click sees it
            button.DoubleTapped += (_, e) => { if (_renaming != id) { e.Handled = true; BeginRename(id); } };
            button.ContextFlyout = TabMenu(id);
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; EndRename(commit: true); }
                else if (e.Key == Key.Escape) { e.Handled = true; EndRename(commit: false); }
            };
            editor.LostFocus += (_, _) => { if (_renaming == id) EndRename(commit: true); };
        }
        button.PointerReleased += (_, e) =>
        {
            if (e.Handled || _renaming == id) return;
            if (e.InitialPressMouseButton == MouseButton.Left) ActivateTab(id);
            else if (e.InitialPressMouseButton == MouseButton.Middle && !tab.IsFocus) CloseTab(id);   // as a browser tab
        };
        return new TabView(button, dot, halo, name, editor, countPill, count);
    }

    // A tab's right-click menu. "Edit layout…" joins it with the painter (roost-tabs T6).
    private MenuFlyout TabMenu(string id)
    {
        var rename = new MenuItem { Header = "Rename", InputGesture = new KeyGesture(Key.F2) };
        rename.Click += (_, _) => BeginRename(id);
        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.Click += (_, _) => { if (_tabs.DuplicateTab(id) is { } copy) ActivateTab(copy.Id); };
        var close = new MenuItem { Header = "Close tab" };
        close.Click += (_, _) => CloseTab(id);
        var menu = new MenuFlyout { Items = { rename, duplicate, new Separator(), close } };
        // Duplicate needs room for one more tab.
        menu.Opening += (_, _) => duplicate.IsEnabled = _tabs.Tabs.Count < RoostTabSet.MaxTabs;
        return menu;
    }

    private void UpdateTabButton(TabView v, RoostTab tab)
    {
        bool on = tab.Id == _tabs.ActiveId;
        var light = RoostTabStatus.For(tab, _roster.Panes);
        var brush = LightBrush(light.Light);
        v.Button.Background = on ? _p.Ground : v.Button.IsPointerOver ? _p.Raised2 : Brushes.Transparent;
        v.Button.BorderBrush = on ? _p.Border : Brushes.Transparent;
        v.Name.Foreground = on ? _p.Title : _p.Muted;
        ((Control)v.Dot.Parent!).IsVisible = light.Light != RoostLight.None;
        v.Dot.Fill = brush;
        v.Halo.Fill = brush;
        v.CountPill.IsVisible = light.Count > 0;
        v.Count.Text = light.Count.ToString();
        v.Count.Foreground = brush;
        v.CountPill.BorderBrush = brush;
        // Needs-you in a tab you're not looking at breathes, like a pane's ring; the active tab's panes ring themselves.
        if (!on && light.Light >= RoostLight.Awaiting) _pulsingTabs.Add(tab.Id);
        else v.Halo.Opacity = 0;
    }

    // ── Tab rename / close ────────────────────────────────────────────────────

    /// <summary>Edits a tab's name in place (Focus can't be renamed): Enter or a click away keeps it, Esc
    /// cancels.</summary>
    private void BeginRename(string id)
    {
        if (_renaming is not null) EndRename(commit: true);
        if (id == RoostTabSet.FocusId || _tabs.Find(id) is not { } tab || !_tabViews.TryGetValue(id, out var v)) return;
        _renaming = id;
        v.Editor.Text = tab.Name;
        v.Name.IsVisible = false;
        v.Editor.IsVisible = true;
        v.Editor.Focus();
        v.Editor.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (_renaming is not { } id) return;
        _renaming = null;
        if (!_tabViews.TryGetValue(id, out var v)) return;
        bool renamed = commit && _tabs.RenameTab(id, v.Editor.Text ?? "");
        v.Editor.IsVisible = false;
        v.Name.IsVisible = true;
        if (renamed) Refresh();   // the strip rebuilds with the new name (and tooltip)
    }

    private void CloseTab(string id)
    {
        if (_renaming == id) EndRename(commit: false);
        if (_tabs.CloseTab(id)) Refresh();
    }

    private Border NewTabButton()
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), Margin = new Thickness(2, 0, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = $"New tab  ·  {RoostKeys.ChordFor(RoostCommand.NewTab)}",
            Child = new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted },
        };
        b.PointerEntered += (_, _) => b.Background = _p.Raised2;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) AddTab(); };
        return b;
    }

    // A new tab opens with its name ready to type over ("Tab 3"; Esc keeps it).
    private void AddTab()
    {
        if (_tabs.AddTab(layout: RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2)) is not { } tab) return;
        ActivateTab(tab.Id);
        BeginRename(tab.Id);
    }

    private IBrush LightBrush(RoostLight light) => light switch
    {
        RoostLight.Error => _p.Err,
        RoostLight.Awaiting => _p.Await,
        RoostLight.Done => _p.Attn,
        RoostLight.Working => _p.Ok,
        _ => _p.Idle,
    };

    // ── Pills, hidden panes, keys ─────────────────────────────────────────────

    private (Border, TextBlock, Ellipse) Pill()
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = _p.Await, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            CornerRadius = SessionPalette.PillRadius,
            Padding = new Thickness(12, 3), BorderThickness = new Thickness(1), BorderBrush = _p.Border,
            Background = _p.Surface, Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } },
        };
        return (pill, text, dot);
    }

    // "N not in a tab · 1 needs you", and "K need you in other tabs" when another tab is calling.
    private void RefreshPills(IReadOnlyList<RoostPane> all)
    {
        var off = _tabs.Unplaced(all).Where(p => !p.Ended).ToList();
        int needs = off.Count(p => p.Group == RoostGroup.NeedsYou);
        _unplacedPill.IsVisible = off.Count > 0;
        _unplacedText.Text = needs > 0 ? $"{off.Count} not in a tab · {needs} need{(needs == 1 ? "s" : "")} you" : $"{off.Count} not in a tab";
        _unplacedDot.IsVisible = needs > 0;
        _unplacedPill.BorderBrush = needs > 0 ? _p.Await : _p.Border;
        _unplacedText.Foreground = needs > 0 ? _p.Await : _p.Muted;

        var elsewhere = _tabs.Elsewhere(all);
        _elsewherePill.IsVisible = elsewhere.Count > 0;
        _elsewhereText.Text = $"{elsewhere.Count} need{(elsewhere.Count == 1 ? "s" : "")} you in other tabs";
        var brush = LightBrush(elsewhere.Light);
        _elsewhereDot.IsVisible = true;
        _elsewhereDot.Fill = brush;
        _elsewherePill.BorderBrush = brush;
        _elsewhereText.Foreground = brush;
    }

    private (Border Row, TextBlock Text) RailFooterRow(string text, string tip)
    {
        var label = new TextBlock { Text = text, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5), Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = tip, Child = label,
        };
        row.PointerEntered += (_, _) => { row.Background = _p.Raised2; label.Foreground = _p.Text; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; label.Foreground = _p.Muted; };
        return (row, label);
    }

    private void RefreshHiddenRow()
    {
        int n = _roster.ClosedPanes.Count;
        _hiddenRow.IsVisible = n > 0;
        _hiddenText.Text = $"{n} hidden";
    }

    // "N hidden": closed panes whose sessions still run. A click lists them to reopen (one, or all).
    private void ShowHiddenMenu(Control anchor)
    {
        var flyout = new MenuFlyout { Placement = global::Avalonia.Controls.PlacementMode.RightEdgeAlignedBottom };
        foreach (var pane in _roster.ClosedPanes)
        {
            var item = new MenuItem { Header = $"Reopen {pane.Session.DisplayName}" };
            var key = pane.Key;
            item.Click += (_, _) => Reopen([key]);
            flyout.Items.Add(item);
        }
        if (_roster.ClosedPanes.Count > 1)
        {
            flyout.Items.Add(new Separator());
            var all = new MenuItem { Header = "Reopen all" };
            all.Click += (_, _) => Reopen(_roster.ClosedPanes.Select(p => p.Key).ToList());
            flyout.Items.Add(all);
        }
        flyout.ShowAt(anchor);
    }

    private void Reopen(IReadOnlyList<string> keys)
    {
        bool changed = false;
        foreach (var k in keys) changed |= _roster.Reopen(k);
        if (!changed) return;
        if (keys.Count == 1) FocusPane(keys[0]);
        else Refresh();
    }

    // The cheat-sheet: every RoostKeys row, by section.
    private void ShowKeys(Control anchor)
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 320 };
        foreach (var group in RoostKeys.All.GroupBy(r => r.Group))
        {
            var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4, ColumnSpacing = 14 };
            int r = 0;
            foreach (var key in group)
            {
                rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                rows.Children.Add(new TextBlock { Text = key.Chord, FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Text, [Grid.RowProperty] = r });
                rows.Children.Add(new TextBlock
                {
                    Text = key.Description, FontFamily = _p.Body, FontSize = 12, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
                    [Grid.RowProperty] = r, [Grid.ColumnProperty] = 1,
                });
                r++;
            }
            body.Children.Add(new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = group.Key.ToString().ToUpperInvariant(), FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint },
                    rows,
                },
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = "Drag a rail row or a pane header onto a region to place it · double-click a header to zoom",
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
        });
        var flyout = new Flyout { Placement = global::Avalonia.Controls.PlacementMode.TopEdgeAlignedLeft, Content = body };
        _openFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    // ── Empty-region picker ──────────────────────────────────────────────────

    // Click an empty region: pick a session for it — those in no tab first (most urgent first), then the ones in
    // other tabs (picking one moves it here) — or start a new session in it.
    private void ShowPicker(int slot)
    {
        if (slot < 0 || slot >= _slotRegions.Count) return;
        var tab = _tabs.Active;
        if (tab.IsFocus) return;
        int region = _slotRegions[slot];
        var flyout = new Flyout { Placement = global::Avalonia.Controls.PlacementMode.Center };
        var list = new StackPanel { Spacing = 1, MinWidth = 260 };
        list.Children.Add(new TextBlock
        {
            Text = "Put a session here", FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, Margin = new Thickness(8, 0, 8, 4),
        });
        var candidates = _roster.Panes.Where(p => !p.Ended && !tab.Cells.Values.Contains(p.Key))
            .OrderBy(p => _tabs.IsPlaced(p.Key))
            .ThenByDescending(RoostTabStatus.LightOf)
            .ThenBy(p => p.Session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
            list.Children.Add(new TextBlock { Text = "Every live session is already in this tab", FontSize = 12, Foreground = _p.Muted, Margin = new Thickness(8, 4) });
        foreach (var pane in candidates)
        {
            var key = pane.Key;
            list.Children.Add(PickerRow(pane, TabTagFor(key), () => { flyout.Hide(); PlacePane(key, slot); }));
        }
        list.Children.Add(new Border { Height = 1, Background = _p.Border, Margin = new Thickness(4, 4) });
        list.Children.Add(PickerRow(null, null, () => { flyout.Hide(); StartNewSession(new RoostPlacement(tab.Id, region)); }));
        flyout.Content = new ScrollViewer { MaxHeight = 380, Content = list };
        _openFlyout = flyout;
        flyout.ShowAt(_cells[slot].Host);
    }

    private Border PickerRow(RoostPane? pane, string? tag, Action onPick)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (pane is not null)
        {
            dock.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Fill = LightBrush(RoostTabStatus.LightOf(pane)), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), [DockPanel.DockProperty] = Dock.Left,
            });
            if (tag is not null)
                dock.Children.Add(new TextBlock
                {
                    Text = tag, FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0), MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis,
                    [DockPanel.DockProperty] = Dock.Right,
                });
        }
        dock.Children.Add(new TextBlock
        {
            Text = pane?.Session.DisplayName ?? "+ New session here", FontFamily = _p.Body, FontSize = 13,
            FontWeight = FontWeight.SemiBold, Foreground = pane is null ? _p.Brand : _p.Text,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 6), Cursor = new Cursor(StandardCursorType.Hand), Child = dock,
        };
        row.PointerEntered += (_, _) => row.Background = _p.Raised2;
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; onPick(); } };
        return row;
    }

    // The tab a session lives in, as the rail and picker label it — null when it's in none.
    private string? TabTagFor(string key) =>
        _tabs.Locate(key).FirstOrDefault() is { TabId: { } id } ? id == RoostTabSet.FocusId ? "focus" : _tabs.Find(id)?.Name : null;

    // ── Rail ──────────────────────────────────────────────────────────────────

    // One rail row's controls, kept so the 1s clock and each scan update text and colour in place.
    private sealed record RailRowView(Border Row, Border Bar, Ellipse Dot, TextBlock Elapsed, Border NewTag, TextBlock Diamond, TextBlock Tag, TextBlock Name);

    private readonly Dictionary<string, RailRowView> _railRows = new(StringComparer.Ordinal);
    private string? _railSig;

    // The rail's controls are rebuilt only when its shape changes (a pane joins, leaves or changes group, or the
    // sort flips); every other pass, the clock's included, just re-labels the existing rows. Never mid-press:
    // rebuilding would drop the row being pressed or dragged (EndDrag refreshes once it lets go).
    private void RefreshRail()
    {
        var sections = RailSections();
        var sig = $"{(int)_railSort}#" + string.Join("|", sections
            .Select(s => $"{s.Title}:{string.Join(",", s.Panes.Select(p => p.Key))}"));
        if (sig != _railSig && _press is null)
        {
            _railSig = sig;
            RebuildRail(sections);
        }
        foreach (var section in sections)
            foreach (var pane in section.Panes)
                if (_railRows.TryGetValue(pane.Key, out var row)) UpdateRailRow(row, pane);
    }

    private void TickRail() => RefreshRail();

    // The rail's headed sections for the current sort: the non-empty status groups, or one A–Z list.
    private List<(string Title, IReadOnlyList<RoostPane> Panes)> RailSections() =>
        _railSort == RoostRailSort.Alphabetical
            ? _roster.RailAlphabetical.Count > 0 ? [("ALL SESSIONS", _roster.RailAlphabetical)] : []
            : _roster.Rail.Where(g => g.Panes.Count > 0).Select(g => (GroupTitle(g.Group), g.Panes)).ToList();

    private void RebuildRail(IReadOnlyList<(string Title, IReadOnlyList<RoostPane> Panes)> sections)
    {
        _rail.Children.Clear();
        _railRows.Clear();
        if (_roster.Panes.Count == 0)
        {
            _rail.Children.Add(new TextBlock { Text = "No live sessions", Margin = new Thickness(8, 0), FontSize = 12, Foreground = _p.Faint });
            return;
        }
        foreach (var (title, panes) in sections)
        {
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new DockPanel
            {
                Margin = new Thickness(8, 0, 8, 4),
                Children =
                {
                    new TextBlock { Text = panes.Count.ToString(), FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Text = title, FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint },
                },
            });
            foreach (var pane in panes)
            {
                var row = RailRow(pane.Key);
                _railRows[pane.Key] = row;
                col.Children.Add(row.Row);
            }
            _rail.Children.Add(col);
        }
    }

    // The rail header's "Status | A–Z" toggle: a small segmented control, docked above the scrolling list.
    private Control RailSortToggle()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        foreach (var (sort, label, tip) in new[]
                 {
                     (RoostRailSort.Status, "Status", "Group sessions by what they need from you"),
                     (RoostRailSort.Alphabetical, "A–Z", "List sessions by name"),
                 })
        {
            var seg = new Border
            {
                Padding = new Thickness(8, 3), Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = _p.Border, BorderThickness = new Thickness(sort == RoostRailSort.Status ? 0 : 1, 0, 0, 0),
                [Grid.ColumnProperty] = (int)sort, [ToolTip.TipProperty] = tip,
                Child = new TextBlock
                {
                    Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            seg.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SetRailSort(sort); };
            _sortSegments[sort] = seg;
            row.Children.Add(seg);
        }
        RefreshSortSegments();
        return new Border
        {
            Margin = new Thickness(16, 12, 16, 8), CornerRadius = new CornerRadius(8), BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), Background = _p.Surface, ClipToBounds = true,
            [DockPanel.DockProperty] = Dock.Top, Child = row,
        };
    }

    private void RefreshSortSegments()
    {
        foreach (var (sort, seg) in _sortSegments)
        {
            bool on = sort == _railSort;
            seg.Background = on ? _p.BrandWash : Brushes.Transparent;
            ((TextBlock)seg.Child!).Foreground = on ? _p.Text : _p.Muted;
        }
    }

    private static string GroupTitle(RoostGroup g) => g switch
    {
        RoostGroup.NeedsYou => "NEEDS YOU",
        RoostGroup.DoneReview => "DONE · REVIEW",
        RoostGroup.Working => "WORKING",
        _ => "QUIET",
    };

    // A rail row's controls; UpdateRailRow fills them from the pane.
    private RailRowView RailRow(string key)
    {
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 6, 1), [DockPanel.DockProperty] = Dock.Left };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var elapsed = new TextBlock { FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        var tag = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 10, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), MaxWidth = 56, TextTrimming = TextTrimming.CharacterEllipsis, [DockPanel.DockProperty] = Dock.Right,
        };
        var newTag = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(1), BorderBrush = _p.BrandLine,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Child = new TextBlock { Text = "new", FontFamily = _p.Body, FontSize = 10, Foreground = _p.Brand },
        };
        var diamond = new TextBlock
        {
            Text = "◆", FontSize = 9, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        var name = new TextBlock
        {
            FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(2, 6, 8, 6), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    bar,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Left, Children = { dot } },
                    elapsed, tag, newTag, diamond, name,
                },
            },
        };
        // Focus moves without a rebuild, so the hover reads it at event time.
        row.PointerEntered += (_, _) => { if (key != _focused) row.Background = _p.Raised2; };
        row.PointerExited += (_, _) => row.Background = key == _focused ? _p.BrandWash : Brushes.Transparent;
        AttachDragSource(row, key);   // first, so a drop marks the release handled before the click sees it
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !e.Handled) FocusPane(key); };
        return new RailRowView(row, bar, dot, elapsed, newTag, diamond, tag, name);
    }

    private void UpdateRailRow(RailRowView v, RoostPane pane)
    {
        var s = pane.Session;
        bool on = pane.Key == _focused;
        var tag = TabTagFor(pane.Key);
        bool inActive = _tabs.Active.Cells.Values.Contains(pane.Key);
        bool fresh = tag is null && !pane.Ended && !_everPlaced.Contains(pane.Key);
        v.Dot.Fill = pane.Ended ? _p.Faint : s.Status switch
        {
            SessionStatus.AwaitingInput => _p.Await,
            SessionStatus.ApiError => _p.Err,
            SessionStatus.NeedsAttention => _p.Attn,
            SessionStatus.Running => _p.Ok,
            _ => _p.Idle,
        };
        var elapsed = pane.Ended ? "ended"
            : s.Status == SessionStatus.AwaitingInput ? s.AwaitingElapsedLabel() ?? ""
            : s.Status == SessionStatus.Running ? s.RunningElapsedLabel() ?? ""
            : "";
        if (v.Elapsed.Text != elapsed) v.Elapsed.Text = elapsed;
        v.Bar.Background = inActive ? _p.Brand : Brushes.Transparent;
        // Its own tab is marked by the bar; another tab's name is the tag.
        v.Tag.Text = inActive ? "" : tag ?? "";
        v.Tag.IsVisible = !inActive && tag is not null;
        v.NewTag.IsVisible = fresh;
        v.Diamond.IsVisible = s.IsPerchControlled;
        v.Name.Text = s.DisplayName;
        v.Name.Foreground = _p.Text;
        v.Row.BorderBrush = on ? _p.BrandLine : Brushes.Transparent;
        if (on || !v.Row.IsPointerOver) v.Row.Background = on ? _p.BrandWash : Brushes.Transparent;
        v.Row.Opacity = pane.Ended ? 0.6 : 1;
        ToolTip.SetTip(v.Row, inActive ? "In this tab" : tag is null
            ? "In no tab — click to look at it in Focus, or drag it onto a region"
            : $"In {tag} — click to go there, or drag it onto a region here");
    }

    // ── Drag to place ─────────────────────────────────────────────────────────

    // Pressing a pane header (or a rail row) and moving a few pixels starts a drag: a ghost chip follows the
    // pointer and the region under it is marked; letting go there puts the session in that region (PlacePane).
    private void AttachDragSource(Control source, string key)
    {
        source.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            _press = (key, e.GetPosition(_overlay), source);
            e.Pointer.Capture(source);
        };
        source.PointerMoved += (_, e) =>
        {
            if (_press is not { } press || !ReferenceEquals(press.Source, source)) return;
            var at = e.GetPosition(_overlay);
            if (_ghost is null)
            {
                if (Math.Abs(at.X - press.Start.X) + Math.Abs(at.Y - press.Start.Y) < 6) return;
                StartGhost(press.Key);
            }
            MoveGhost(at);
        };
        source.PointerReleased += (_, e) =>
        {
            if (_press is not { } press || !ReferenceEquals(press.Source, source)) return;
            if (_ghost is not null) e.Handled = true;
            EndDrag(drop: true);
            e.Pointer.Capture(null);
        };
        source.PointerCaptureLost += (_, _) =>
        {
            if (_press is { } press && ReferenceEquals(press.Source, source)) EndDrag(drop: false);
        };
    }

    // Pressing a tab header and moving a few pixels drags the tab: letting go over another tab's header moves it
    // to that tab's place (Focus stays first).
    private void AttachTabDragSource(Control source, string tabId)
    {
        source.PointerPressed += (_, e) =>
        {
            if (_renaming == tabId || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            _tabPress = (tabId, e.GetPosition(_overlay), source);
            e.Pointer.Capture(source);
        };
        source.PointerMoved += (_, e) =>
        {
            if (_tabPress is not { } press || !ReferenceEquals(press.Source, source)) return;
            var at = e.GetPosition(_overlay);
            if (_ghost is null)
            {
                if (Math.Abs(at.X - press.Start.X) + Math.Abs(at.Y - press.Start.Y) < 6) return;
                StartGhost(null, _tabs.Find(tabId)?.Name ?? "");
            }
            MoveGhost(at);
        };
        source.PointerReleased += (_, e) =>
        {
            if (_tabPress is not { } press || !ReferenceEquals(press.Source, source)) return;
            if (_ghost is not null) e.Handled = true;
            EndDrag(drop: true);
            e.Pointer.Capture(null);
        };
        source.PointerCaptureLost += (_, _) =>
        {
            if (_tabPress is { } press && ReferenceEquals(press.Source, source)) EndDrag(drop: false);
        };
    }

    // The tab header under the pointer, if any.
    private string? TabAt(Point at)
    {
        foreach (var (id, v) in _tabViews)
        {
            if (!v.Button.IsVisible || v.Button.TranslatePoint(default, _overlay) is not { } origin) continue;
            if (new Rect(origin, v.Button.Bounds.Size).Contains(at)) return id;
        }
        return null;
    }

    private void MarkDrop(Control target)
    {
        if (target.TranslatePoint(default, _overlay) is not { } origin) return;
        Canvas.SetLeft(_dropMark, origin.X);
        Canvas.SetTop(_dropMark, origin.Y);
        _dropMark.Width = target.Bounds.Width;
        _dropMark.Height = target.Bounds.Height;
        _dropMark.CornerRadius = target is Border { CornerRadius: var r } ? r : new CornerRadius(12);
    }

    private void StartGhost(string? key, string? label = null)
    {
        var name = label ?? _roster.Find(key!)?.Session.DisplayName ?? key;
        _ghost = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(12, 5), BorderThickness = new Thickness(1),
            BorderBrush = _p.Brand, Background = _p.Raised2, Opacity = 0.95,
            Child = new TextBlock { Text = name, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = _p.Text },
        };
        _overlay.Children.Add(_ghost);
    }

    // Moves the ghost to the pointer and marks what's under it: a session drag targets the active tab's regions or
    // any tab header; a tab drag, another tab header.
    private void MoveGhost(Point at)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost, at.Y + 8);
        _dropSlot = -1;
        _dropTab = TabAt(at);
        if (_tabPress is { } tp && _dropTab == tp.TabId) _dropTab = null;
        if (_dropTab is { } tabId) MarkDrop(_tabViews[tabId].Button);
        else if (_press is not null)
            for (int i = 0; i < _slotRegions.Count; i++)
            {
                var host = _cells[i].Host;
                if (!host.IsVisible || host.TranslatePoint(default, _overlay) is not { } origin) continue;
                if (!new Rect(origin, host.Bounds.Size).Contains(at)) continue;
                _dropSlot = i;
                MarkDrop(host);
                _dropMark.CornerRadius = new CornerRadius(12);
                break;
            }
        _dropMark.IsVisible = _dropSlot >= 0 || _dropTab is not null;
    }

    private void EndDrag(bool drop)
    {
        var key = _press?.Key;
        var movingTab = _tabPress?.TabId;
        int slot = _dropSlot;
        var tabId = _dropTab;
        bool dragged = _ghost is not null;
        _press = null;
        _tabPress = null;
        if (_ghost is not null) _overlay.Children.Remove(_ghost);
        _ghost = null;
        _dropMark.IsVisible = false;
        _dropSlot = -1;
        _dropTab = null;
        if (drop && dragged && movingTab is not null && tabId is not null) MoveTabTo(movingTab, tabId);
        else if (drop && dragged && key is not null && tabId is not null) DropOnTab(key, tabId);
        else if (drop && dragged && key is not null && slot >= 0) PlacePane(key, slot);
        else { RefreshRail(); RefreshTabStrip(); }
    }

    // A session dropped on a tab header goes into that tab (its first empty region, else a swap) without switching
    // to it — like dropping a file on a folder. Dropped on the active tab, it's focused there.
    private void DropOnTab(string key, string tabId)
    {
        if (_roster.Find(key) is null || _tabs.DropOnTab(tabId, key) is null) return;
        _everPlaced.Add(key);
        if (tabId == _tabs.ActiveId) FocusPane(key);
        Refresh();
    }

    // A tab dragged onto another's header takes its place (onto Focus = first).
    private void MoveTabTo(string tabId, string ontoId)
    {
        int to = ontoId == RoostTabSet.FocusId ? 0 : _tabs.Tabs.ToList().FindIndex(t => t.Id == ontoId);
        if (to >= 0 && _tabs.MoveTab(tabId, to)) Refresh();
        else RefreshTabStrip();
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
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) onClick(); };
        return b;
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
