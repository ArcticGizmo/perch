using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;
using Perch.Data.Roost;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The Roost (docs/roost-plan.md): a tmux-style view of every live session — an urgency-ranked rail on the left
/// (Needs you → Done · review → Working → Quiet) and a stage of <see cref="SessionPane"/>s on the right, laid out
/// by the pure <see cref="RoostStage"/> / <see cref="RoostTemplates"/> / <see cref="RoostLayout"/> rules. Pane order
/// is the user's (first-seen, rearranged by dragging headers; never reordered by a status flip). The session list
/// itself is the app-owned <see cref="RoostRoster"/>, so pins and order survive closing the window; this window
/// owns only the per-pane feeds (<see cref="RoostFeed"/>: a Perch session's in-memory conversation, or a tailed
/// transcript), which live only while it's open.
///
/// <para>Three layouts (a persisted title-bar toggle): <b>Tiled</b> — up to six panes on stage, each a full thread,
/// in a grid shaped for how many there are (the title bar's snap flyout picks the shape per count); the rest wait
/// in the rail; <b>Main + stack</b> — the focused pane large and expanded, every other pane a mini card in the
/// stack; <b>Zoom</b> — the focused pane alone. Only placed panes hold controls: anything off-screen is parked (no
/// materialised thread). The
/// visible layout is rebuilt only when its shape changes; a scan that only moves text refreshes the panes in
/// place, so an expanded thread keeps its scroll. The title-bar chips double as group filters.</para>
/// </summary>
internal sealed class RoostWindow : Window
{
    private const double StagePad = 14, CellGap = 14;

    private readonly RoostRoster _roster;
    private readonly Func<RoostPane, RoostFeed?> _feedFactory;
    private readonly SessionPalette _p;

    private readonly Dictionary<string, RoostFeed?> _feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionPane> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoostPaneSize> _placed = new(StringComparer.Ordinal);

    private readonly StackPanel _chips;
    private readonly Dictionary<RoostLayoutMode, Border> _segments = new();
    private readonly StackPanel _rail;
    private readonly Panel _stage;
    // Tiled
    private readonly RoostStage _onStage = new();
    private readonly RoostTilePanel _grid;
    // Empty per-cell borders at each slot's position: what a drag hit-tests against. The panes themselves sit in
    // _paneHosts, one per pane on stage, moved between cells by their grid position only — taking a pane out of
    // the tree and back in would restyle and re-measure its whole thread.
    private readonly Border[] _cellHosts = new Border[RoostTemplates.AutoMaxCells];
    private readonly Dictionary<string, Border> _paneHosts = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _cellKeys = [];
    // Main + stack
    private readonly Grid _mainGrid;
    private readonly Border _mainHost;
    private readonly StackPanel _stack;
    // Zoom
    private readonly Border _zoomHost;
    private readonly Border _emptyNote;
    private readonly TextBlock _emptyTitle, _emptyBody;
    private readonly Border _offPill;
    private readonly TextBlock _offPillText;
    private readonly Ellipse _offPillDot;
    // Drag a pane by its header (or a rail row) onto a cell: the ghost chip and the drop-cell mark ride an overlay.
    private readonly Canvas _overlay;
    private readonly Border _dropMark;
    private (string Key, Point Start, Control Source)? _press;
    private Border? _ghost;
    private int _dropSlot = -1;

    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _holdTimer;   // re-runs layout when a typing hold lapses
    private readonly TypingHold _typing = new();

    // What the stage containers currently hold, so a layout pass only touches what changed — re-parenting a
    // pane drops keyboard focus, which must never happen to the composer being typed in.
    private RoostLayoutMode? _builtMode;

    private RoostLayoutMode _mode;
    private RoostLayoutMode _preZoomMode = RoostLayoutMode.Tiled;
    private readonly Dictionary<int, RoostSnapTemplate> _layoutByCount;
    private RoostSnapTemplate _drawnTemplate = RoostSnapTemplate.Full;
    private int _drawnCount = -1;
    private Border? _snapButton;
    private readonly DispatcherTimer _snapHintTimer;
    private RoostGroup? _filter;
    private string? _focused;
    // "+ New session" was clicked: the first new Perch pane to appear before the deadline goes on stage.
    private HashSet<string>? _keysAtNewSession;
    private DateTime _newSessionUntil;

    public RoostWindow(RoostRoster roster, Func<RoostPane, RoostFeed?> feedFactory, SessionPalette? palette = null,
        RoostLayoutMode layout = RoostLayoutMode.Tiled, IReadOnlyDictionary<int, RoostSnapTemplate>? layoutByCount = null)
    {
        _roster = roster;
        _feedFactory = feedFactory;
        _p = palette ?? SessionPalette.Current;
        _mode = layout;
        _layoutByCount = layoutByCount is null ? new() : new(layoutByCount);

        Title = "Roost";
        Width = 1280;
        Height = 800;
        MinWidth = 760;
        MinHeight = 480;
        Background = _p.Ground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Title bar: name · summary chips (filters) ········ layout toggle · + New session ──
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                RoostGlyph(_p.Brand, 18),
                new TextBlock { Text = "Roost", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = _p.Title, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var newSession = BarButton("+ New session", StartNewSession);
        newSession.Margin = new Thickness(10, 0, 0, 0);
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Children = { SnapButton(), SegmentedToggle(), newSession },
        };
        var bar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10), [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { right, new StackPanel { Orientation = Orientation.Horizontal, Children = { title, _chips } } },
            },
        };

        // ── Rail ──
        _rail = new StackPanel { Spacing = 14, Margin = new Thickness(8, 12) };
        var railHost = new Border
        {
            Width = 216, Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 1, 0),
            [DockPanel.DockProperty] = Dock.Left,
            Child = new ScrollViewer { HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _rail },
        };

        // ── Stage: one container per layout, only the active one visible ──
        // Each cell host carries half a gap on every side, so the grid's own margin is the pad less that half.
        _grid = new RoostTilePanel { Margin = new Thickness(StagePad - CellGap / 2) };
        for (int i = 0; i < _cellHosts.Length; i++)
        {
            _cellHosts[i] = new Border { Margin = new Thickness(CellGap / 2) };
            _grid.Children.Add(_cellHosts[i]);
        }

        _mainHost = new Border { Margin = new Thickness(0, 0, CellGap / 2, 0), [Grid.ColumnProperty] = 0 };
        _stack = new StackPanel { Spacing = CellGap, Margin = new Thickness(CellGap / 2, 0, 0, 0) };
        _mainGrid = new Grid
        {
            Margin = new Thickness(StagePad), ColumnDefinitions = new ColumnDefinitions("1.75*,*"), IsVisible = false,
            Children =
            {
                _mainHost,
                new ScrollViewer
                {
                    [Grid.ColumnProperty] = 1,
                    HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = _stack,
                },
            },
        };
        _zoomHost = new Border { Margin = new Thickness(StagePad), IsVisible = false };

        _emptyTitle = new TextBlock { FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 16, Foreground = _p.Title, HorizontalAlignment = HorizontalAlignment.Center };
        _emptyBody = new TextBlock { FontFamily = _p.Body, FontSize = 13, Foreground = _p.Muted, HorizontalAlignment = HorizontalAlignment.Center };
        _emptyNote = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Child = new StackPanel { Spacing = 6, Children = { _emptyTitle, _emptyBody } },
        };
        _stage = new Panel { Background = _p.Ground, Children = { _grid, _mainGrid, _zoomHost, _emptyNote } };
        _stage.SizeChanged += (_, _) => Refresh();   // the stage's shape can change the default layout

        // ── Bottom bar: key hints · the "N off stage" pill (its own band, so it never covers a pane) ──
        (_offPill, _offPillText, _offPillDot) = OverflowPill();
        _offPill[ToolTip.TipProperty] = "Waiting in the rail — click to bring the most urgent on stage";
        _offPill.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var visible = _cellKeys.ToHashSet(StringComparer.Ordinal);
            var next = ShownPanes().Where(p => !visible.Contains(p.Key)).OrderBy(RoostStage.Urgency).FirstOrDefault();
            if (next is not null) FocusPane(next.Key);
        };
        var hints = new TextBlock
        {
            Text = "Ctrl+1–9 pane  ·  Ctrl+. next needing you  ·  drag a header to move  ·  double-click a header to zoom  ·  Ctrl+Shift+E keep on stage",
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { _offPill },
            [DockPanel.DockProperty] = Dock.Right,
        };
        var bottomBar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 5), MinHeight = 36, [DockPanel.DockProperty] = Dock.Bottom,
            Child = new DockPanel { LastChildFill = true, Children = { pills, hints } },
        };
        var stageColumn = new DockPanel { LastChildFill = true, Children = { bottomBar, _stage } };

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
        _clockTimer.Tick += (_, _) => { foreach (var k in _placed.Keys) _views[k].Tick(); RefreshRail(); };
        _clockTimer.Start();
        _holdTimer = new DispatcherTimer();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); Refresh(); };
        _snapHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _snapHintTimer.Tick += (_, _) => { _snapHintTimer.Stop(); RefreshSnapButton(); };

        Closed += (_, _) =>
        {
            _pulseTimer.Stop();
            _clockTimer.Stop();
            _holdTimer.Stop();
            _snapHintTimer.Stop();
            foreach (var v in _views.Values) v.Park();
            foreach (var f in _feeds.Values) f?.Dispose();
            _feeds.Clear();
        };

        RefreshSegments();
        Refresh();
    }

    /// <summary>"+ New session" in the title bar.</summary>
    public event Action? NewSessionRequested;

    /// <summary>A pane asked to open its session (the Perch window, or focus the terminal).</summary>
    public event Action<ClaudeSession>? OpenSessionRequested;

    /// <summary>A done-review pane was focused — acknowledge it (by pid), as clicking its overlay row does.</summary>
    public event Action<string>? AcknowledgeRequested;

    /// <summary>The layout toggle moved (persist it).</summary>
    public event Action<RoostLayoutMode>? LayoutChanged;

    /// <summary>A layout was picked for a stage count (persist the whole map).</summary>
    public event Action<IReadOnlyDictionary<int, RoostSnapTemplate>>? LayoutByCountChanged;

    /// <summary>A drag rearranged the panes — persist <see cref="RoostRoster.Order"/>.</summary>
    public event Action? OrderChanged;

    /// <summary>A permission card in a Perch pane was answered: (session id, item, allow, switch mode).</summary>
    public event Action<string, PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>Esc on a focused Perch pane with a turn running: interrupt it (session id).</summary>
    public event Action<string>? InterruptRequested;

    /// <summary>A Perch pane's composer sent a reply: (session id, text).</summary>
    public event Action<string, string>? PromptSubmitted;

    /// <summary>A pane was closed or reopened — persist <see cref="RoostRoster.ClosedKeys"/>.</summary>
    public event Action? ClosedPanesChanged;

    /// <summary>"Take over in Perch" on a terminal pane (the app confirms before stopping anything).</summary>
    public event Action<ClaudeSession>? TakeOverRequested;

    /// <summary>Which sessions offer "Take over in Perch" — the app supplies the overlay's Elevate rule. Null =
    /// none.</summary>
    public Func<ClaudeSession, bool>? CanTakeOver { get; set; }

    /// <summary>A question card in a Perch pane was answered: (session id, item, answers).</summary>
    public event Action<string, PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    public RoostLayoutMode Mode => _mode;

    /// <summary>Whether the session's pane is on screen right now — placed by the current layout, expanded or as
    /// a mini card (not paged, filtered or zoomed away). With <see cref="Window.IsActive"/> it's the Roost half of
    /// <see cref="AttentionSeen"/>.</summary>
    public bool IsOnScreen(string sessionId) =>
        _roster.Panes.FirstOrDefault(p => p.Session.SessionId == sessionId) is { } pane && _placed.ContainsKey(pane.Key);

    /// <summary>The roster changed (a monitor scan landed) — re-sync feeds, panes and layout, and flash the
    /// taskbar when a session newly needs the user while the Roost isn't the active window.</summary>
    public void RosterChanged()
    {
        Refresh();
        if (_roster.NeedsYouArrivals.Count > 0 && !IsActive && TryGetPlatformHandle() is { } handle)
            PlatformServices.WindowChrome.FlashTaskbar(handle.Handle);
    }

    /// <summary>Focuses a pane: brings it into view (Tiled puts it on stage in the weakest pane's cell if it's
    /// waiting in the rail, Zoom shows it, Main + stack makes it main), and acknowledges a done-review session.</summary>
    public void FocusPane(string key)
    {
        if (_roster.Find(key) is not { } pane) return;
        if (_filter is { } f && pane.Group != f) _filter = null;   // focusing something filtered out lifts the filter
        if (_filter is null) _onStage.Bring(key, _roster.Panes, Guard());   // the old focus is spared
        _focused = key;
        if (pane is { Ended: false, Session.Status: SessionStatus.NeedsAttention })
            AcknowledgeRequested?.Invoke(pane.Session.Pid);
        Refresh();
    }

    /// <summary>Switches layout (the toggle, Ctrl+Shift+Z).</summary>
    public void SetMode(RoostLayoutMode mode)
    {
        if (mode == _mode) return;
        if (mode == RoostLayoutMode.Zoom) _preZoomMode = _mode;
        _mode = mode;
        RefreshSegments();
        LayoutChanged?.Invoke(mode);
        Refresh();
    }

    /// <summary>Picks the layout for <paramref name="count"/> panes on stage (Auto clears the pick). Switches to
    /// Tiled, since the layouts are Tiled's.</summary>
    public void SetLayoutFor(int count, RoostSnapTemplate template)
    {
        bool changed = template == RoostSnapTemplate.Auto
            ? _layoutByCount.Remove(count)
            : !_layoutByCount.TryGetValue(count, out var had) || had != template;
        if (template != RoostSnapTemplate.Auto) _layoutByCount[count] = template;
        if (changed) LayoutByCountChanged?.Invoke(new Dictionary<int, RoostSnapTemplate>(_layoutByCount));
        if (_mode != RoostLayoutMode.Tiled) { SetMode(RoostLayoutMode.Tiled); return; }
        if (changed) Refresh();
    }

    /// <summary>Puts pane <paramref name="key"/> in cell <paramref name="slot"/> (a drag, or a flyout cell): a pane on
    /// stage swaps cells with the one there, a pane from the rail takes the cell and sends its occupant to the
    /// rail. The two also swap places in the saved order.</summary>
    public void PlacePane(string key, int slot)
    {
        if (_roster.Find(key) is null) return;
        _filter = null;
        if (_mode != RoostLayoutMode.Tiled) SetMode(RoostLayoutMode.Tiled);
        if (slot < _onStage.Slots.Count && _onStage.Slots[slot] == key) { FocusPane(key); return; }   // its own cell
        var displaced = _onStage.Place(key, slot, _roster.Panes);
        if (displaced is not null) _roster.Swap(key, displaced);
        else _roster.MoveToEnd(key);
        OrderChanged?.Invoke();
        _focused = key;
        Refresh();
    }

    // "+ New session": remember what's here, so the Perch session it starts can be put on stage when it appears.
    private void StartNewSession()
    {
        _keysAtNewSession = _roster.Panes.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        _newSessionUntil = Clock.Now.AddMinutes(1);
        NewSessionRequested?.Invoke();
    }

    /// <summary>HeadlessRenderer hook: "+ New session" clicked.</summary>
    internal void StartNewSessionForRender() => StartNewSession();

    /// <summary>HeadlessRenderer hook: whether pane <paramref name="key"/> is placed right now.</summary>
    internal bool IsOnScreenKey(string key) => _placed.ContainsKey(key);

    /// <summary>HeadlessRenderer hook: a drag of pane <paramref name="key"/> hovering cell <paramref name="slot"/>
    /// (the ghost at that cell's centre). <see cref="DropForRender"/> lets go.</summary>
    internal void DragForRender(string key, int slot)
    {
        _press = (key, default, this);
        StartGhost(key);
        if (slot < 0 || slot >= _cellHosts.Length || !_cellHosts[slot].IsVisible) return;
        var host = _cellHosts[slot];
        if (host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), _overlay) is { } at) MoveGhost(at);
    }

    internal void DropForRender() => EndDrag(drop: true);

    /// <summary>HeadlessRenderer hook: open the snap-layout flyout.</summary>
    internal void OpenSnapFlyoutForRender() { if (_snapButton is { } b) ShowSnapFlyout(b); }

    /// <summary>HeadlessRenderer hook: pretend the user is mid-reply in pane <paramref name="key"/> (the typing hold).</summary>
    internal void TypeForRender(string key) => _typing.Keystroke(key, Clock.Now);

    /// <summary>HeadlessRenderer hook: close a pane as its menu would.</summary>
    internal void ClosePaneForRender(string key) => OnPaneAction(key, RoostPaneAction.Close);

    /// <summary>HeadlessRenderer hook: apply a chip filter (null clears).</summary>
    internal void FilterForRender(RoostGroup? group) { _filter = group; Refresh(); }

    // ── Layout pass ───────────────────────────────────────────────────────────

    private void Refresh()
    {
        var all = _roster.Panes;
        SyncFeedsAndViews(all);
        var shown = _filter is { } g ? all.Where(p => p.Group == g).ToList() : all;
        if (_filter is not null && shown.Count == 0) { _filter = null; shown = all; }   // the group emptied

        _placed.Clear();
        switch (_mode)
        {
            case RoostLayoutMode.MainStack: LayoutMainStack(shown); break;
            case RoostLayoutMode.Zoom: LayoutZoom(shown); break;
            default: LayoutTiled(all, shown); break;
        }
        _grid.IsVisible = _mode == RoostLayoutMode.Tiled;
        _mainGrid.IsVisible = _mode == RoostLayoutMode.MainStack;
        _zoomHost.IsVisible = _mode == RoostLayoutMode.Zoom;

        foreach (var pane in all)
        {
            var view = _views[pane.Key];
            view.CanTakeOver = !pane.Ended && CanTakeOver?.Invoke(pane.Session) == true;
            view.Update(pane, _feeds[pane.Key]);
            view.SetFocused(pane.Key == _focused);
            if (_placed.TryGetValue(pane.Key, out var size)) view.SetSize(size, held: false);
            else view.Park();
        }

        _emptyNote.IsVisible = all.Count == 0;
        _emptyTitle.Text = "No live sessions";
        _emptyBody.Text = "Sessions appear here as soon as they start — or start one with + New session.";
        RefreshChips();
        RefreshRail();
        RefreshSnapButton();
        UpdatePulse();
        ArmHoldTimer();
    }

    // While typing holds the stage, wake when the hold lapses so a waiting pane then goes on stage.
    private void ArmHoldTimer()
    {
        _holdTimer.Stop();
        if (_mode != RoostLayoutMode.Tiled || _typing.ReleasesAt(Clock.Now) is not { } at) return;
        var wait = at - Clock.Now;
        _holdTimer.Interval = wait > TimeSpan.Zero ? wait + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50);
        _holdTimer.Start();
    }

    private void OnComposerTyping(string key)
    {
        _typing.Keystroke(key, Clock.Now);
        ArmHoldTimer();   // keep pushing the release out while the typing continues
    }

    private void OnComposerFocusChanged(string key, bool focused)
    {
        if (focused) { _typing.Focused(key); return; }
        _typing.Blurred(key);
        Refresh();   // leaving the composer releases the hold at once
    }

    private void OnPromptSubmitted(string key, string text)
    {
        if (_roster.Find(key) is not { Ended: false } pane) return;
        PromptSubmitted?.Invoke(pane.Session.SessionId, text);
        _typing.Sent(key);
        Refresh();
    }

    private RoostStageGuard Guard() => new(_focused, _typing.TypingIn(Clock.Now));

    private void LayoutTiled(IReadOnlyList<RoostPane> all, IReadOnlyList<RoostPane> shown)
    {
        IReadOnlyList<string> keys;
        if (_filter is null)
        {
            var guard = Guard();
            _onStage.Sync(all, guard, hold: guard.TypingIn is not null);
            AdmitStartedSession(all, guard);
            keys = _onStage.Slots;
        }
        else keys = RoostStage.Strict(shown);   // a filter shows its group without touching the sticky places

        int count = Math.Max(1, keys.Count);
        var template = RoostTemplates.For(count, _layoutByCount, StageAspect());
        if (_drawnCount >= 0 && count != _drawnCount) FlashSnapHint();
        _drawnCount = count;
        _drawnTemplate = template;
        EnsureBuiltFor(RoostLayoutMode.Tiled);
        var shape = RoostTemplates.Shape(template);
        ApplyGridShape(shape);

        // Panes leaving the stage leave the tree; a pane that stays keeps its host and only moves (so a swap,
        // or the layout changing shape, never re-parents it — it keeps its focus, scroll and measured thread).
        var onStage = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _paneHosts.Keys.Where(k => !onStage.Contains(k)).ToList()) RemovePaneHost(gone);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!_paneHosts.TryGetValue(keys[i], out var host))
            {
                host = new Border { Margin = new Thickness(CellGap / 2), Child = _views[keys[i]] };
                _paneHosts[keys[i]] = host;
                _grid.Children.Add(host);
            }
            if (RoostTilePanel.GetSlot(host) != i) RoostTilePanel.SetSlot(host, i);
        }
        _cellKeys = keys.ToList();
        foreach (var k in keys) _placed[k] = RoostPaneSize.Expanded;
        RefreshOffStage(shown);
    }

    // A session the user started with "+ New session" goes on stage when it first appears, even over a full one.
    private void AdmitStartedSession(IReadOnlyList<RoostPane> all, RoostStageGuard guard)
    {
        if (_keysAtNewSession is not { } before) return;
        if (Clock.Now > _newSessionUntil) { _keysAtNewSession = null; return; }
        if (all.FirstOrDefault(p => p.Session.IsPerchControlled && !p.Ended && !before.Contains(p.Key)) is not { } started) return;
        _keysAtNewSession = null;
        _onStage.Bring(started.Key, all, guard);
    }

    private void LayoutMainStack(IReadOnlyList<RoostPane> shown)
    {
        var (main, stack) = RoostLayout.MainStack(shown.Select(p => p.Key).ToList(), _focused);
        EnsureBuiltFor(RoostLayoutMode.MainStack);
        var mainView = main is null ? null : _views[main];
        bool stackChanged = !_stack.Children.Cast<SessionPane>().Select(v => v.Key).SequenceEqual(stack);
        if (stackChanged) _stack.Children.Clear();                       // first, in case main came out of it
        if (!ReferenceEquals(_mainHost.Child, mainView)) _mainHost.Child = null;
        if (stackChanged) foreach (var k in stack) _stack.Children.Add(_views[k]);
        if (_mainHost.Child is null && mainView is not null) _mainHost.Child = mainView;
        if (main is not null) _placed[main] = RoostPaneSize.Expanded;
        foreach (var k in stack) _placed[k] = RoostPaneSize.Collapsed;
        HideOverflow();
    }

    private void LayoutZoom(IReadOnlyList<RoostPane> shown)
    {
        var target = RoostLayout.ZoomTarget(shown.Select(p => p.Key).ToList(), _focused);
        EnsureBuiltFor(RoostLayoutMode.Zoom);
        var view = target is null ? null : _views[target];
        if (!ReferenceEquals(_zoomHost.Child, view)) _zoomHost.Child = view;
        if (target is not null) _placed[target] = RoostPaneSize.Expanded;
        HideOverflow();
    }

    // A layout switch empties every container, so the new layout can re-parent the panes it places.
    private void EnsureBuiltFor(RoostLayoutMode mode)
    {
        if (_builtMode == mode) return;
        _builtMode = mode;
        foreach (var key in _paneHosts.Keys.ToList()) RemovePaneHost(key);
        _mainHost.Child = null;
        _stack.Children.Clear();
        _zoomHost.Child = null;
    }

    private void RemovePaneHost(string key)
    {
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
            var dropped = _views[gone];
            dropped.Park();
            _views.Remove(gone);
            if (_feeds.Remove(gone, out var f)) f?.Dispose();
            if (_focused == gone) _focused = null;
            // A dropped pane still sitting in a Tiled host, Zoom or Main (the stack diff drops it on its own,
            // since its key leaves the stack's signature).
            RemovePaneHost(gone);
            if (ReferenceEquals(_zoomHost.Child, dropped)) _zoomHost.Child = null;
            if (ReferenceEquals(_mainHost.Child, dropped)) _mainHost.Child = null;
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
                view.ComposerTyping += OnComposerTyping;
                view.ComposerFocusChanged += OnComposerFocusChanged;
                AttachDragSource(view.Header, key);
                _views[pane.Key] = view;
            }
        }
    }

    private double StageAspect()
    {
        var b = _stage.Bounds;
        return b.Width > 0 && b.Height > 0 ? b.Width / b.Height : 1.6;
    }

    // Lays the stage out for a template, with each cell border at its slot (spare ones hidden).
    private void ApplyGridShape(RoostTemplateShape shape)
    {
        _grid.Shape = shape;
        for (int i = 0; i < _cellHosts.Length; i++)
        {
            _cellHosts[i].IsVisible = i < shape.Slots.Count;
            RoostTilePanel.SetSlot(_cellHosts[i], i);
        }
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void OnChordKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control), shift = mods.HasFlag(KeyModifiers.Shift);
        if (!ctrl) return;

        if (!shift && e.Key is >= Key.D1 and <= Key.D9)
        {
            var shown = ShownPanes();
            int index = e.Key - Key.D1;
            if (index < shown.Count) FocusPane(shown[index].Key);
            e.Handled = true;
        }
        else if (!shift && e.Key == Key.OemPeriod)
        {
            if (_roster.NextNeedingYou(_focused) is { } next) FocusPane(next);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.E)
        {
            if (_focused is { } key) OnPaneAction(key, RoostPaneAction.KeepOnStage);
            e.Handled = true;
        }
        else if (shift && e.Key == Key.Z)
        {
            SetMode(_mode == RoostLayoutMode.Zoom ? _preZoomMode : RoostLayoutMode.Zoom);
            e.Handled = true;
        }
    }

    // Bubbling keys — anything a focused control (a thread, a composer) didn't already take.
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape && _ghost is not null) { EndDrag(drop: false); e.Handled = true; return; }
        if (e.Key is Key.Enter or Key.Escape) e.Handled = FocusedPerchKey(e.Key);
    }

    // The focused Perch pane's permission keys, as in SessionWindow (and the TUI): Enter allows a pending
    // permission (a question card is answered by picking, never a bare Enter); Esc denies it, or with nothing
    // pending interrupts a running turn. Terminal/IDE panes have no control channel, so these do nothing there.
    private bool FocusedPerchKey(Key key)
    {
        if (_focused is not { } k || _roster.Find(k) is not { Ended: false } pane) return false;
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

    private List<RoostPane> ShownPanes() =>
        _filter is { } g ? _roster.Panes.Where(p => p.Group == g).ToList() : _roster.Panes.ToList();

    private void OnPaneAction(string key, RoostPaneAction action)
    {
        if (_roster.Find(key) is not { } pane) return;
        switch (action)
        {
            case RoostPaneAction.KeepOnStage:
                bool keep = pane.Pin != RoostPin.Expanded;
                _roster.SetPin(key, keep ? RoostPin.Expanded : RoostPin.Auto);
                if (keep) FocusPane(key);   // pinning a pane from the rail brings it on stage
                else Refresh();
                break;
            case RoostPaneAction.Zoom:
                _focused = key;
                if (_mode == RoostLayoutMode.Zoom) SetMode(_preZoomMode);
                else SetMode(RoostLayoutMode.Zoom);
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
                if (_roster.Close(key)) { ClosedPanesChanged?.Invoke(); Refresh(); }
                break;
        }
    }

    private async Task CopyAsync(string text)
    {
        try { if (Clipboard is { } clip) await clip.SetTextAsync(text); }
        catch { /* best-effort */ }
    }

    // ── Pulse ─────────────────────────────────────────────────────────────────

    private void UpdatePulse()
    {
        bool any = _placed.Keys.Any(k => _views[k].Pulsing);
        bool still = Pulse.ReduceMotion;
        if (any && still) foreach (var k in _placed.Keys) _views[k].PulseTick(1, reduceMotion: true);
        if (any && !still) { if (!_pulseTimer.IsEnabled) _pulseTimer.Start(); }
        else if (_pulseTimer.IsEnabled) _pulseTimer.Stop();
    }

    private void PulseFrame()
    {
        double i = Pulse.Intensity(2200);
        bool still = Pulse.ReduceMotion;
        foreach (var k in _placed.Keys) _views[k].PulseTick(i, still);
        if (still) _pulseTimer.Stop();
    }

    // ── Chrome: layout toggle, overflow pills, chips, rail ────────────────────

    private Control SegmentedToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[] { (RoostLayoutMode.Tiled, "Tiled"), (RoostLayoutMode.MainStack, "Main + stack"), (RoostLayoutMode.Zoom, "Zoom") })
        {
            var seg = new Border
            {
                Padding = new Thickness(11, 5), Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = _p.Border, BorderThickness = new Thickness(mode == RoostLayoutMode.Tiled ? 0 : 1, 0, 0, 0),
                Child = new TextBlock { Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12 },
            };
            seg.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) SetMode(mode); };
            _segments[mode] = seg;
            row.Children.Add(seg);
        }
        return new Border
        {
            CornerRadius = new CornerRadius(9), BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            Background = _p.Surface, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = row,
        };
    }

    // The snap-layout button: a thumbnail of the grid Tiled is drawing now. A click opens the template flyout.
    private Control SnapButton()
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 5), Margin = new Thickness(0, 0, 8, 0),
            BorderThickness = new Thickness(1), BorderBrush = _p.Border, Background = _p.Surface,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = "Snap layout",
        };
        b.PointerEntered += (_, _) => b.BorderBrush = _p.BrandLine;
        b.PointerExited += (_, _) => b.BorderBrush = _p.Border;
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowSnapFlyout(b); };
        _snapButton = b;
        return b;
    }

    // How many panes the stage holds — the count the flyout picks a layout for.
    private int StageCount() => _mode == RoostLayoutMode.Tiled && _drawnCount > 0
        ? _drawnCount
        : Math.Clamp(ShownPanes().Count, 1, RoostStage.Capacity);

    private void RefreshSnapButton()
    {
        if (_snapButton is null) return;
        bool tiled = _mode == RoostLayoutMode.Tiled;
        bool hint = _snapHintTimer.IsEnabled;
        var shown = tiled ? _drawnTemplate : RoostSnapTemplate.Full;
        _snapButton.BorderBrush = hint ? _p.Brand : _p.Border;
        _snapButton.Background = hint ? _p.BrandWash : _p.Surface;
        _snapButton.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                TemplateThumb(RoostTemplates.Shape(shown), 26, 16, tiled ? _p.Brand : _p.Muted, onSlot: null),
                new TextBlock
                {
                    Text = tiled ? $"{StageCount()} · {RoostTemplates.Name(shown)}" : "Layout",
                    FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = tiled ? _p.Text : _p.Muted,
                },
            },
        };
    }

    // The stage count changed and the layout with it: light the snap button up for a moment, so the switch is
    // noticed and the flyout (which picks for the new count) is one click away.
    private void FlashSnapHint()
    {
        _snapHintTimer.Stop();
        _snapHintTimer.Start();
    }

    // Windows-style snap flyout for the current stage count: a thumbnail per layout, those with too few cells
    // dimmed. A click on a thumbnail picks it for this count; a click on one of its cells also puts the focused
    // pane in that cell.
    private void ShowSnapFlyout(Control anchor)
    {
        var flyout = new Flyout { Placement = global::Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
        var grid = new global::Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        int count = StageCount();
        var picked = _layoutByCount.TryGetValue(count, out var pick) && RoostTemplates.Fits(pick, count) ? pick : RoostSnapTemplate.Auto;
        var fallback = RoostTemplates.ForCount(count, StageAspect());
        foreach (var template in RoostTemplates.Picker)
        {
            var t = template;
            bool on = t == picked, fits = RoostTemplates.Fits(t, count);
            var shape = RoostTemplates.Shape(t == RoostSnapTemplate.Auto ? fallback : t);
            var caption = new TextBlock
            {
                Text = t == RoostSnapTemplate.Auto ? "Default" : RoostTemplates.Name(t),
                FontFamily = _p.Body, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = on ? _p.Text : _p.Muted, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
            };
            var tile = new Border
            {
                Width = 84, Margin = new Thickness(4), Padding = new Thickness(6), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), BorderBrush = on ? _p.BrandLine : Brushes.Transparent,
                Background = on ? _p.BrandWash : Brushes.Transparent, Opacity = fits ? 1 : 0.35,
                Cursor = fits ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
                [ToolTip.TipProperty] = fits ? null : $"Fewer than {count} cells",
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        TemplateThumb(shape, 70, 44, _p.Muted, onSlot: fits ? slot =>
                        {
                            flyout.Hide();
                            SetLayoutFor(count, t);
                            if (_focused is { } key) PlacePane(key, slot);
                        } : null),
                        caption,
                    },
                },
            };
            if (fits)
            {
                if (!on)
                {
                    tile.PointerEntered += (_, _) => tile.Background = _p.Raised2;
                    tile.PointerExited += (_, _) => tile.Background = Brushes.Transparent;
                }
                tile.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
                    flyout.Hide();
                    SetLayoutFor(count, t);
                };
            }
            grid.Children.Add(tile);
        }
        flyout.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"Layout for {count} session{(count == 1 ? "" : "s")}"
                           + (_focused is null ? "" : " · click a cell to put the focused pane there"),
                    FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, Margin = new Thickness(4, 0),
                },
                grid,
            },
        };
        flyout.ShowAt(anchor);
    }

    // A template drawn small: one rounded cell per slot. With onSlot, each cell highlights on hover and clicks
    // report its index.
    private Control TemplateThumb(RoostTemplateShape shape, double width, double height, IBrush stroke, Action<int>? onSlot)
    {
        const double gap = 2;
        var grid = new Grid { Width = width, Height = height };
        foreach (var w in shape.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Star));
        foreach (var w in shape.Rows) grid.RowDefinitions.Add(new RowDefinition(w, GridUnitType.Star));
        for (int i = 0; i < shape.Slots.Count; i++)
        {
            var slot = shape.Slots[i];
            int index = i;
            var cell = new Border
            {
                Margin = new Thickness(gap / 2), CornerRadius = new CornerRadius(onSlot is null ? 2 : 3),
                BorderThickness = new Thickness(1.2), BorderBrush = stroke, Background = _p.Raised,
                [Grid.RowProperty] = slot.Row, [Grid.ColumnProperty] = slot.Column,
                [Grid.RowSpanProperty] = slot.RowSpan, [Grid.ColumnSpanProperty] = slot.ColumnSpan,
            };
            if (onSlot is not null)
            {
                cell.PointerEntered += (_, _) => { cell.Background = _p.Brand; cell.BorderBrush = _p.Brand; };
                cell.PointerExited += (_, _) => { cell.Background = _p.Raised; cell.BorderBrush = stroke; };
                cell.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left) return;
                    e.Handled = true;
                    onSlot(index);
                };
            }
            grid.Children.Add(cell);
        }
        return grid;
    }

    private void RefreshSegments()
    {
        foreach (var (mode, seg) in _segments)
        {
            bool on = mode == _mode;
            seg.Background = on ? _p.BrandWash : Brushes.Transparent;
            ((TextBlock)seg.Child!).Foreground = on ? _p.Text : _p.Muted;
        }
    }

    private (Border, TextBlock, Ellipse) OverflowPill()
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

    private void HideOverflow() => _offPill.IsVisible = false;

    // "N off stage · 1 needs you": the shown panes waiting in the rail.
    private void RefreshOffStage(IReadOnlyList<RoostPane> shown)
    {
        var visible = _cellKeys.ToHashSet(StringComparer.Ordinal);
        var off = shown.Where(p => !visible.Contains(p.Key)).ToList();
        int needs = off.Count(p => p.Group == RoostGroup.NeedsYou);
        _offPill.IsVisible = off.Count > 0;
        _offPillText.Text = needs > 0 ? $"{off.Count} off stage · {needs} need{(needs == 1 ? "s" : "")} you" : $"{off.Count} off stage";
        _offPillDot.IsVisible = needs > 0;
        _offPill.BorderBrush = needs > 0 ? _p.Await : _p.Border;
        _offPillText.Foreground = needs > 0 ? _p.Await : _p.Muted;
    }

    private void RefreshChips()
    {
        var c = _roster.Counts;
        _chips.Children.Clear();
        if (c.NeedsYou > 0) _chips.Children.Add(Chip($"{c.NeedsYou} need{(c.NeedsYou == 1 ? "s" : "")} you", _p.Await, RoostGroup.NeedsYou, strong: true));
        if (c.Working > 0) _chips.Children.Add(Chip($"{c.Working} working", _p.Ok, RoostGroup.Working, strong: false));
        if (c.DoneReview > 0) _chips.Children.Add(Chip($"{c.DoneReview} done", _p.Attn, RoostGroup.DoneReview, strong: false));
        if (c.Quiet > 0) _chips.Children.Add(Chip($"{c.Quiet} quiet", _p.Idle, RoostGroup.Quiet, strong: false));
        if (_roster.ClosedPanes.Count > 0) _chips.Children.Add(HiddenChip());
    }

    // "N hidden": closed panes whose sessions still run. A click lists them to reopen (one, or all).
    private Border HiddenChip()
    {
        var hidden = _roster.ClosedPanes;
        var chip = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(10, 3), BorderThickness = new Thickness(1),
            BorderBrush = _p.Border, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand), [ToolTip.TipProperty] = "Closed panes — click to reopen",
            Child = new TextBlock { Text = $"{hidden.Count} hidden", FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Faint },
        };
        chip.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            var flyout = new MenuFlyout { Placement = global::Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft };
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
            flyout.ShowAt(chip);
        };
        return chip;
    }

    private void Reopen(IReadOnlyList<string> keys)
    {
        bool changed = false;
        foreach (var k in keys) changed |= _roster.Reopen(k);
        if (!changed) return;
        ClosedPanesChanged?.Invoke();
        if (keys.Count == 1) FocusPane(keys[0]);
        else Refresh();
    }

    // A summary chip that filters the stage to its group (click again to clear).
    private Border Chip(string label, IBrush dot, RoostGroup group, bool strong)
    {
        bool on = _filter == group;
        var chip = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(10, 3), BorderThickness = new Thickness(1),
            BorderBrush = on ? _p.BrandLine : _p.Border, Background = on ? _p.BrandWash : _p.Surface,
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = on ? "Show every session" : "Show only these",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = strong || on ? _p.Text : _p.Muted },
                },
            },
        };
        chip.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            _filter = _filter == group ? null : group;
            Refresh();
        };
        return chip;
    }

    private void RefreshRail()
    {
        if (_press is not null) return;   // rebuilding would drop the row being pressed / dragged
        _rail.Children.Clear();
        if (_roster.Panes.Count == 0)
        {
            _rail.Children.Add(new TextBlock { Text = "No live sessions", Margin = new Thickness(8, 0), FontSize = 12, Foreground = _p.Faint });
            return;
        }
        foreach (var group in _roster.Rail)
        {
            if (group.Panes.Count == 0) continue;
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new DockPanel
            {
                Margin = new Thickness(8, 0, 8, 4),
                Children =
                {
                    new TextBlock { Text = group.Panes.Count.ToString(), FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Text = GroupTitle(group.Group), FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint },
                },
            });
            foreach (var pane in group.Panes) col.Children.Add(RailRow(pane));
            _rail.Children.Add(col);
        }
    }

    private static string GroupTitle(RoostGroup g) => g switch
    {
        RoostGroup.NeedsYou => "NEEDS YOU",
        RoostGroup.DoneReview => "DONE · REVIEW",
        RoostGroup.Working => "WORKING",
        _ => "QUIET",
    };

    private Control RailRow(RoostPane pane)
    {
        var s = pane.Session;
        bool on = pane.Key == _focused;
        var dot = new Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = pane.Ended ? _p.Faint : s.Status switch
            {
                SessionStatus.AwaitingInput => _p.Await,
                SessionStatus.ApiError => _p.Err,
                SessionStatus.NeedsAttention => _p.Attn,
                SessionStatus.Running => _p.Ok,
                _ => _p.Idle,
            },
        };
        string elapsed = pane.Ended ? "ended"
            : s.Status == SessionStatus.AwaitingInput ? s.AwaitingElapsedLabel() ?? ""
            : s.Status == SessionStatus.Running ? s.RunningElapsedLabel() ?? ""
            : "";
        // Tiled: a pane waiting off stage reads dimmer; one that hasn't been on stage yet is marked "new".
        bool offStage = _mode == RoostLayoutMode.Tiled && !_cellKeys.Contains(pane.Key);
        bool fresh = offStage && _filter is null && !pane.Ended && !_onStage.WasEverOn(pane.Key);
        var newTag = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(1), BorderBrush = _p.BrandLine, IsVisible = fresh,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Child = new TextBlock { Text = "new", FontFamily = _p.Body, FontSize = 10, Foreground = _p.Brand },
        };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6), BorderThickness = new Thickness(1),
            BorderBrush = on ? _p.BrandLine : Brushes.Transparent, Background = on ? _p.BrandWash : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Opacity = pane.Ended ? 0.6 : 1,
            [ToolTip.TipProperty] = offStage ? "Off stage — click to bring it on, or drag it onto a cell" : null,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Left, Children = { dot } },
                    new TextBlock { Text = elapsed, FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right },
                    newTag,
                    new TextBlock
                    {
                        Text = s.IsPerchControlled ? "◆" : "", FontSize = 9, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(5, 0, 0, 0), IsVisible = s.IsPerchControlled, [DockPanel.DockProperty] = Dock.Right,
                    },
                    new TextBlock
                    {
                        Text = s.DisplayName, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = offStage ? _p.Faint : _p.Text,
                        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                    },
                },
            },
        };
        if (!on)
        {
            row.PointerEntered += (_, _) => row.Background = _p.Raised2;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        }
        AttachDragSource(row, pane.Key);   // first, so a drop marks the release handled before the click sees it
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !e.Handled) FocusPane(pane.Key); };
        return row;
    }

    // ── Drag to rearrange ─────────────────────────────────────────────────────

    // Pressing a pane header (or a rail row) and moving a few pixels starts a drag in Tiled: a ghost chip follows
    // the pointer and the cell under it is marked; letting go there puts the pane in that cell (PlacePane).
    private void AttachDragSource(Control source, string key)
    {
        source.PointerPressed += (_, e) =>
        {
            if (_mode != RoostLayoutMode.Tiled || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
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

    private void StartGhost(string key)
    {
        var name = _roster.Find(key)?.Session.DisplayName ?? key;
        _ghost = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(12, 5), BorderThickness = new Thickness(1),
            BorderBrush = _p.Brand, Background = _p.Raised2, Opacity = 0.95,
            Child = new TextBlock { Text = name, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = _p.Text },
        };
        _overlay.Children.Add(_ghost);
    }

    // Moves the ghost to the pointer and marks the Tiled cell under it (an empty cell counts: dropping there
    // moves the pane to the end).
    private void MoveGhost(Point at)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost, at.Y + 8);
        _dropSlot = -1;
        for (int i = 0; i < _cellHosts.Length; i++)
        {
            var host = _cellHosts[i];
            if (!host.IsVisible || host.TranslatePoint(default, _overlay) is not { } origin) continue;
            var rect = new Rect(origin, host.Bounds.Size);
            if (!rect.Contains(at)) continue;
            _dropSlot = i;
            Canvas.SetLeft(_dropMark, rect.X);
            Canvas.SetTop(_dropMark, rect.Y);
            _dropMark.Width = rect.Width;
            _dropMark.Height = rect.Height;
            break;
        }
        _dropMark.IsVisible = _dropSlot >= 0;
    }

    private void EndDrag(bool drop)
    {
        var key = _press?.Key;
        int slot = _dropSlot;
        bool dragged = _ghost is not null;
        _press = null;
        if (_ghost is not null) _overlay.Children.Remove(_ghost);
        _ghost = null;
        _dropMark.IsVisible = false;
        _dropSlot = -1;
        if (drop && dragged && key is not null && slot >= 0) PlacePane(key, slot);
        else RefreshRail();
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
}
