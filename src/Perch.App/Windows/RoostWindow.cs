using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
/// The Roost (docs/roost-plan.md): a tmux-style view of every live session — an urgency-ranked rail on the left
/// (Needs you → Done · review → Working → Quiet) and a stage of <see cref="SessionPane"/>s on the right, laid out
/// by the pure <see cref="RoostLayout"/> rules (first-seen order that never reorders on a status flip; panes
/// expanded or collapsed to mini cards by status, pin, and focus). The session list itself is the app-owned
/// <see cref="RoostRoster"/>, so pins and order survive closing the window; this window owns only the per-pane
/// feeds (<see cref="RoostFeed"/>: a Perch session's in-memory conversation, or a tailed transcript), which
/// live only while it's open.
///
/// <para>Tiled is a fixed 2×2 cell viewport paged a row at a time (wheel / PageUp·PageDown / the ↑↓ pills) —
/// discrete row paging gives the row snap for free, and only the visible cells hold controls: an off-screen
/// pane is parked (no materialised thread). The visible layout is rebuilt only when its shape changes; a scan
/// that only moves text refreshes the panes in place, so an expanded thread keeps its scroll.</para>
/// </summary>
internal sealed class RoostWindow : Window
{
    private const double StagePad = 14, CellGap = 14;

    private readonly RoostRoster _roster;
    private readonly Func<RoostPane, RoostFeed?> _feedFactory;
    private readonly SessionPalette _p;

    private readonly Dictionary<string, RoostFeed?> _feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionPane> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoostPaneSize> _sizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visible = new(StringComparer.Ordinal);

    private readonly StackPanel _chips;
    private readonly StackPanel _rail;
    private readonly Grid _grid;
    private readonly Border[] _cellHosts = new Border[4];
    private readonly Panel _stage;
    private readonly Border _emptyNote;
    private readonly Border _pillUp, _pillDown;
    private readonly TextBlock _pillUpText, _pillDownText;
    private readonly Ellipse _pillDownDot;

    private readonly DispatcherTimer _pulseTimer;
    private readonly DispatcherTimer _clockTimer;

    private string? _focused;
    private int _firstRow;
    private int _cellCount;
    private string _layoutSig = "";
    private double _miniHeight;

    public RoostWindow(RoostRoster roster, Func<RoostPane, RoostFeed?> feedFactory, SessionPalette? palette = null)
    {
        _roster = roster;
        _feedFactory = feedFactory;
        _p = palette ?? SessionPalette.Current;

        Title = "Roost";
        Width = 1280;
        Height = 800;
        MinWidth = 760;
        MinHeight = 480;
        Background = _p.Ground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // ── Title bar: name · summary chips ········ + New session ──
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
        newSession[DockPanel.DockProperty] = Dock.Right;
        var bar = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10), [DockPanel.DockProperty] = Dock.Top,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { newSession, new StackPanel { Orientation = Orientation.Horizontal, Children = { title, _chips } } },
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

        // ── Stage: the 2×2 cell viewport + the ↑/↓ overflow pills ──
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
        (_pillUp, _pillUpText, _) = OverflowPill(up: true);
        (_pillDown, _pillDownText, _pillDownDot) = OverflowPill(up: false);
        _pillUp.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Page(-1); };
        _pillDown.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Page(+1); };

        _emptyNote = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "No live sessions", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 16, Foreground = _p.Title, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Sessions appear here as soon as they start — or start one with + New session.", FontFamily = _p.Body, FontSize = 13, Foreground = _p.Muted, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        _stage = new Panel { Background = _p.Ground, Children = { _grid, _emptyNote } };

        // ── Bottom bar: key hints · the ↑/↓ overflow pills (their own band, so they never cover a pane) ──
        var hints = new TextBlock
        {
            Text = "PgUp / PgDn  page   ·   double-click a header  expand / collapse",
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
        _stage.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y == 0) return;
            Page(e.Delta.Y < 0 ? +1 : -1);
            e.Handled = true;
        };
        _stage.SizeChanged += (_, _) => { _miniHeight = 0; Refresh(); };

        Content = new DockPanel { LastChildFill = true, Children = { bar, railHost, stageColumn } };

        KeyDown += OnKeyDown;

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pulseTimer.Tick += (_, _) => PulseFrame();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { foreach (var k in _visible) _views[k].Tick(); RefreshRail(); };
        _clockTimer.Start();

        Closed += (_, _) =>
        {
            _pulseTimer.Stop();
            _clockTimer.Stop();
            foreach (var v in _views.Values) v.Park();
            foreach (var f in _feeds.Values) f?.Dispose();
            _feeds.Clear();
        };

        Refresh();
    }

    /// <summary>"+ New session" in the title bar.</summary>
    public event Action? NewSessionRequested;

    /// <summary>A pane asked to open its session (the Perch window, or focus the terminal).</summary>
    public event Action<ClaudeSession>? OpenSessionRequested;

    /// <summary>A permission card in a Perch pane was answered: (session id, item, allow, switch mode).</summary>
    public event Action<string, PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>A question card in a Perch pane was answered: (session id, item, answers).</summary>
    public event Action<string, PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    /// <summary>The roster changed (a monitor scan landed) — re-sync feeds, panes and layout.</summary>
    public void RosterChanged() => Refresh();

    /// <summary>Focuses a pane: scrolls it into view and (by the resolver's focus rule) keeps it expanded.</summary>
    public void FocusPane(string key)
    {
        if (_roster.Find(key) is null) return;
        _focused = key;
        Refresh(reveal: key);
    }

    // ── Layout pass ───────────────────────────────────────────────────────────

    private void Refresh(string? reveal = null)
    {
        var panes = _roster.Panes;
        SyncFeedsAndViews(panes);

        // Resolve every pane's size. P1 has no composer, so nobody is ever "typing elsewhere".
        var sized = new List<(string Key, RoostPaneSize Size)>(panes.Count);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pane in panes)
        {
            RoostPaneSize? current = _sizes.TryGetValue(pane.Key, out var c) ? c : null;
            var d = RoostLayout.ResolveSize(new RoostSizeInputs(
                pane.Pin, pane.Group, pane.Ended, pane.Key == _focused, TypingElsewhere: false, current));
            _sizes[pane.Key] = d.Size;
            if (d.Held) held.Add(pane.Key);
            sized.Add((pane.Key, d.Size));
        }

        int capacity = RoostLayout.CellCapacity(CellHeight(), MiniHeight(), CellGap);
        var cells = RoostLayout.Pack(sized, capacity);
        _cellCount = cells.Count;
        if (reveal is not null) _firstRow = RoostLayout.ScrollToReveal(RoostLayout.CellOf(cells, reveal), _firstRow, cells.Count);
        _firstRow = RoostLayout.ClampFirstRow(_firstRow, cells.Count);

        // Rebuild the visible cells only when their shape changed; otherwise the panes refresh in place.
        int first = _firstRow * RoostLayout.Columns;
        var visibleCells = cells.Skip(first).Take(RoostLayout.Columns * RoostLayout.VisibleRows).ToList();
        var sig = string.Join("|", visibleCells.Select(c => (c.Expanded ? "E:" : "m:") + string.Join(",", c.Keys)));
        if (sig != _layoutSig)
        {
            _layoutSig = sig;
            foreach (var host in _cellHosts)
            {
                if (host.Child is Panel stack) stack.Children.Clear();
                host.Child = null;
            }
            _visible.Clear();
            for (int i = 0; i < visibleCells.Count; i++)
            {
                var cell = visibleCells[i];
                if (cell.Expanded)
                {
                    _cellHosts[i].Child = _views[cell.Keys[0]];
                }
                else
                {
                    var stack = new StackPanel { Spacing = CellGap, VerticalAlignment = VerticalAlignment.Top };
                    foreach (var k in cell.Keys) stack.Children.Add(_views[k]);
                    _cellHosts[i].Child = stack;
                }
                foreach (var k in cell.Keys) _visible.Add(k);
            }
        }

        foreach (var pane in panes)
        {
            var view = _views[pane.Key];
            view.Update(pane, _feeds[pane.Key]);
            view.SetFocused(pane.Key == _focused);
            if (_visible.Contains(pane.Key)) view.SetSize(_sizes[pane.Key], held.Contains(pane.Key));
            else view.Park();
        }

        _emptyNote.IsVisible = panes.Count == 0;
        RefreshOverflow(cells);
        RefreshChips();
        RefreshRail();
        UpdatePulse();
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
            _sizes.Remove(gone);
            if (_feeds.Remove(gone, out var f)) f?.Dispose();
            if (_focused == gone) _focused = null;
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
                view.Activated += FocusPane;
                view.ActionRequested += OnPaneAction;
                var sid = pane.Session.SessionId;
                view.PermissionAnswered += (item, allow, mode) => PermissionAnswered?.Invoke(sid, item, allow, mode);
                view.QuestionAnswered += (item, answers) => QuestionAnswered?.Invoke(sid, item, answers);
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

    /// <summary>HeadlessRenderer hook: page the Tiled viewport (there's no wheel in a headless capture).</summary>
    internal void PageForRender(int rows) => Page(rows);

    private void Page(int rows)
    {
        int next = RoostLayout.ClampFirstRow(_firstRow + rows, _cellCount);
        if (next == _firstRow) return;
        _firstRow = next;
        Refresh();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.PageDown: Page(+1); e.Handled = true; break;
            case Key.PageUp: Page(-1); e.Handled = true; break;
        }
    }

    private void OnPaneAction(string key, RoostPaneAction action)
    {
        if (_roster.Find(key) is not { } pane) return;
        switch (action)
        {
            case RoostPaneAction.ToggleSize:
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
        bool any = _visible.Any(k => _views[k].Pulsing);
        bool still = Pulse.ReduceMotion;
        if (any && still) foreach (var k in _visible) _views[k].PulseTick(1, reduceMotion: true);
        if (any && !still) { if (!_pulseTimer.IsEnabled) _pulseTimer.Start(); }
        else if (_pulseTimer.IsEnabled) _pulseTimer.Stop();
    }

    private void PulseFrame()
    {
        double i = Pulse.Intensity(2200);
        bool still = Pulse.ReduceMotion;
        foreach (var k in _visible) _views[k].PulseTick(i, still);
        if (still) _pulseTimer.Stop();
    }

    // ── Chrome: overflow pills, chips, rail ───────────────────────────────────

    private (Border, TextBlock, Ellipse) OverflowPill(bool up)
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = _p.Await, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = SessionPalette.PillRadius,
            Padding = new Thickness(12, 3), BorderThickness = new Thickness(1), BorderBrush = _p.Border,
            Background = _p.Surface, Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } },
        };
        return (pill, text, dot);
    }

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
        if (c.NeedsYou > 0) _chips.Children.Add(Chip($"{c.NeedsYou} need{(c.NeedsYou == 1 ? "s" : "")} you", _p.Await, strong: true));
        if (c.Working > 0) _chips.Children.Add(Chip($"{c.Working} working", _p.Ok, strong: false));
        if (c.DoneReview > 0) _chips.Children.Add(Chip($"{c.DoneReview} done", _p.Attn, strong: false));
        if (c.Quiet > 0) _chips.Children.Add(Chip($"{c.Quiet} quiet", _p.Idle, strong: false));
    }

    private Border Chip(string label, IBrush dot, bool strong) => new()
    {
        CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(10, 3), BorderThickness = new Thickness(1),
        BorderBrush = _p.Border, Background = _p.Surface, VerticalAlignment = VerticalAlignment.Center,
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = strong ? _p.Text : _p.Muted },
            },
        },
    };

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
    /// panes (tmux main-vertical). Shared with the overlay's entry-point glyph (CP9) in spirit — that one is
    /// owner-drawn.</summary>
    internal static Control RoostGlyph(IBrush stroke, double size) => new global::Avalonia.Controls.Shapes.Path
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Stroke = stroke, StrokeThickness = 1.5,
        VerticalAlignment = VerticalAlignment.Center,
        Data = Geometry.Parse("M3,2 H13 A1.5,1.5 0 0 1 14.5,3.5 V12.5 A1.5,1.5 0 0 1 13,14 H3 A1.5,1.5 0 0 1 1.5,12.5 V3.5 A1.5,1.5 0 0 1 3,2 Z M7.5,2 V14 M7.5,8 H14.5"),
    };
}
