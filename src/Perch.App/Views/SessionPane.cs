using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Data;
using Perch.Data.Control;
using Perch.Data.Roost;

namespace Perch.Avalonia.Views;

/// <summary>What a pane's menu / footer asked for.</summary>
internal enum RoostPaneAction
{
    /// <summary>Flip expanded ↔ collapsed and pin it (header double-click, Ctrl+Shift+E, the menu).</summary>
    ToggleSize,
    /// <summary>Clear the pin — back to size-by-status.</summary>
    AutoSize,
    /// <summary>Open the Perch session window / focus the terminal hosting the session.</summary>
    OpenSession,
    /// <summary>Copy <c>claude --resume {id}</c>.</summary>
    CopyResume,
}

/// <summary>
/// One session in the Roost (docs/roost-plan.md): a header (status dot, name, origin badge, status pill, model,
/// context thermo, ⋯ menu) over a body that is either the session's thread in compact density (expanded) or a
/// <see cref="ActivitySummary"/> mini card (collapsed), plus an origin-dependent footer when expanded. Its chrome
/// carries the status: a pulsing ring for needs-you, steady rings for an error / done-review, a dimmed header for
/// idle and ended. Owns no state beyond what it's shown — the window resolves size/focus and feeds it data.
/// An expanded pane creates its <see cref="SessionThreadView"/>; collapsing (or <see cref="Park"/>) drops and
/// unbinds it, so a collapsed or off-screen pane holds no thread.
/// </summary>
internal sealed class SessionPane : Border
{
    /// <summary>Mini-card body line height; the body always reserves <see cref="ActivitySummary.DefaultMax"/>
    /// lines so every mini card is the same height (the window derives cell capacity from it).</summary>
    public const double MiniLineHeight = 19;

    private readonly SessionPalette _p;
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name;
    private readonly Border _origin;
    private readonly TextBlock _originText;
    private readonly Border _pill;
    private readonly TextBlock _pillText;
    private readonly TextBlock _meta;
    private readonly Border _ctxTrack;
    private readonly Border _ctxFill;
    private readonly Border _header;
    private readonly Border _body;
    private readonly Border _footer;
    private readonly StackPanel _miniLines;
    private readonly Border _menuButton;

    private RoostPane? _pane;
    private RoostFeed? _feed;
    private SessionThreadView? _thread;
    private SessionConversation? _boundConversation;
    private RoostPaneSize _size = RoostPaneSize.Collapsed;
    private bool _held, _focused, _parked = true;

    public SessionPane(SessionPalette palette, string key)
    {
        _p = palette;
        Key = key;
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        Background = _p.Surface;
        ClipToBounds = false;

        _name = new TextBlock
        {
            FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = _p.Title,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        _originText = new TextBlock { FontFamily = _p.Mono, FontSize = 10.5 };
        _origin = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = _originText, Margin = new Thickness(7, 0, 0, 0),
        };
        _pillText = new TextBlock { FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11 };
        _pill = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(9, 2), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = _pillText, Margin = new Thickness(8, 0, 0, 0),
        };
        _menuButton = new Border
        {
            Width = 22, Height = 20, CornerRadius = new CornerRadius(6), Margin = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center, [ToolTip.TipProperty] = "Pane menu",
            Child = new TextBlock
            {
                Text = "⋯", FontSize = 14, Foreground = _p.Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _menuButton.PointerEntered += (_, _) => _menuButton.Background = _p.Raised2;
        _menuButton.PointerExited += (_, _) => _menuButton.Background = Brushes.Transparent;
        _menuButton.PointerPressed += (_, e) => e.Handled = true;   // don't also count as a header double-click
        _menuButton.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;
            ShowMenu();
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, [DockPanel.DockProperty] = Dock.Right,
            Children = { _pill, _menuButton },
        };
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0, [DockPanel.DockProperty] = Dock.Left,
            Children = { _dot },
        };
        _dot.Margin = new Thickness(0, 0, 8, 0);
        // The name gives way first and the origin badge stays right beside it: a left-aligned dock whose
        // right-docked badge is measured first, leaving the name the rest to trim into.
        _origin[DockPanel.DockProperty] = Dock.Right;
        var line1 = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                left, right,
                new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Left, Children = { _origin, _name } },
            },
        };

        _meta = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _ctxFill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
        _ctxTrack = new Border
        {
            Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = _p.Raised2,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Child = _ctxFill, [DockPanel.DockProperty] = Dock.Right,
        };
        var line2 = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 3, 0, 0), Children = { _ctxTrack, _meta } };

        _header = new Border
        {
            Padding = new Thickness(10, 8, 8, 7), BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(11, 11, 0, 0), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Children = { line1, line2 } }, [DockPanel.DockProperty] = Dock.Top,
        };
        _header.DoubleTapped += (_, e) => { e.Handled = true; ActionRequested?.Invoke(Key, RoostPaneAction.ToggleSize); };

        _miniLines = new StackPanel
        {
            Margin = new Thickness(12, 6, 10, 8),
            MinHeight = MiniLineHeight * ActivitySummary.DefaultMax,
        };
        _footer = new Border
        {
            BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6),
            Background = _p.Surface, CornerRadius = new CornerRadius(0, 0, 11, 11),
            [DockPanel.DockProperty] = Dock.Bottom, IsVisible = false,
        };
        _body = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(0, 0, 11, 11) };

        Child = new DockPanel { LastChildFill = true, Children = { _header, _footer, _body } };

        // Any press inside the pane focuses it (tunnelling, so it also fires for clicks the thread handles).
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(Key), global::Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public string Key { get; }
    public RoostPaneSize Size => _size;

    /// <summary>The pane was clicked (focus it).</summary>
    public event Action<string>? Activated;

    /// <summary>The menu, a header double-click or a footer button asked for something.</summary>
    public event Action<string, RoostPaneAction>? ActionRequested;

    /// <summary>A permission card in this (controlled) pane's thread was answered: (item, allow, switch mode).</summary>
    public event Action<PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>A question card in this (controlled) pane's thread was answered.</summary>
    public event Action<PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    /// <summary>Points the pane at its latest roster snapshot and feed; refreshes header, chrome and body.</summary>
    public void Update(RoostPane pane, RoostFeed? feed)
    {
        _pane = pane;
        if (!ReferenceEquals(feed, _feed))
        {
            if (_feed is not null) _feed.Changed -= OnFeedChanged;
            _feed = feed;
            if (_feed is not null) _feed.Changed += OnFeedChanged;
        }
        RefreshHeader();
        RefreshChrome();
        RefreshBody();
    }

    /// <summary>Sets the resolved size. <paramref name="held"/> = wants to expand but is held collapsed (it pulses).</summary>
    public void SetSize(RoostPaneSize size, bool held)
    {
        bool changed = _parked || size != _size || held != _held;   // back from parked = body is stale
        _parked = false;
        _size = size;
        _held = held;
        if (!changed) return;
        RefreshChrome();
        RefreshBody();
    }

    /// <summary>The window's 1s clock: re-derive the time-bearing header text ("Working · 41s").</summary>
    public void Tick() => RefreshHeader();

    public void SetFocused(bool focused)
    {
        if (focused == _focused) return;
        _focused = focused;
        RefreshChrome();
    }

    /// <summary>The pane left the viewport: drop its thread so an off-screen pane holds no materialised view.
    /// It re-materialises the next time it's shown expanded.</summary>
    public void Park()
    {
        if (_parked) return;
        _parked = true;
        DropThread();
    }

    /// <summary>True when this pane is animating its ring (needs-you, or held from expanding).</summary>
    public bool Pulsing => _pane is { Ended: false } p && (_held || p.Session.Status == SessionStatus.AwaitingInput);

    /// <summary>One pulse frame: <paramref name="intensity"/> 0..1 breathes the glow around the ring.</summary>
    public void PulseTick(double intensity, bool reduceMotion)
    {
        if (!Pulsing) return;
        var c = RingColor();
        BoxShadow = reduceMotion
            ? Ring(c, 3, 0, 0)
            : Ring(c, 2, 7 * intensity, 0.28 * (1 - 0.35 * intensity));
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void RefreshHeader()
    {
        if (_pane is not { } pane) return;
        var s = pane.Session;
        _name.Text = s.DisplayName;
        _dot.Fill = DotBrush(pane);

        var (origin, perch) = s.IsPerchControlled ? ("◆ Perch", true)
            : s.IdeHost is { } ide ? (ShortIde(ide), false)
            : s.IsDesktop ? ("Desktop", false)
            : ("▣ terminal", false);
        _originText.Text = origin;
        _originText.Foreground = perch ? _p.Brand : _p.Muted;
        _origin.BorderBrush = perch ? _p.BrandLine : _p.Border;

        _pillText.Text = PillText(pane);
        _meta.Text = MetaText(s);

        _ctxTrack.IsVisible = s.ContextFill is not null;
        if (s.ContextFill is { } fill)
        {
            _ctxFill.Width = Math.Clamp(fill, 0, 1) * _ctxTrack.Width;
            _ctxFill.Background = fill >= 0.8 ? _p.Await : _p.Ok;
            _ctxTrack[ToolTip.TipProperty] = $"Context {fill:P0}";
        }
    }

    private static string ShortIde(IdeHost ide) => ide.Kind switch
    {
        IdeHostKind.VsCode => "VS Code",
        _ => ide.DisplayName,
    };

    /// <summary>The status pill's words.</summary>
    internal static string PillText(RoostPane pane)
    {
        var s = pane.Session;
        if (pane.EndedAt is { } ended) return $"Ended {Ago(ended)}";
        return s.Status switch
        {
            SessionStatus.AwaitingInput => s.AwaitingElapsedLabel() is { } w ? $"Needs your input · {w}" : "Needs your input",
            SessionStatus.ApiError => s.ApiFailure is { Status: > 0 } f ? $"API error · {f.Status}" : "API error",
            SessionStatus.NeedsAttention => $"Done {Ago(s.LastUpdated)} · review",
            SessionStatus.Running => s.RunningElapsedLabel() is { } r ? $"Working · {r}" : "Working",
            _ => "Idle",
        };
    }

    private static string Ago(DateTime at)
    {
        var d = Clock.Now - at;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h ago" : $"{(int)d.TotalMinutes}m ago";
    }

    private static string MetaText(ClaudeSession s)
    {
        var parts = new List<string>();
        if (s.Model is { Length: > 0 } m) parts.Add(Windows.SessionWindow.ShortModel(m));
        if (s.GitStats is { } g && (g.Added > 0 || g.Deleted > 0)) parts.Add($"+{g.Added} −{g.Deleted}");
        parts.Add(TailOf(s.Cwd));
        return string.Join("  ·  ", parts);
    }

    private static string TailOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private IBrush DotBrush(RoostPane pane) => pane.Ended ? _p.Faint : pane.Session.Status switch
    {
        SessionStatus.AwaitingInput => _p.Await,
        SessionStatus.ApiError => _p.Err,
        SessionStatus.NeedsAttention => _p.Attn,
        SessionStatus.Running => _p.Ok,
        _ => _p.Idle,
    };

    private Color RingColor()
    {
        if (_pane is not { } pane) return Colors.Transparent;
        var brush = _held && pane.Group != RoostGroup.NeedsYou && pane.Group != RoostGroup.DoneReview
            ? _p.Await
            : DotBrush(pane);
        return ((ISolidColorBrush)brush).Color;
    }

    private static BoxShadows Ring(Color c, double spread, double glow, double glowAlpha)
    {
        var ring = new BoxShadow { Spread = spread, Color = c };
        if (glow <= 0.1) return new BoxShadows(ring);
        var halo = new BoxShadow { Spread = spread + glow, Color = Color.FromArgb((byte)(255 * glowAlpha), c.R, c.G, c.B) };
        return new BoxShadows(ring, [halo]);
    }

    private void RefreshChrome()
    {
        if (_pane is not { } pane) return;
        var s = pane.Session;
        bool ended = pane.Ended;
        var status = ended ? SessionStatus.Idle : s.Status;

        // Ring: needs-you 2px (pulsing via PulseTick), error 2px steady, done-review 1.5px steady, else none.
        BoxShadow = status switch
        {
            SessionStatus.AwaitingInput or SessionStatus.ApiError => Ring(RingColor(), 2, 0, 0),
            SessionStatus.NeedsAttention => Ring(RingColor(), 1.5, 0, 0),
            _ when _held => Ring(RingColor(), 1.5, 0, 0),
            _ => default,
        };
        BorderBrush = _focused ? _p.BrandLine : _p.Border;

        _header.Background = status switch
        {
            SessionStatus.AwaitingInput => Layer(_p.AwaitWash),
            SessionStatus.ApiError => Layer(_p.ErrWash),
            SessionStatus.NeedsAttention => Layer(_p.AttnWash),
            _ when _focused => Layer(_p.BrandWash),
            _ => _p.Raised,
        };
        _header.Opacity = ended ? 0.6 : status == SessionStatus.Idle ? 0.8 : 1;
        _body.Opacity = ended ? 0.6 : 1;

        (IBrush fg, IBrush line, IBrush wash) = status switch
        {
            SessionStatus.AwaitingInput => ((IBrush)_p.Await, (IBrush)_p.Await, (IBrush)_p.AwaitWash),
            SessionStatus.ApiError => (_p.Err, _p.Err, _p.ErrWash),
            SessionStatus.NeedsAttention => (_p.Attn, _p.Attn, _p.AttnWash),
            SessionStatus.Running => (_p.Ok, _p.Border, _p.Surface),
            _ => (_p.Muted, _p.Border, _p.Surface),
        };
        _pillText.Foreground = fg;
        _pill.BorderBrush = line;
        _pill.Background = wash;
    }

    // A translucent wash laid over the raised header ground (the mockup's layered linear-gradient).
    private IBrush Layer(SolidColorBrush wash)
    {
        var raised = _p.Raised.Color;
        var w = wash.Color;
        double a = w.A / 255.0;
        byte Mix(byte bg, byte fg) => (byte)Math.Round(bg + (fg - bg) * a);
        return new SolidColorBrush(Color.FromRgb(Mix(raised.R, w.R), Mix(raised.G, w.G), Mix(raised.B, w.B)));
    }

    // ── Body ──────────────────────────────────────────────────────────────────

    private void OnFeedChanged()
    {
        if (_size == RoostPaneSize.Expanded && _thread is not null && _feed is { } f
            && !ReferenceEquals(f.Conversation, _boundConversation))
        {
            _thread.Bind(f.Conversation);   // the tail reset: a fresh conversation
            _boundConversation = f.Conversation;
        }
        if (_size == RoostPaneSize.Collapsed || _thread is null) RefreshBody();
    }

    private void RefreshBody()
    {
        if (_pane is null || _parked) return;
        if (_size == RoostPaneSize.Expanded && _feed is { IsLoading: false } feed)
        {
            EnsureThread(feed);
            RefreshFooter();
            return;
        }

        DropThread();
        _footer.IsVisible = false;
        _body.Child = _miniLines;
        FillMiniLines();
    }

    private void EnsureThread(RoostFeed feed)
    {
        if (_thread is not null && ReferenceEquals(_boundConversation, feed.Conversation)) return;
        if (_thread is null)
        {
            _thread = new SessionThreadView(_p, compact: true) { Cwd = _pane?.Session.Cwd ?? "" };
            _thread.PermissionAnswered += (item, allow, mode) => PermissionAnswered?.Invoke(item, allow, mode);
            _thread.QuestionAnswered += (item, answers) => QuestionAnswered?.Invoke(item, answers);
        }
        _thread.Bind(feed.Conversation);
        _boundConversation = feed.Conversation;
        _body.Child = new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(SessionThreadView.CompactScale, SessionThreadView.CompactScale),
            Child = _thread,
        };
    }

    private void DropThread()
    {
        if (_thread is null) return;
        _thread.Unbind();
        _thread = null;
        _boundConversation = null;
        _body.Child = null;
    }

    private void FillMiniLines()
    {
        _miniLines.Children.Clear();
        if (_feed is null || _feed.IsLoading)
        {
            _miniLines.Children.Add(MiniLine("", _feed is null ? "No transcript found" : "Loading…", _p.Faint, _p.Faint));
            return;
        }
        bool running = _pane is { Ended: false, Session.Status: SessionStatus.Running };
        var lines = ActivitySummary.Build(_feed.Conversation, running);
        if (lines.Count == 0) { _miniLines.Children.Add(MiniLine("", "No activity yet", _p.Faint, _p.Faint)); return; }
        foreach (var l in lines)
        {
            var (glyph, glyphBrush, textBrush) = l.Kind switch
            {
                ActivityKind.ToolRunning => ("▸", (IBrush)_p.Ok, (IBrush)_p.Text),
                ActivityKind.ToolFailed => ("✕", _p.Err, _p.Text),
                ActivityKind.Permission => ("⚠", _p.Await, _p.Title),
                ActivityKind.Question => ("?", _p.Await, _p.Title),
                ActivityKind.Plan => ("▤", _p.Await, _p.Title),
                ActivityKind.Done => ("✓", _p.Ok, _p.Text),
                ActivityKind.User => ("›", _p.Muted, _p.Muted),
                ActivityKind.Error => ("✕", _p.Err, _p.Err),
                ActivityKind.Compacting => ("⟳", _p.Muted, _p.Muted),
                ActivityKind.Prose => ("…", _p.Faint, _p.Muted),
                _ => ("▸", _p.Faint, _p.Text),
            };
            _miniLines.Children.Add(MiniLine(glyph, l.Text, glyphBrush, textBrush));
        }
    }

    private Control MiniLine(string glyph, string text, IBrush glyphBrush, IBrush textBrush) => new DockPanel
    {
        Height = MiniLineHeight, LastChildFill = true,
        Children =
        {
            new TextBlock
            {
                Text = glyph, Width = 16, FontSize = 12, Foreground = glyphBrush, VerticalAlignment = VerticalAlignment.Center,
                [DockPanel.DockProperty] = Dock.Left,
            },
            new TextBlock
            {
                Text = text, FontFamily = _p.Body, FontSize = 12.5, Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        },
    };

    // ── Footer ────────────────────────────────────────────────────────────────

    private void RefreshFooter()
    {
        if (_pane is not { } pane) return;
        bool controlled = pane.Session.IsPerchControlled;   // by origin: a Perch session opens its window
        var note = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Text = controlled
                ? (pane.Session.Status == SessionStatus.AwaitingInput ? "Answer here, or reply in the full window" : "Perch session")
                : pane.Ended ? "Session ended" : "Tailing · read-only",
        };
        var button = FooterButton(controlled ? "⤢ Open window" : "Focus terminal ↗",
            () => ActionRequested?.Invoke(Key, RoostPaneAction.OpenSession));
        button[DockPanel.DockProperty] = Dock.Right;
        button.IsVisible = !pane.Ended;
        _footer.Child = new DockPanel { LastChildFill = true, Children = { button, note } };
        _footer.IsVisible = true;
    }

    private Border FooterButton(string label, Action onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3), BorderThickness = new Thickness(1),
            BorderBrush = _p.Border, Background = _p.Raised, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = _p.Text },
        };
        b.PointerEntered += (_, _) => b.BorderBrush = _p.BrandLine;
        b.PointerExited += (_, _) => b.BorderBrush = _p.Border;
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) onClick(); };
        return b;
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void ShowMenu()
    {
        if (_pane is not { } pane) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var toggle = new MenuItem { Header = _size == RoostPaneSize.Expanded ? "Collapse" : "Expand" };
        toggle.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.ToggleSize);
        var auto = new MenuItem { Header = "Auto size (by status)", IsEnabled = pane.Pin != RoostPin.Auto };
        auto.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.AutoSize);
        var open = new MenuItem
        {
            Header = pane.Session.IsPerchControlled ? "Open full window" : "Focus terminal",
            IsEnabled = !pane.Ended,
        };
        open.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.OpenSession);
        var copy = new MenuItem { Header = "Copy resume command" };
        copy.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.CopyResume);
        flyout.Items.Add(toggle);
        flyout.Items.Add(auto);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(open);
        flyout.Items.Add(copy);
        flyout.ShowAt(_menuButton);
    }
}
