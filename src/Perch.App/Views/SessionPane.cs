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
    /// <summary>Take the session out of its tab (back to the rail).</summary>
    RemoveFromTab,
    /// <summary>Zoom the pane to fill its tab, or restore it (header double-click, Ctrl+Shift+Z).</summary>
    Zoom,
    /// <summary>Open the Perch session window / focus the terminal hosting the session.</summary>
    OpenSession,
    /// <summary>Copy <c>claude --resume {id}</c>.</summary>
    CopyResume,
    /// <summary>Take a terminal session over in Perch (the app confirms first — see <c>App.OnElevateToPerch</c>).</summary>
    TakeOver,
    /// <summary>Hide the pane (the session keeps running; reopen it from the rail's "N hidden" row).</summary>
    Close,
    /// <summary>A dormant pane: <c>claude --resume</c> it in a new terminal.</summary>
    ResumeInTerminal,
    /// <summary>A dormant pane: drop it from the Roost and the Recent list.</summary>
    Dismiss,
}

/// <summary>
/// One session in the Roost (docs/roost-plan.md): a header (status dot, name, origin badge, status pill, model,
/// context thermo, ⋯ menu) over a body that is the session's thread in compact density — or, while that loads, an
/// <see cref="ActivitySummary"/> mini card — plus an origin-dependent footer. Its chrome carries the status: a
/// pulsing ring for needs-you, steady rings for an error / done-review, a dimmed header for idle and ended. Owns no
/// state beyond what it's shown — the window resolves placement/focus and feeds it data. A shown pane
/// (<see cref="Show"/>) creates its <see cref="SessionThreadView"/>; <see cref="Park"/> drops and unbinds it, so an
/// off-screen pane past the warm limit holds no thread.
/// <para>A <b>dormant</b> pane (<see cref="RoostPane.IsDormant"/>, docs/session-recovery-plan.md R6) shows a session with
/// no process: its transcript, a pill saying how it ended, and a composer whose first send resumes it (the window asks
/// the app through <see cref="PromptSubmitted"/>), plus "Resume in terminal".</para>
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
    private readonly TextBlock _footerNote;
    private readonly TextBox _composer;
    private readonly Border _footerButton;
    private readonly TextBlock _footerButtonText;
    private readonly Border _takeOverButton;
    private readonly Border _earlierBar;
    private readonly TextBlock _earlierText;
    private bool _earlierArmed;   // a large "load earlier" was clicked once; the next click confirms it

    /// <summary>Whether this pane's session can be taken over in Perch (an interactive terminal session Perch
    /// doesn't already own, still running). Set by the window — the eligibility rule is the overlay's.</summary>
    public bool CanTakeOver { get; set; }

    /// <summary>Whether a dormant pane's composer can resume it (the window has somewhere to send the wake).</summary>
    public bool CanWake { get; set; }

    /// <summary>A dormant pane's first send is being checked and started: the composer holds the text, read-only.</summary>
    public bool IsWaking { get; private set; }

    private readonly Border _terminalButton;
    private readonly StackPanel _miniLines;
    private readonly Border _menuButton;

    private RoostPane? _pane;
    private RoostFeed? _feed;
    private SessionThreadView? _thread;
    private SessionConversation? _boundConversation;
    private bool _focused, _parked = true;

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
        _header.DoubleTapped += (_, e) => { e.Handled = true; ActionRequested?.Invoke(Key, RoostPaneAction.Zoom); };

        _miniLines = new StackPanel
        {
            Margin = new Thickness(12, 6, 10, 8),
            MinHeight = MiniLineHeight * ActivitySummary.DefaultMax,
        };
        // Footer: built once and only re-labelled, so a half-typed reply survives every refresh. A Perch pane
        // gets the lite composer; a terminal/IDE pane (or an ended one) a note instead.
        _footerNote = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _composer = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = _p.Body, FontSize = 12.5,
            Foreground = _p.Text, CaretBrush = _p.Brand, Background = _p.Raised, BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 5),
            MinHeight = 30, MaxHeight = 88, VerticalContentAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        _composer.AddHandler(KeyDownEvent, OnComposerKeyDown, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _footerButtonText = new TextBlock { FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = _p.Text };
        _footerButton = FooterButton(_footerButtonText, () => ActionRequested?.Invoke(Key, RoostPaneAction.OpenSession));
        _footerButton[DockPanel.DockProperty] = Dock.Right;
        _footerButton.Margin = new Thickness(8, 0, 0, 0);
        _footerButton.VerticalAlignment = VerticalAlignment.Bottom;
        _takeOverButton = FooterButton(
            new TextBlock { Text = "Take over in Perch", FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = _p.Brand },
            () => ActionRequested?.Invoke(Key, RoostPaneAction.TakeOver));
        _takeOverButton[DockPanel.DockProperty] = Dock.Right;
        _takeOverButton.Margin = new Thickness(8, 0, 0, 0);
        _takeOverButton.VerticalAlignment = VerticalAlignment.Bottom;
        _takeOverButton.IsVisible = false;
        _takeOverButton[ToolTip.TipProperty] = "Stop it in its terminal and continue the same conversation in Perch (asks first)";
        _terminalButton = FooterButton(
            new TextBlock { Text = "Resume in terminal", FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = _p.Text },
            () => ActionRequested?.Invoke(Key, RoostPaneAction.ResumeInTerminal));
        _terminalButton[DockPanel.DockProperty] = Dock.Right;
        _terminalButton.Margin = new Thickness(8, 0, 0, 0);
        _terminalButton.VerticalAlignment = VerticalAlignment.Bottom;
        _terminalButton.IsVisible = false;
        _terminalButton[ToolTip.TipProperty] = "Open a terminal running claude --resume for this session";
        _footer = new Border
        {
            BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6),
            Background = _p.Surface, CornerRadius = new CornerRadius(0, 0, 11, 11),
            [DockPanel.DockProperty] = Dock.Bottom, IsVisible = false,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children = { _footerButton, _takeOverButton, _terminalButton, new Panel { Children = { _footerNote, _composer } } },
            },
        };
        _body = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(0, 0, 11, 11) };

        // A tailed pane starts from the transcript's last RoostFeed.TailLines lines; this strip atop its thread
        // loads the rest (asking first when that's a large transcript).
        var earlierText = _earlierText = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Muted, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _earlierBar = new Border
        {
            Padding = new Thickness(10, 4), Background = _p.Raised, BorderBrush = _p.BorderSoft,
            BorderThickness = new Thickness(0, 0, 0, 1), Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false, Child = earlierText, [DockPanel.DockProperty] = Dock.Top,
        };
        _earlierBar.PointerEntered += (_, _) => earlierText.Foreground = _p.Brand;
        _earlierBar.PointerExited += (_, _) => earlierText.Foreground = _p.Muted;
        _earlierBar.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) OnLoadEarlier(); };

        Child = new DockPanel { LastChildFill = true, Children = { _header, _footer, _body } };

        // Any press inside the pane focuses it (tunnelling, so it also fires for clicks the thread handles).
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(Key), global::Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public string Key { get; }

    /// <summary>The header band — the handle the Roost drags a pane by.</summary>
    public Control Header => _header;

    /// <summary>The pane was clicked (focus it).</summary>
    public event Action<string>? Activated;

    /// <summary>The menu, a header double-click or a footer button asked for something.</summary>
    public event Action<string, RoostPaneAction>? ActionRequested;

    /// <summary>A permission card in this (controlled) pane's thread was answered: (item, allow, switch mode).</summary>
    public event Action<PermissionItem, bool, bool>? PermissionAnswered;

    /// <summary>A question card in this (controlled) pane's thread was answered.</summary>
    public event Action<PermissionItem, IReadOnlyDictionary<string, IReadOnlyList<string>>>? QuestionAnswered;

    /// <summary>The composer sent a reply: (pane key, text).</summary>
    public event Action<string, string>? PromptSubmitted;

    /// <summary>Esc in the composer with a turn running (pane key).</summary>
    public event Action<string>? InterruptRequested;

    /// <summary>Points the pane at its latest roster snapshot and feed; refreshes header, chrome and body.</summary>
    public void Update(RoostPane pane, RoostFeed? feed)
    {
        _pane = pane;
        if (!ReferenceEquals(feed, _feed))
        {
            if (_feed is not null) _feed.Changed -= OnFeedChanged;
            _feed = feed;
            _earlierArmed = false;
            if (_feed is not null) _feed.Changed += OnFeedChanged;
        }
        RefreshHeader();
        RefreshChrome();
        RefreshBody();
    }

    /// <summary>The pane is on stage: (re)build its body if it was parked (a parked pane's body is stale).</summary>
    public void Show()
    {
        if (!_parked) return;
        _parked = false;
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

    /// <summary>True when this pane is animating its ring (it needs the user).</summary>
    public bool Pulsing => _pane is { Ended: false } p && p.Session.Status == SessionStatus.AwaitingInput;

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
        _dot.Fill = _p.PaneDot(pane);

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
        if (pane.Dormant is { } dormant) return DormantPillText(dormant);
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

    /// <summary>A dormant pane's pill: why it's here and when it stopped.</summary>
    internal static string DormantPillText(RoostDormant d) => d.Kind switch
    {
        RoostDormantKind.WasOpenInPerch => "Was open in Perch",
        RoostDormantKind.Interrupted => $"Interrupted {Ago(d.LastActive)}",
        RoostDormantKind.BeforeShutdown => $"Before shutdown · {Ago(d.LastActive)}",
        RoostDormantKind.Exited => $"Exited {Ago(d.LastActive)}",
        RoostDormantKind.Ended => $"Ended {Ago(d.LastActive)}",
        _ => "Not running",
    };

    /// <summary>A dormant pane's rail-row note: short, beside the name.</summary>
    internal static string DormantRailLabel(RoostDormant d) => d.Kind switch
    {
        RoostDormantKind.WasOpenInPerch => "was open",
        RoostDormantKind.Interrupted => "interrupted",
        RoostDormantKind.BeforeShutdown => "shutdown",
        RoostDormantKind.NotRunning => "",
        _ => Ago(d.LastActive),
    };

    private static string Ago(DateTime at)
    {
        var d = Clock.Now - at;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d.TotalDays >= 2) return $"{(int)d.TotalDays}d ago";
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

    private Color RingColor() => _pane is { } pane ? _p.PaneDot(pane).Color : Colors.Transparent;

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

        // A dormant pane's pill: the attention hue for an ending worth noticing (interrupted, just before a shutdown),
        // the brand for one Perch had open, else quiet.
        (IBrush fg, IBrush line, IBrush wash) = pane.Dormant is { } d
            ? d.IsFlagged ? ((IBrush)_p.Await, (IBrush)_p.Await, (IBrush)_p.AwaitWash)
              : d.Kind == RoostDormantKind.WasOpenInPerch ? (_p.Brand, _p.BrandLine, _p.BrandWash)
              : (_p.Muted, _p.Border, _p.Surface)
            : status switch
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
        if (_thread is not null && _feed is { } f)
            EnsureThread(f);   // rebinds after a tail reset / "load earlier"; re-shows or hides that strip
        if (_thread is null) RefreshBody();
        else RefreshFooter();   // the composer's hint follows pending / running / idle
    }

    private void RefreshBody()
    {
        if (_pane is null || _parked) return;
        if (_feed is { IsLoading: false } feed)
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

    // "Load earlier": a large transcript (the history viewer's gate) takes a second, confirming click; the read
    // and decode then run off the UI thread and the thread rebinds when the whole conversation lands.
    private void OnLoadEarlier()
    {
        if (_feed is not { CanLoadEarlier: true, IsLoadingEarlier: false } feed) return;
        if (feed.EarlierIsLarge && !_earlierArmed) { _earlierArmed = true; RefreshEarlierBar(feed); return; }
        _earlierArmed = false;
        feed.LoadEarlier();
        RefreshEarlierBar(feed);
    }

    private void RefreshEarlierBar(RoostFeed feed)
    {
        _earlierBar.IsVisible = feed.CanLoadEarlier;
        _earlierText.Text = feed.IsLoadingEarlier ? "Loading earlier…"
            : _earlierArmed ? $"The whole transcript is {SessionHistory.FormatSize(feed.TranscriptBytes)} and may be slow  ·  Load anyway"
            : $"Showing the last {RoostFeed.TailLines} lines  ·  Load earlier";
    }

    private void EnsureThread(RoostFeed feed)
    {
        RefreshEarlierBar(feed);
        if (_thread is not null)
        {
            // Already materialised: at most a rebind (a tail reset / "load earlier" swapped the conversation).
            if (!ReferenceEquals(_boundConversation, feed.Conversation))
            {
                _thread.Bind(feed.Conversation);
                _boundConversation = feed.Conversation;
            }
            return;
        }
        _thread = new SessionThreadView(_p, compact: true) { Cwd = _pane?.Session.Cwd ?? "" };
        _thread.PermissionAnswered += (item, allow, mode) => PermissionAnswered?.Invoke(item, allow, mode);
        _thread.QuestionAnswered += (item, answers) => QuestionAnswered?.Invoke(item, answers);
        _thread.Bind(feed.Conversation);
        _boundConversation = feed.Conversation;
        _body.Child = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                _earlierBar,
                new LayoutTransformControl
                {
                    LayoutTransform = new ScaleTransform(SessionThreadView.CompactScale, SessionThreadView.CompactScale),
                    Child = _thread,
                },
            },
        };
    }

    private void DropThread()
    {
        if (_thread is null) return;
        _thread.Unbind();
        _thread = null;
        _boundConversation = null;
        if (_earlierBar.Parent is Panel holder) holder.Children.Remove(_earlierBar);   // reused by the next build
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
        bool errored = _pane is { Ended: false, Session.Status: SessionStatus.ApiError };
        var lines = ActivitySummary.Build(_feed.Conversation, running, sessionErrored: errored);
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
        bool perch = pane.Session.IsPerchControlled;                        // by origin: opens its window
        bool dormant = pane.IsDormant;
        bool canReply = dormant ? CanWake : perch && !pane.Ended && _feed is { IsControlled: true };
        _composer.IsVisible = canReply;
        _footerNote.IsVisible = !canReply;
        _footerNote.Text = dormant ? "Not running" : pane.Ended ? "Session ended" : perch ? "Perch session" : "Tailing · read-only";
        ToolTip.SetTip(_composer, dormant ? "Claude isn't running — your first message resumes this session" : null);
        if (dormant)
            _composer.PlaceholderText = IsWaking ? "Starting Claude…" : "Message to resume…";
        else if (canReply && _feed is { } feed)
        {
            var conv = feed.Conversation;
            _composer.PlaceholderText = conv.PendingPermission is { IsQuestion: false }
                ? "Reply — or Enter to allow, Esc to deny"
                : conv.TurnActive ? "Queue a message… (Esc interrupts)" : "Reply…";
        }
        // A dormant session opens in a Perch window (still dormant) whatever its origin.
        _footerButtonText.Text = perch || dormant ? "⤢ Open window" : "Focus terminal ↗";
        _footerButton.IsVisible = !pane.Ended;
        _takeOverButton.IsVisible = CanTakeOver && !perch && pane.IsLive;
        _terminalButton.IsVisible = dormant;
        _footer.IsVisible = true;
    }

    /// <summary>A dormant pane's send is (or stops) being checked and started. While waking the composer keeps the text
    /// read-only; <paramref name="clear"/> empties it once the message went.</summary>
    public void SetWaking(bool waking, bool clear = false)
    {
        IsWaking = waking;
        _composer.IsReadOnly = waking;
        if (clear) _composer.Text = "";
        RefreshFooter();
    }

    // Enter sends (or, with nothing typed, allows a pending permission — never a question, which is answered by
    // picking); Shift+Enter is a newline; Esc denies a pending permission, else interrupts a running turn. Tunnel
    // so these beat the TextBox's own Enter-inserts-a-newline.
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        // A dormant pane: Enter is the first send, which resumes the session. The window clears the text once it went.
        if (_pane is { IsDormant: true })
        {
            if (e.Key != global::Avalonia.Input.Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            if (!IsWaking && _composer.Text is { } typed && !string.IsNullOrWhiteSpace(typed))
                PromptSubmitted?.Invoke(Key, typed.TrimEnd());
            return;
        }
        if (_feed is not { IsControlled: true } feed) return;
        var conv = feed.Conversation;
        if (e.Key == global::Avalonia.Input.Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var text = _composer.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                if (conv.PendingPermission is { IsQuestion: false } pending) PermissionAnswered?.Invoke(pending, true, false);
            }
            else
            {
                PromptSubmitted?.Invoke(Key, text.TrimEnd());
                _composer.Text = "";
            }
            e.Handled = true;
        }
        else if (e.Key == global::Avalonia.Input.Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (conv.PendingPermission is { } pending) { PermissionAnswered?.Invoke(pending, false, false); e.Handled = true; }
            else if (conv.TurnActive) { InterruptRequested?.Invoke(Key); e.Handled = true; }
        }
    }

    /// <summary>True while this pane's composer has keyboard focus.</summary>
    public bool ComposerFocused => _composer.IsFocused;

    private Border FooterButton(TextBlock label, Action onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3), BorderThickness = new Thickness(1),
            BorderBrush = _p.Border, Background = _p.Raised, Cursor = new Cursor(StandardCursorType.Hand),
            MinHeight = 30, Child = new Border { VerticalAlignment = VerticalAlignment.Center, Child = label },
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
        var zoom = new MenuItem { Header = "Zoom / restore", InputGesture = new KeyGesture(global::Avalonia.Input.Key.Z, KeyModifiers.Control | KeyModifiers.Shift) };
        zoom.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.Zoom);
        var remove = new MenuItem { Header = "Remove from tab" };
        remove.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.RemoveFromTab);
        var open = new MenuItem
        {
            Header = pane.Session.IsPerchControlled || pane.IsDormant ? "Open full window" : "Focus terminal",
            IsEnabled = !pane.Ended,
        };
        open.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.OpenSession);
        var copy = new MenuItem { Header = "Copy resume command" };
        copy.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.CopyResume);
        flyout.Items.Add(zoom);
        flyout.Items.Add(remove);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(open);
        if (pane.IsDormant)
        {
            var terminal = new MenuItem { Header = "Resume in terminal" };
            terminal.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.ResumeInTerminal);
            flyout.Items.Add(terminal);
        }
        if (CanTakeOver && !pane.Session.IsPerchControlled && pane.IsLive)
        {
            var take = new MenuItem { Header = "Take over in Perch…" };
            take.Click += (_, _) => ActionRequested?.Invoke(Key, RoostPaneAction.TakeOver);
            flyout.Items.Add(take);
        }
        flyout.Items.Add(copy);
        flyout.Items.Add(new Separator());
        // A dormant pane isn't running, so there's nothing to hide: it's dismissed (from the Recent list too).
        var close = pane.IsDormant ? new MenuItem { Header = "Dismiss" } : new MenuItem { Header = "Close pane" };
        close.Click += (_, _) => ActionRequested?.Invoke(Key, pane.IsDormant ? RoostPaneAction.Dismiss : RoostPaneAction.Close);
        flyout.Items.Add(close);
        flyout.ShowAt(_menuButton);
    }
}
