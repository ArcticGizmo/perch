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
/// by the pure <see cref="RoostLayout"/> rules (first-seen order that never reorders on a status flip; panes
/// expanded or collapsed to mini cards by status, pin, and focus). The session list itself is the app-owned
/// <see cref="RoostRoster"/>, so pins and order survive closing the window; this window owns only the per-pane
/// feeds (<see cref="RoostFeed"/>: a Perch session's in-memory conversation, or a tailed transcript), which
/// live only while it's open.
///
/// <para>Three layouts (a persisted title-bar toggle): <b>Tiled</b> — a fixed 2×2 cell viewport paged a row at a
/// time (wheel / PageUp·PageDown / the ↑↓ pills; discrete paging gives the row snap for free); <b>Main + stack</b>
/// — the focused pane large and expanded, every other pane a mini card in the stack; <b>Zoom</b> — the focused
/// pane alone. Only placed panes hold controls: anything off-screen is parked (no materialised thread). The
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
    private readonly Dictionary<string, RoostPaneSize> _sizes = new(StringComparer.Ordinal);   // Tiled's resolved sizes
    private readonly Dictionary<string, (RoostPaneSize Size, bool Held)> _placed = new(StringComparer.Ordinal);

    private readonly StackPanel _chips;
    private readonly Dictionary<RoostLayoutMode, Border> _segments = new();
    private readonly StackPanel _rail;
    private readonly Panel _stage;
    // Tiled
    private readonly Grid _grid;
    private readonly Border[] _cellHosts = new Border[4];
    // Main + stack
    private readonly Grid _mainGrid;
    private readonly Border _mainHost;
    private readonly StackPanel _stack;
    // Zoom
    private readonly Border _zoomHost;
    private readonly Border _emptyNote;
    private readonly TextBlock _emptyTitle, _emptyBody;
    private readonly Border _pillUp, _pillDown;
    private readonly TextBlock _pillUpText, _pillDownText;
    private readonly Ellipse _pillDownDot;

    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _holdTimer;   // re-runs layout when a typing hold lapses
    private readonly TypingHold _typing = new();

    // What the stage containers currently hold, so a layout pass only touches what changed — re-parenting a
    // pane drops keyboard focus, which must never happen to the composer being typed in.
    private RoostLayoutMode? _builtMode;
    private readonly string?[] _cellSigs = new string?[4];

    private RoostLayoutMode _mode;
    private RoostLayoutMode _preZoomMode = RoostLayoutMode.Tiled;
    private RoostGroup? _filter;
    private string? _focused;
    private int _firstRow;
    private int _cellCount;
    private double _miniHeight;

    public RoostWindow(RoostRoster roster, Func<RoostPane, RoostFeed?> feedFactory, SessionPalette? palette = null,
        RoostLayoutMode layout = RoostLayoutMode.Tiled)
    {
        _roster = roster;
        _feedFactory = feedFactory;
        _p = palette ?? SessionPalette.Current;
        _mode = layout;

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
        var newSession = BarButton("+ New session", () => NewSessionRequested?.Invoke());
        newSession.Margin = new Thickness(10, 0, 0, 0);
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Children = { SegmentedToggle(), newSession },
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
        _grid = new Grid
        {
            Margin = new Thickness(StagePad),
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("*,*"),
        };
        for (int i = 0; i < 4; i++)
        {
            _cellHosts[i] = new Border
            {
                Margin = new Thickness(i % 2 == 0 ? 0 : CellGap / 2, i < 2 ? 0 : CellGap / 2, i % 2 == 0 ? CellGap / 2 : 0, i < 2 ? CellGap / 2 : 0),
                [Grid.RowProperty] = i / 2, [Grid.ColumnProperty] = i % 2,
            };
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
        _stage.PointerWheelChanged += (_, e) =>
        {
            if (_mode != RoostLayoutMode.Tiled || e.Delta.Y == 0) return;
            // A wheel over an expanded thread scrolls that thread; only the gaps between panes page.
            if (e.Source is Visual v && v.FindAncestorOfType<SessionThreadView>(includeSelf: true) is not null) return;
            Page(e.Delta.Y < 0 ? +1 : -1);
            e.Handled = true;
        };
        _stage.SizeChanged += (_, _) => { _miniHeight = 0; Refresh(); };

        // ── Bottom bar: key hints · the ↑/↓ overflow pills (their own band, so they never cover a pane) ──
        (_pillUp, _pillUpText, _) = OverflowPill();
        (_pillDown, _pillDownText, _pillDownDot) = OverflowPill();
        _pillUp.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Page(-1); };
        _pillDown.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Page(+1); };
        var hints = new TextBlock
        {
            Text = "Ctrl+1–9 pane  ·  Ctrl+. next needing you  ·  Ctrl+Shift+E expand/collapse  ·  Ctrl+Shift+Z zoom  ·  PgUp/PgDn page",
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { _pillUp, _pillDown },
            [DockPanel.DockProperty] = Dock.Right,
        };
        var bottomBar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 5), MinHeight = 36, [DockPanel.DockProperty] = Dock.Bottom,
            Child = new DockPanel { LastChildFill = true, Children = { pills, hints } },
        };
        var stageColumn = new DockPanel { LastChildFill = true, Children = { bottomBar, _stage } };

        Content = new DockPanel { LastChildFill = true, Children = { bar, railHost, stageColumn } };

        // Window chords tunnel (so a focused thread can't swallow them); paging bubbles, so a focused thread's
        // own PageUp/PageDown still scroll it.
        AddHandler(KeyDownEvent, OnChordKeyDown, RoutingStrategies.Tunnel);
        KeyDown += OnPageKeyDown;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pulseTimer.Tick += (_, _) => PulseFrame();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { foreach (var k in _placed.Keys) _views[k].Tick(); RefreshRail(); };
        _clockTimer.Start();
        _holdTimer = new DispatcherTimer();
        _holdTimer.Tick += (_, _) => { _holdTimer.Stop(); Refresh(); };

        Closed += (_, _) =>
        {
            _pulseTimer.Stop();
            _clockTimer.Stop();
            _holdTimer.Stop();
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

    /// <summary>A permission card in a Perch pane was answered: (session id, item, allow, switch mode).</summary>
    public event Action<string, PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>Esc on a focused Perch pane with a turn running: interrupt it (session id).</summary>
    public event Action<string>? InterruptRequested;

    /// <summary>A Perch pane's composer sent a reply: (session id, text).</summary>
    public event Action<string, string>? PromptSubmitted;

    /// <summary>A question card in a Perch pane was answered: (session id, item, answers).</summary>
    public event Action<string, PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    public RoostLayoutMode Mode => _mode;

    /// <summary>The roster changed (a monitor scan landed) — re-sync feeds, panes and layout.</summary>
    public void RosterChanged() => Refresh();

    /// <summary>Focuses a pane: brings it into view (Tiled scrolls to it, Zoom shows it, Main + stack makes it
    /// main), keeps it expanded by the resolver's focus rule, and acknowledges a done-review session.</summary>
    public void FocusPane(string key)
    {
        if (_roster.Find(key) is not { } pane) return;
        if (_filter is { } f && pane.Group != f) _filter = null;   // focusing something filtered out lifts the filter
        _focused = key;
        if (pane is { Ended: false, Session.Status: SessionStatus.NeedsAttention })
            AcknowledgeRequested?.Invoke(pane.Session.Pid);
        Refresh(reveal: key);
    }

    /// <summary>Switches layout (the toggle, Ctrl+Shift+Z).</summary>
    public void SetMode(RoostLayoutMode mode)
    {
        if (mode == _mode) return;
        if (mode == RoostLayoutMode.Zoom) _preZoomMode = _mode;
        _mode = mode;
        RefreshSegments();
        LayoutChanged?.Invoke(mode);
        Refresh(reveal: _focused);
    }

    /// <summary>HeadlessRenderer hook: page the Tiled viewport (there's no wheel in a headless capture).</summary>
    internal void PageForRender(int rows) => Page(rows);

    /// <summary>HeadlessRenderer hook: pretend the user is mid-reply in pane <paramref name="key"/> (the typing hold).</summary>
    internal void TypeForRender(string key) => _typing.Keystroke(key, Clock.Now);

    /// <summary>HeadlessRenderer hook: apply a chip filter (null clears).</summary>
    internal void FilterForRender(RoostGroup? group) { _filter = group; _firstRow = 0; Refresh(); }

    // ── Layout pass ───────────────────────────────────────────────────────────

    private void Refresh(string? reveal = null)
    {
        var all = _roster.Panes;
        SyncFeedsAndViews(all);
        var shown = _filter is { } g ? all.Where(p => p.Group == g).ToList() : all;
        if (_filter is not null && shown.Count == 0) { _filter = null; shown = all; }   // the group emptied

        ResolveTiledSizes(all);
        _placed.Clear();
        switch (_mode)
        {
            case RoostLayoutMode.MainStack: LayoutMainStack(shown); break;
            case RoostLayoutMode.Zoom: LayoutZoom(shown); break;
            default: LayoutTiled(shown, reveal); break;
        }
        _grid.IsVisible = _mode == RoostLayoutMode.Tiled;
        _mainGrid.IsVisible = _mode == RoostLayoutMode.MainStack;
        _zoomHost.IsVisible = _mode == RoostLayoutMode.Zoom;

        foreach (var pane in all)
        {
            var view = _views[pane.Key];
            view.Update(pane, _feeds[pane.Key]);
            view.SetFocused(pane.Key == _focused);
            if (_placed.TryGetValue(pane.Key, out var place)) view.SetSize(place.Size, place.Held);
            else view.Park();
        }

        _emptyNote.IsVisible = all.Count == 0;
        _emptyTitle.Text = "No live sessions";
        _emptyBody.Text = "Sessions appear here as soon as they start — or start one with + New session.";
        RefreshChips();
        RefreshRail();
        UpdatePulse();
        ArmHoldTimer();
    }

    // While a pane is held back by the typing hold, wake when the hold lapses so it then expands.
    private void ArmHoldTimer()
    {
        _holdTimer.Stop();
        if (_held.Count == 0 || _typing.ReleasesAt(Clock.Now) is not { } at) return;
        var wait = at - Clock.Now;
        _holdTimer.Interval = wait > TimeSpan.Zero ? wait + TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(50);
        _holdTimer.Start();
    }

    private void OnComposerTyping(string key)
    {
        _typing.Keystroke(key, Clock.Now);
        if (_held.Count > 0) ArmHoldTimer();   // keep pushing the release out while the typing continues
    }

    private void OnComposerFocusChanged(string key, bool focused)
    {
        if (focused) { _typing.Focused(key); return; }
        _typing.Blurred(key);
        if (_held.Count > 0) Refresh();   // leaving the composer releases any held pane at once
    }

    private void OnPromptSubmitted(string key, string text)
    {
        if (_roster.Find(key) is not { Ended: false } pane) return;
        PromptSubmitted?.Invoke(pane.Session.SessionId, text);
        _typing.Sent(key);
        if (_held.Count > 0) Refresh();
    }

    // Every pane's Tiled size (kept even while another layout shows, as the resolver's "current" memory).
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private void ResolveTiledSizes(IReadOnlyList<RoostPane> panes)
    {
        _held.Clear();
        foreach (var pane in panes)
        {
            RoostPaneSize? current = _sizes.TryGetValue(pane.Key, out var c) ? c : null;
            var d = RoostLayout.ResolveSize(new RoostSizeInputs(
                pane.Pin, pane.Group, pane.Ended, pane.Key == _focused,
                TypingElsewhere: _typing.TypingElsewhere(pane.Key, Clock.Now), current));
            _sizes[pane.Key] = d.Size;
            if (d.Held) _held.Add(pane.Key);
        }
    }

    private void LayoutTiled(IReadOnlyList<RoostPane> shown, string? reveal)
    {
        var sized = shown.Select(p => (p.Key, _sizes[p.Key])).ToList();
        int capacity = RoostLayout.CellCapacity(CellHeight(), MiniHeight(), CellGap);
        var cells = RoostLayout.Pack(sized, capacity);
        _cellCount = cells.Count;
        if (reveal is not null) _firstRow = RoostLayout.ScrollToReveal(RoostLayout.CellOf(cells, reveal), _firstRow, cells.Count);
        _firstRow = RoostLayout.ClampFirstRow(_firstRow, cells.Count);

        int first = _firstRow * RoostLayout.Columns;
        var visibleCells = cells.Skip(first).Take(RoostLayout.Columns * RoostLayout.VisibleRows).ToList();
        EnsureBuiltFor(RoostLayoutMode.Tiled);

        // Only the cells whose contents changed are touched: detach them all first (a pane may be moving from
        // one changed cell to another), then fill them. An unchanged cell — say, the one holding the composer
        // being typed in — is never re-parented, so it keeps its focus and scroll.
        var sigs = new string[_cellHosts.Length];
        for (int i = 0; i < sigs.Length; i++)
            sigs[i] = i < visibleCells.Count
                ? (visibleCells[i].Expanded ? "E:" : "m:") + string.Join(",", visibleCells[i].Keys)
                : "";
        for (int i = 0; i < sigs.Length; i++)
            if (sigs[i] != _cellSigs[i]) DetachHost(_cellHosts[i]);
        for (int i = 0; i < sigs.Length; i++)
        {
            if (sigs[i] == _cellSigs[i]) continue;
            _cellSigs[i] = sigs[i];
            if (i >= visibleCells.Count) continue;
            var cell = visibleCells[i];
            if (cell.Expanded) _cellHosts[i].Child = _views[cell.Keys[0]];
            else
            {
                var stack = new StackPanel { Spacing = CellGap, VerticalAlignment = VerticalAlignment.Top };
                foreach (var k in cell.Keys) stack.Children.Add(_views[k]);
                _cellHosts[i].Child = stack;
            }
        }
        foreach (var cell in visibleCells)
            foreach (var k in cell.Keys)
                _placed[k] = (_sizes[k], _held.Contains(k));
        RefreshOverflow(cells);
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
        if (main is not null) _placed[main] = (RoostPaneSize.Expanded, false);
        foreach (var k in stack) _placed[k] = (RoostPaneSize.Collapsed, false);
        HideOverflow();
    }

    private void LayoutZoom(IReadOnlyList<RoostPane> shown)
    {
        var target = RoostLayout.ZoomTarget(shown.Select(p => p.Key).ToList(), _focused);
        EnsureBuiltFor(RoostLayoutMode.Zoom);
        var view = target is null ? null : _views[target];
        if (!ReferenceEquals(_zoomHost.Child, view)) _zoomHost.Child = view;
        if (target is not null) _placed[target] = (RoostPaneSize.Expanded, false);
        HideOverflow();
    }

    // A layout switch empties every container, so the new layout can re-parent the panes it places.
    private void EnsureBuiltFor(RoostLayoutMode mode)
    {
        if (_builtMode == mode) return;
        _builtMode = mode;
        foreach (var host in _cellHosts) DetachHost(host);
        Array.Clear(_cellSigs);
        _mainHost.Child = null;
        _stack.Children.Clear();
        _zoomHost.Child = null;
    }

    private static void DetachHost(Border host)
    {
        if (host.Child is Panel stack) stack.Children.Clear();
        host.Child = null;
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
            _sizes.Remove(gone);
            if (_feeds.Remove(gone, out var f)) f?.Dispose();
            if (_focused == gone) _focused = null;
            // A dropped pane still sitting in Zoom / Main (Tiled's per-cell diff and the stack diff drop it on
            // their own, since its key leaves their signatures).
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
                _views[pane.Key] = view;
            }
        }
    }

    private double CellHeight()
    {
        double h = _stage.Bounds.Height;
        return h <= 0 ? 600 : Math.Max(120, (h - 2 * StagePad - CellGap) / 2);
    }

    // A mini card's height, measured once per stage size from a probe pane (fonts decide it, never a guess).
    private double MiniHeight()
    {
        if (_miniHeight > 0) return _miniHeight;
        var probePane = _roster.Panes.FirstOrDefault();
        if (probePane is null) return 110;
        var probe = new SessionPane(_p, "probe");
        probe.Update(probePane, null);
        probe.SetSize(RoostPaneSize.Collapsed, false);
        double w = _stage.Bounds.Width > 0 ? (_stage.Bounds.Width - 2 * StagePad - CellGap) / 2 : 500;
        probe.Measure(new Size(w, double.PositiveInfinity));
        _miniHeight = probe.DesiredSize.Height > 0 ? probe.DesiredSize.Height : 110;
        probe.Park();
        return _miniHeight;
    }

    private void Page(int rows)
    {
        if (_mode != RoostLayoutMode.Tiled) return;
        int next = RoostLayout.ClampFirstRow(_firstRow + rows, _cellCount);
        if (next == _firstRow) return;
        _firstRow = next;
        Refresh();
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
            if (_focused is { } key) OnPaneAction(key, RoostPaneAction.ToggleSize);
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
        switch (e.Key)
        {
            case Key.PageDown: Page(+1); e.Handled = true; break;
            case Key.PageUp: Page(-1); e.Handled = true; break;
            case Key.Enter or Key.Escape: e.Handled = FocusedPerchKey(e.Key); break;
        }
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
            case RoostPaneAction.ToggleSize:
                if (_mode != RoostLayoutMode.Tiled) { SetMode(RoostLayoutMode.Tiled); }
                var current = _sizes.TryGetValue(key, out var s) ? s : RoostPaneSize.Collapsed;
                _roster.SetPin(key, current == RoostPaneSize.Expanded ? RoostPin.Collapsed : RoostPin.Expanded);
                _focused = key;
                Refresh(reveal: key);
                break;
            case RoostPaneAction.AutoSize:
                _roster.SetPin(key, RoostPin.Auto);
                Refresh(reveal: key);
                break;
            case RoostPaneAction.OpenSession:
                OpenSessionRequested?.Invoke(pane.Session);
                break;
            case RoostPaneAction.CopyResume:
                _ = CopyAsync($"claude --resume {pane.Session.SessionId}");
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

    private void HideOverflow() => _pillUp.IsVisible = _pillDown.IsVisible = false;

    private void RefreshOverflow(IReadOnlyList<RoostCell> cells)
    {
        var o = RoostLayout.Overflow(cells, _firstRow,
            k => _roster.Find(k) is { Group: RoostGroup.NeedsYou });
        _pillUp.IsVisible = o.Above > 0;
        _pillUpText.Text = o.AboveNeedsYou > 0 ? $"↑ {o.Above} more · {o.AboveNeedsYou} need{(o.AboveNeedsYou == 1 ? "s" : "")} you" : $"↑ {o.Above} more";
        _pillDown.IsVisible = o.Below > 0;
        bool downNeeds = o.BelowNeedsYou > 0;
        _pillDownText.Text = downNeeds ? $"↓ {o.Below} more · {o.BelowNeedsYou} need{(o.BelowNeedsYou == 1 ? "s" : "")} you" : $"↓ {o.Below} more";
        _pillDownDot.IsVisible = downNeeds;
        _pillDown.BorderBrush = downNeeds ? _p.Await : _p.Border;
        _pillDownText.Foreground = downNeeds ? _p.Await : _p.Muted;
        _pillUp.BorderBrush = o.AboveNeedsYou > 0 ? _p.Await : _p.Border;
        _pillUpText.Foreground = o.AboveNeedsYou > 0 ? _p.Await : _p.Muted;
    }

    private void RefreshChips()
    {
        var c = _roster.Counts;
        _chips.Children.Clear();
        if (c.NeedsYou > 0) _chips.Children.Add(Chip($"{c.NeedsYou} need{(c.NeedsYou == 1 ? "s" : "")} you", _p.Await, RoostGroup.NeedsYou, strong: true));
        if (c.Working > 0) _chips.Children.Add(Chip($"{c.Working} working", _p.Ok, RoostGroup.Working, strong: false));
        if (c.DoneReview > 0) _chips.Children.Add(Chip($"{c.DoneReview} done", _p.Attn, RoostGroup.DoneReview, strong: false));
        if (c.Quiet > 0) _chips.Children.Add(Chip($"{c.Quiet} quiet", _p.Idle, RoostGroup.Quiet, strong: false));
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
            _firstRow = 0;
            Refresh();
        };
        return chip;
    }

    private void RefreshRail()
    {
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
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6), BorderThickness = new Thickness(1),
            BorderBrush = on ? _p.BrandLine : Brushes.Transparent, Background = on ? _p.BrandWash : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Opacity = pane.Ended ? 0.6 : 1,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Left, Children = { dot } },
                    new TextBlock { Text = elapsed, FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock
                    {
                        Text = s.IsPerchControlled ? "◆" : "", FontSize = 9, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(5, 0, 0, 0), IsVisible = s.IsPerchControlled, [DockPanel.DockProperty] = Dock.Right,
                    },
                    new TextBlock
                    {
                        Text = s.DisplayName, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = _p.Text,
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
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) FocusPane(pane.Key); };
        return row;
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
