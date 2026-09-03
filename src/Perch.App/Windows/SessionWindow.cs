using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The rich Perch-native session client (docs/session-ui-plan.md, Phase 1): Perch owns a Claude Code
/// session over the CLI's stream-json protocol (<see cref="ClaudeSessionController"/>) and renders it in
/// the "Distinct Perch identity" (<see cref="SessionPalette"/>) — a session bar (project, model, live
/// permission-mode switcher, cost/queue, interrupt), the conversation as composed turns
/// (<see cref="SessionThreadView"/> over a <see cref="SessionConversation"/>), permission prompts as
/// first-class cards answered in-UI, AskUserQuestion as a question card, and a composer. Opens on a
/// launcher (new session in a folder, or resume a recent one) unless attached to a session straight away —
/// the destination for the tray, the overlay's "+ New session" row, "Elevate to Perch", and the <c>perch</c>
/// CLI.
///
/// <para>The window is a <em>view</em>: the session itself is an app-owned <see cref="PerchSession"/>.
/// Closing the window hides the view (the process keeps running; its overlay row reopens a window onto it);
/// only the persistent "End session" button, behind a confirmation, stops the process. Starting is
/// delegated to the app via <see cref="StartRequested"/> so the app owns every live session.</para>
///
/// <para>Collision defences on the way in: a resume is refused while the id is live in a real terminal
/// (<see cref="LiveLookup"/>) or controlled by another Perch (<see cref="SessionLock.HeldByOther"/>); the
/// controller pins the id and writes the lock before the process starts.</para>
/// </summary>
internal sealed class SessionWindow : Window
{
    private static readonly string[] Modes = ["default", "auto", "plan", "acceptEdits", "bypassPermissions"];

    // What a Perch-driven session starts in when nothing is explicitly chosen — the launcher pick, then
    // settings.json's permissions.defaultMode, then this. A plain terminal `claude` would fall back to
    // "default" (ask on every action), but a Perch session answers permissions in-UI, so "auto" is the
    // friendlier default. (This is the recent change from the old "manual"/default fallback.)
    private const string FallbackMode = "auto";
    private string StartingMode => _mode ?? _defaults.PermissionMode ?? FallbackMode;
    private static readonly string[] ModelChoices = ["haiku", "sonnet", "opus", "fable"];

    // What a session launches with when nothing is chosen here: the user's settings.json values, else the
    // CLI's own built-in default. Read once per window so the pills show the real starting state, not "default".
    private readonly SessionDefaults _defaults = ClaudeUserSettings.ReadSessionDefaults();
    private const string CliDefaultModel = "opus";   // Claude Code's built-in default when settings.json sets none

    private readonly SessionPalette _p;
    private PerchSession? _session;
    private readonly SessionConversation _emptyConversation = new();
    private SessionConversation Conv => _session?.Conversation ?? _emptyConversation;
    private bool _closed;

    // Launch parameters (the launcher edits these; a targeted open sets them directly).
    private string _cwd = "";
    private string? _resumeId;
    private string? _model;
    private string? _mode;
    private string? _effort;

    // Session bar
    private readonly TextBlock _projectText, _pathText, _idText, _statusText;
    private readonly Border _idLine;
    // Composer settings pills: one joined "model │ effort" pill (the two are related — effort is per model) and
    // the permission-mode pill.
    private readonly TextBlock _modelPillText, _modePillText, _effortPillText;
    private readonly Border _modelEffortPill, _modelHalf, _effortHalf, _modePill;
    // Live usage readout beside the settings chips: cumulative tokens in/out, and context-window pressure.
    private readonly TextBlock _tokensPillText, _contextPillText;
    private readonly Border _tokensPill, _contextPill;
    // The context pill's thermometer — the overlay's own glyph/variants (OverlayCanvas.DrawThermo), at the
    // thresholds the floating UI is configured with. Show/threshold/green-segment mirror settings, pushed
    // by the app via SetContextPressureConfig; default to AppSettings' own defaults so it reads sanely if
    // that call never comes.
    private readonly ThermoGlyph _thermoGlyph;
    private bool _showContextPressure = true, _showContextGreenSegment;
    private readonly ModeGlyph _modeGlyph;
    private readonly SessionButton _interruptButton, _resumeButton, _endButton, _moreButton;

    // Centre: launcher or thread
    private readonly Panel _center;
    private readonly Control _launcher;
    private readonly SessionThreadView _thread;

    // Launcher
    private readonly AutoCompleteBox _folderBox;   // free-text project folder, searchable over past projects
    private IReadOnlyList<string> _folderSuggestions = [];  // recency-ordered projects, for Tab-completion
    private readonly SessionButton _newButton;
    private readonly StackPanel _recentsList;
    private readonly TextBox _recentsSearch;
    private readonly Border _recentsSearchFrame;
    private readonly TextBlock _recentsHeader;
    private IReadOnlyList<HistoryEntry> _allRecents = [];
    // Per-session resume estimate (context tokens, cache warmth, cost), computed lazily off-thread for the
    // rows on screen and cached by session id. _estimating guards against re-queuing one that's in flight.
    private readonly Dictionary<string, ResumeEstimate> _estimates = new();
    private readonly HashSet<string> _estimating = new();

    // A floating, viewport-relative toast for launcher errors ("live in a terminal", "already controlled by
    // another Perch", …). It sits over _center rather than at the bottom of the recents column so a long
    // recents list can't scroll the message off-screen. Auto-dismisses; click to dismiss early.
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer;

    // Composer
    private readonly Border _composerDock;
    private readonly Border _composerFrame;
    private readonly TextBox _composer;
    private readonly SessionButton _sendButton;

    /// <summary>Resolves a session id to its live (terminal-hosted) session, if any — the refuse-if-live
    /// guard's oracle. The app wires it to the monitor's latest roster.</summary>
    public Func<string, ClaudeSession?>? LiveLookup { get; set; }

    /// <summary>Starts a session on the app's behalf (so the app owns it): (cwd, model, mode, resumeId) → the
    /// live session. Throws when the process can't start.</summary>
    public Func<SessionLaunchOptions, PerchSession>? StartRequested { get; set; }

    /// <summary>The user wants a launcher for another session (the app opens a fresh window).</summary>
    public event Action? NewSessionRequested;

    /// <summary>The session this window currently views, or null on the launcher.</summary>
    public PerchSession? Session => _session;

    /// <summary>The id of the session this window views (known from launch), or null on the launcher.</summary>
    public string? SessionId => _session?.SessionId;

    public SessionWindow(SessionPalette? palette = null)
    {
        _p = palette ?? SessionPalette.Current;
        Title = "Perch session";
        Width = 880;
        Height = 740;
        MinWidth = 560;
        MinHeight = 420;
        Background = _p.Surface;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://perch/Assets/icon.ico"))); } catch { }

        // The composer and folder box sit inside their own warm frames, so the Fluent TextBox chrome
        // (hover/focus fills and borders) is switched off here — the frames carry the visual state.
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                                    "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                                    "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            Resources[key] = Brushes.Transparent;
        Resources["TextControlForeground"] = _p.Text;
        Resources["TextControlForegroundPointerOver"] = _p.Text;
        Resources["TextControlForegroundFocused"] = _p.Text;
        Resources["TextControlPlaceholderForeground"] = _p.Faint;
        Resources["TextControlPlaceholderForegroundPointerOver"] = _p.Faint;
        Resources["TextControlPlaceholderForegroundFocused"] = _p.Faint;

        // ── Session bar ──────────────────────────────────────────────────────────
        _projectText = new TextBlock { FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 15, Foreground = _p.Title };
        _pathText = new TextBlock { FontFamily = _p.Mono, FontSize = 12.5, Foreground = _p.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
        // The session id, de-emphasised beneath the path; click copies it (the id is what `--resume` wants).
        _idText = new TextBlock { FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        _idLine = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 1),
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left,
            [ToolTip.TipProperty] = "Copy session id",
            Child = _idText,
        };
        _idLine.PointerEntered += (_, _) => _idText.Opacity = 1;
        _idLine.PointerExited += (_, _) => _idText.Opacity = 0.8;
        _idLine.PointerReleased += async (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) await CopySessionIdAsync(); };
        var crumb = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = _p.BrandWash,
                    BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1), Child = SessionThreadView.MarkImage(17),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                },
                new StackPanel { Children = { _projectText, _pathText, _idLine }, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        // Session settings live with the composer (they shape the next message): "model │ effort" · permission mode.
        _modelPillText = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted, FontFamily = _p.Body, VerticalAlignment = VerticalAlignment.Center };
        _effortPillText = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted, FontFamily = _p.Body, VerticalAlignment = VerticalAlignment.Center };
        _modelHalf = PillHalf(_modelPillText, "Model — click to change");
        _modelHalf.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowModelMenu(); };
        _effortHalf = PillHalf(_effortPillText, "Effort level for this model — click to change");
        _effortHalf.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowEffortMenu(); };
        var divider = new Border { Width = 1, Background = _p.Border, Margin = new Thickness(0, -4) };
        _modelEffortPill = new Border
        {
            Background = _p.Raised2, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _modelHalf, divider, _effortHalf } },
        };
        // The mode pill wears the overlay's own badge (same chevrons, same per-mode colour) so the two read alike.
        _modeGlyph = new ModeGlyph { Neutral = _p.Muted, Plan = _p.Plan, VerticalAlignment = VerticalAlignment.Center };
        _modePillText = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted, FontFamily = _p.Body };
        _modePill = Pill(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _modeGlyph, _modePillText } },
            _p.Raised2, _p.BorderSoft);
        _modePill.Cursor = new Cursor(StandardCursorType.Hand);
        _modePill[ToolTip.TipProperty] = "Permission mode — click to change";
        _modePill.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) ShowModeMenu(); };

        // Informational (not clickable): tokens in/out this session, and how full the context window is. Both
        // hidden until a turn lands, so the launcher and a just-opened session stay uncluttered.
        _tokensPillText = new TextBlock { FontSize = 12, FontFamily = _p.Mono, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center };
        _tokensPill = Pill(_tokensPillText, _p.Raised2, _p.BorderSoft);
        _tokensPill.IsVisible = false;
        _contextPillText = new TextBlock { FontSize = 12, FontFamily = _p.Mono, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center };
        _thermoGlyph = new ThermoGlyph { VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        _contextPill = Pill(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _thermoGlyph, _contextPillText } },
            _p.Raised2, _p.BorderSoft);
        _contextPill.IsVisible = false;

        _statusText = new TextBlock { FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center };
        _interruptButton = new SessionButton(_p, "Interrupt", SessionButtonKind.Quiet, "esc", compact: true) { IsVisible = false };
        _interruptButton.Click += () => _session?.Interrupt();
        _resumeButton = new SessionButton(_p, "Resume", SessionButtonKind.Primary, compact: true) { IsVisible = false };
        _resumeButton.Click += StartSession;
        // Always present while a session runs: the one way to stop the process. Closing the window merely
        // hides this view, so the two intents can't be confused.
        _endButton = new SessionButton(_p, "End session", SessionButtonKind.Quiet, compact: true) { IsVisible = false };
        _endButton.Click += async () => await ConfirmEndAsync();
        _moreButton = new SessionButton(_p, "⋯", SessionButtonKind.Quiet, compact: true);
        _moreButton.Click += ShowMoreMenu;

        var barLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { crumb } };
        var barRight = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _statusText, _interruptButton, _resumeButton, _endButton, _moreButton },
        };
        var bar = new DockPanel { Children = { barRight, barLeft } };
        barRight[DockPanel.DockProperty] = Dock.Right;
        var barFrame = new Border
        {
            Background = _p.Surface, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12), Child = bar, [DockPanel.DockProperty] = Dock.Top,
        };

        // ── Composer ─────────────────────────────────────────────────────────────
        _composer = new TextBox
        {
            PlaceholderText = "Reply, or type / for a command", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = _p.Text, CaretBrush = _p.Brand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            MinHeight = 24, MaxHeight = 180, IsEnabled = false,
        };
        _composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        _sendButton = new SessionButton(_p, "→", SessionButtonKind.Primary, compact: true)
        {
            Width = 36, Height = 36, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(0),
            [ToolTip.TipProperty] = "Send  ↵   ·   new line  ⇧↵   ·   interrupt  esc",
        };
        _sendButton.Click += SendPrompt;
        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _modelEffortPill, _modePill, _tokensPill, _contextPill },
        };
        var cbar = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Children = { _sendButton, chips } };
        _sendButton[DockPanel.DockProperty] = Dock.Right;
        _composerFrame = new Border
        {
            MaxWidth = SessionPalette.ThreadMaxWidth, Background = _p.Raised, BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 12, 14, 10),
            Child = new StackPanel { Children = { _composer, cbar } },
        };
        _composer.GotFocus += (_, _) => _composerFrame.BorderBrush = _p.BrandLine;
        _composer.LostFocus += (_, _) => _composerFrame.BorderBrush = _p.Border;
        // Hidden on the launcher; the thread and the composer appear together once a session starts.
        _composerDock = new Border
        {
            Background = _p.Surface, BorderBrush = _p.BorderSoft, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(22, 14, 22, 16), Child = _composerFrame, [DockPanel.DockProperty] = Dock.Bottom,
            IsVisible = false,
        };

        // ── Centre ───────────────────────────────────────────────────────────────
        _thread = new SessionThreadView(_p) { IsVisible = false };
        _thread.PermissionAnswered += (item, allow, switchMode) => { _session?.AnswerPermission(item, allow, switchMode); _composer.Focus(); };
        _thread.QuestionAnswered += (item, answers) => { _session?.AnswerQuestion(item, answers); _composer.Focus(); };
        // Live theme swap: the shared palette's brushes are re-tinted in place (so chrome and text follow with
        // no work here), but the thread's markdown baked its code-syntax for the old light/dark side — rebuild it.
        ThemeService.Changed += OnThemeChanged;

        _folderBox = new AutoCompleteBox
        {
            FontFamily = _p.Mono, FontSize = 13, Foreground = _p.Text,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        FolderSearchBox.Configure(_folderBox, "Project folder…",
            _p.Text, _p.Muted, _p.Surface, _p.Border, _p.Body, _p.Mono, borderless: true);
        // Empty + focused pops the whole recency-ordered list (a 0-char search); typing then filters it. The
        // open is posted so it runs after the click that focused the box settles — setting it inline in
        // GotFocus loses the race with the pointer press and the list never appears on the first click.
        _folderBox.GotFocus += (_, _) => Dispatcher.UIThread.Post(OpenFolderDropdownIfEmpty, DispatcherPriority.Input);
        // Tab completes to the most recent match (CLI-style, without launching); Enter launches.
        _folderBox.AddHandler(KeyDownEvent, OnFolderKeyDown, RoutingStrategies.Tunnel);
        _folderBox.TextChanged += (_, _) => UpdateNewEnabled();
        _newButton = new SessionButton(_p, "New session", SessionButtonKind.Primary) { Enabled = false };
        _newButton.Click += StartFromFolderBox;
        _recentsList = new StackPanel { Spacing = 2 };
        _recentsHeader = new TextBlock
        {
            Text = "RESUME RECENT", FontFamily = _p.Mono, FontSize = 11, LetterSpacing = 1.1,
            Foreground = _p.Faint, Margin = new Thickness(0, 0, 0, 8),
        };
        _recentsSearch = new TextBox
        {
            FontFamily = _p.Body, FontSize = 13, Foreground = _p.Text,
            PlaceholderText = "Search every session on this machine — name, folder or id",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _recentsSearch.TextChanged += (_, _) => RenderRecents();
        _recentsSearchFrame = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(12, 6), IsVisible = false,
            Margin = new Thickness(0, 0, 0, 10), Child = _recentsSearch,
        };
        _launcher = BuildLauncher();

        // Floating error toast, layered over the centre so it's visible whatever the recents list is doing.
        _toastText = new TextBlock
        {
            FontFamily = _p.Body, FontSize = 12.5, Foreground = _p.Err, TextWrapping = TextWrapping.Wrap,
        };
        _toast = new Border
        {
            Background = _p.Surface, BorderBrush = _p.Err, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(14, 11),
            Margin = new Thickness(16, 16, 16, 0), MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false, Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #40000000"),
            Child = _toastText,
        };
        _toast.PointerReleased += (_, _) => HideToast();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => HideToast();

        _center = new Panel { Children = { _launcher, _thread, _toast } };
        Content = new DockPanel { Children = { barFrame, _composerDock, _center } };

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        RefreshBar();
    }

    // ── Attaching to a session ────────────────────────────────────────────────────

    /// <summary>Points this window at a live (or ended) session: the thread shows its conversation, the bar
    /// follows its state, the launcher hides. Reattaching the same session is a no-op.</summary>
    public void Attach(PerchSession session)
    {
        if (ReferenceEquals(_session, session)) { ShowThread(); return; }
        Detach();
        _session = session;
        _cwd = session.Cwd;
        _model = session.Model;
        _mode = session.PermissionMode;
        _effort = session.Effort;
        _folderBox.Text = session.Cwd;
        session.Conversation.StateChanged += RefreshBar;
        session.Ended += OnSessionEnded;
        _thread.Bind(session.Conversation);
        ShowThread();
        ApplyRunState();
        RefreshBar();
        if (session.IsRunning) _composer.Focus();
    }

    private void Detach()
    {
        if (_session is not { } s) return;
        s.Conversation.StateChanged -= RefreshBar;
        s.Ended -= OnSessionEnded;
        _session = null;
    }

    // Composer / buttons follow whether the viewed session is running or has ended.
    private void ApplyRunState()
    {
        bool running = _session is { IsRunning: true };
        _composer.IsEnabled = running;
        _composer.PlaceholderText = running ? "Reply, or type / for a command" : "Session ended — Resume to pick it back up";
        _endButton.IsVisible = running;
        _resumeButton.IsVisible = _session is { HasEnded: true } && _session.SessionId is not null;
        HideToast();
    }

    // ── Public entry points ───────────────────────────────────────────────────────

    /// <summary>Fills the launcher's "Resume recent" list off the UI thread. No-op once a session runs.</summary>
    public void LoadRecents(IReadOnlySet<string> activeSessionIds)
    {
        if (_session is not null) return;
        System.Threading.Tasks.Task.Run(() => SessionHistory.ListAll(activeSessionIds)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || _session is not null) return;
                PopulateRecents(t.Result);
            });
        });
    }

    /// <summary>Opens straight onto an existing session (<c>--resume</c>) — the elevate / CLI path.</summary>
    public void ResumeSession(string sessionId, string cwd)
    {
        _resumeId = sessionId;
        _cwd = cwd;
        _folderBox.Text = cwd;
        StartSession();
    }

    /// <summary>Opens straight onto a fresh session in <paramref name="cwd"/> — the CLI's <c>perch [dir]</c>.</summary>
    public void StartNew(string cwd, string? model = null, string? mode = null)
    {
        _resumeId = null;
        _cwd = cwd;
        _model = model;
        _mode = mode;
        _folderBox.Text = cwd;
        StartSession();
    }

    // ── Launcher ─────────────────────────────────────────────────────────────────

    private Control BuildLauncher()
    {
        var browse = new SessionButton(_p, "Choose folder…", SessionButtonKind.Ghost, compact: true);
        browse.Click += async () => await BrowseAsync();
        var folderFrame = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(12, 5, 5, 5),
            Child = new DockPanel { Children = { browse, _folderBox } },
        };
        browse[DockPanel.DockProperty] = Dock.Right;
        browse.Margin = new Thickness(8, 0, 0, 0);

        // Just the launch button — no model picker (Enter in the folder box starts too; the model can be
        // changed from the top-bar pill once the session is running).
        var row2 = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _newButton },
        };

        var recents = new Border
        {
            BorderBrush = _p.Separator, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0),
            Margin = new Thickness(0, 20, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    _recentsHeader,
                    _recentsSearchFrame,
                    _recentsList,
                },
            },
        };

        var column = new StackPanel
        {
            MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 0,
            Children =
            {
                new Border
                {
                    Width = 46, Height = 46, CornerRadius = new CornerRadius(13), Background = _p.BrandWash,
                    BorderBrush = _p.BrandLine, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14), Child = SessionThreadView.MarkImage(30),
                },
                new TextBlock
                {
                    Text = "Start a session", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 19,
                    Foreground = _p.Title, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5),
                },
                new TextBlock
                {
                    Text = "Point Perch at a project, or pick up where a past session left off.", FontFamily = _p.Body,
                    FontSize = 14, Foreground = _p.Muted, HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18),
                },
                folderFrame,
                new Border { Height = 10 },
                row2,
                recents,
            },
        };
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(22, 36, 22, 24), Child = column },
        };
    }

    // Drops the recent-projects list open when the box is empty (a 0-char search). Guarded so it never opens
    // an empty popup or fights a session that started in the meantime.
    private void OpenFolderDropdownIfEmpty()
    {
        if (_session is null && _folderSuggestions.Count > 0 && string.IsNullOrEmpty(_folderBox.Text))
            _folderBox.IsDropDownOpen = true;
    }

    // The most-recent project matching what's typed (recency order; the whole list when the box is empty) —
    // the target of Tab-completion, mirroring a shell's "complete to the newest match".
    private string? TopFolderMatch()
    {
        if (_folderSuggestions.Count == 0) return null;
        var q = _folderBox.Text?.Trim() ?? "";
        return q.Length == 0
            ? _folderSuggestions[0]
            : _folderSuggestions.FirstOrDefault(f => f.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void SetFolderText(string path)
    {
        _folderBox.Text = path;
        _folderBox.CaretIndex = path.Length;   // caret to the end, as a shell would after completing
    }

    // Tab completes to the most recent match without launching (CLI path-completion); Enter launches the
    // session on whatever folder the box holds (completing first if it isn't a real folder yet).
    private void OnFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (_session is not null) return;

        if (e.Key == Key.Tab && _folderBox.IsDropDownOpen)
        {
            if (TopFolderMatch() is { } top) SetFolderText(top);
            e.Handled = true;   // never let Tab move focus or start the session
            return;
        }

        if (e.Key == Key.Enter)
        {
            // An item arrowed to in the open dropdown wins; otherwise take whatever's typed.
            if (_folderBox.IsDropDownOpen && _folderBox.SelectedItem is string sel && Directory.Exists(sel))
                SetFolderText(sel);

            var cwd = _folderBox.Text?.Trim() ?? "";
            if (Directory.Exists(cwd))
            {
                _folderBox.IsDropDownOpen = false;
                StartFromFolderBox();
                e.Handled = true;
            }
            // Not a real folder yet — complete the partial (a second Enter then launches), don't guess-launch.
            else if (_folderBox.IsDropDownOpen && TopFolderMatch() is { } top)
            {
                SetFolderText(top);
                e.Handled = true;
            }
        }
    }

    // Launch a fresh session on the folder currently in the box (the New-session button and Enter share this).
    private void StartFromFolderBox()
    {
        _resumeId = null;
        _cwd = _folderBox.Text?.Trim() ?? "";
        StartSession();
    }

    // Holds the full machine-wide list; RenderRecents applies the search filter on top of it. Only sessions
    // with a resumable id + cwd are ever offered, so filter those out up front.
    private void PopulateRecents(IReadOnlyList<HistoryEntry> entries)
    {
        _allRecents = entries.Where(e => !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Cwd)).ToList();

        // The folder box searches the distinct projects you've launched sessions in before (recency order),
        // so a familiar project is a few keystrokes — or one focus, which drops the whole list open.
        _folderSuggestions = SessionHistory.DistinctFolders(entries);
        _folderBox.ItemsSource = _folderSuggestions;

        RenderRecents();
    }

    // How many rows to show unfiltered (a recent shortlist) vs. when searching (a broader slice of the whole
    // machine — the list scrolls, so this is only a cap against pathological session counts).
    private const int RecentShortlist = 12;
    private const int SearchResultCap = 60;

    private void RenderRecents()
    {
        var query = _recentsSearch.Text?.Trim() ?? "";
        bool searching = query.Length > 0;

        var matches = (searching ? _allRecents.Where(e => MatchesSearch(e, query)) : _allRecents)
            .Take(searching ? SearchResultCap : RecentShortlist)
            .ToList();

        _recentsList.Children.Clear();
        foreach (var e in matches)
            _recentsList.Children.Add(RecentRow(e));
        if (matches.Count == 0)
            _recentsList.Children.Add(new TextBlock
            {
                Text = searching ? "no sessions match that search" : "no past sessions yet",
                FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint, Margin = new Thickness(10, 4),
            });

        _recentsHeader.Text = searching ? "SEARCH RESULTS" : "RESUME RECENT";
        // The search box only earns its space once there's a corpus to search.
        _recentsSearchFrame.IsVisible = _allRecents.Count > 0;

        EnsureEstimates(matches);
    }

    // Computes the resume estimate for any on-screen row that lacks one, off the UI thread, then re-renders
    // once so the freshly-cached figures appear. Only the displayed rows pay the transcript read, and each is
    // computed once (cached by session id), so typing in the search box stays cheap.
    private void EnsureEstimates(IReadOnlyList<HistoryEntry> shown)
    {
        var todo = shown
            .Where(e => !e.IsActive && !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Path)
                     && !_estimates.ContainsKey(e.SessionId) && _estimating.Add(e.SessionId))
            .ToList();
        if (todo.Count == 0) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            var results = new List<(string Id, ResumeEstimate Est)>();
            foreach (var e in todo)
            {
                try
                {
                    var (used, window) = TranscriptReader.ReadContextUsage(e.Path, e.Cwd);
                    results.Add((e.SessionId, ResumeEstimate.Compute(used, window, DateTime.Now - e.LastUpdated)));
                }
                catch { results.Add((e.SessionId, default)); }
            }
            return results;
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || _session is not null) return;
                foreach (var (id, est) in t.Result) { _estimates[id] = est; _estimating.Remove(id); }
                RenderRecents();   // repaint the rows now their estimates are known
            });
        });
    }

    // Every space-separated term must appear somewhere across the session's name, folder path or id
    // (case-insensitive) — so "perch social" narrows to sessions matching both.
    private static bool MatchesSearch(HistoryEntry e, string query)
    {
        var hay = $"{e.ProjectName}\n{e.Title}\n{e.Cwd}\n{e.SessionId}";
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (hay.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }

    private Control RecentRow(HistoryEntry e)
    {
        bool live = e.IsActive;
        var dot = new Ellipse { Width = 8, Height = 8, Fill = live ? _p.Err : _p.Faint, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = e.ProjectName, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 14, Foreground = _p.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock
        {
            Text = live ? "live in a terminal — can't control" : (string.IsNullOrWhiteSpace(e.Title) ? e.Cwd : e.Title),
            FontFamily = live ? _p.Body : _p.Mono, FontSize = 12.5, Foreground = _p.Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var when = new TextBlock
        {
            Text = live ? "now" : $"{e.RelativeTime} · {e.SizeLabel}", FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0),
        };
        var text = new StackPanel { Children = { name, sub }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        // The resume estimate (once computed off-thread): what resuming this session will re-send, whether its
        // cache is likely warm, the theoretical 5-hour share, and a dollar figure.
        if (!live && e.SessionId is { } id && _estimates.TryGetValue(id, out var est) && est.HasData)
        {
            text.Children.Add(new TextBlock
            {
                Text = ResumeEstimateLine(est), FontFamily = _p.Mono, FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
                Foreground = est.Warmth == CacheWarmth.Cold ? _p.Await : _p.Faint,
                [ToolTip.TipProperty] = ResumeEstimateTip(est),
            });
        }
        var row = new DockPanel { Children = { dot, when, text } };
        when[DockPanel.DockProperty] = Dock.Right;
        var frame = new Border
        {
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(10, 9), Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = row, Opacity = live ? 0.75 : 1,
        };
        frame.PointerEntered += (_, _) => frame.Background = _p.Raised2;
        frame.PointerExited += (_, _) => frame.Background = Brushes.Transparent;
        frame.PointerReleased += async (_, ev) =>
        {
            if (ev.InitialPressMouseButton != MouseButton.Left) return;
            if (live)
            {
                LaunchFail($"{e.DisplayName} is live in a terminal — Perch can't take it over while it's running. " +
                           "Close it there, or use “Elevate to Perch” on its overlay row.");
                return;
            }
            if (!await ConfirmHeavyResumeAsync(e)) return;
            _resumeId = e.SessionId;
            _cwd = e.Cwd;
            _folderBox.Text = e.Cwd;
            StartSession();
        };
        return frame;
    }

    // A resume that would spend more than this share of a 5-hour window gets an "are you sure?" with the
    // impact spelled out, so a heavy old session isn't reopened (and its whole context re-billed) by reflex.
    private const double HeavyResumeThresholdPercent = 5.0;

    // True to proceed with the resume. Warns first for a session whose estimate exceeds the threshold; when no
    // estimate is known (never computed, or the session has no usage on disk) there's nothing to warn about.
    private async System.Threading.Tasks.Task<bool> ConfirmHeavyResumeAsync(HistoryEntry e)
    {
        if (e.SessionId is not { } id || !_estimates.TryGetValue(id, out var est) || !est.HasData)
            return true;
        if (est.FiveHourPercent <= HeavyResumeThresholdPercent)
            return true;

        var cost = est.LikelyCostUsd is { } c ? $"   ·   ≈${c:0.00} first message" : "";
        var body =
            $"Resuming continues this conversation, so your first message re-sends its whole ≈{FormatTokens(est.ContextTokens)}-token " +
            "context to the model. Rough impact of that first message:\n\n" +
            $"    ≈ {est.FiveHourPercent:0.#}% of a 5-hour usage window   (theoretical)\n" +
            $"    {est.ContextPercent:0}% of the model's {FormatTokens(est.WindowTokens)} context window\n" +
            $"    cache {WarmthWord(est.Warmth)} — {(est.Warmth == CacheWarmth.Warm ? "likely served cheaply from cache" : "the whole context re-reads at full price, then re-caches")}{cost}\n\n" +
            "Every figure is an estimate, not a bill. Resume anyway?";
        return await ConfirmDialog.ShowAsync(this, $"Resume {e.DisplayName}?", body, "Resume anyway", "Cancel");
    }

    // The one-line resume estimate under a recent row: input tokens to resume, cache warmth, theoretical 5h
    // share, and the likely dollar cost. "↩" marks it as the resume-cost readout.
    private static string ResumeEstimateLine(ResumeEstimate est)
    {
        var parts = new List<string>
        {
            $"↩ ≈{FormatTokens(est.ContextTokens)} in",
            $"cache {WarmthWord(est.Warmth)}",
            $"≈{est.FiveHourPercent:0.#}% of 5h",
        };
        if (est.LikelyCostUsd is { } c) parts.Add($"≈${c:0.00}");
        return string.Join(" · ", parts);
    }

    private string ResumeEstimateTip(ResumeEstimate est)
    {
        string cache = est.Warmth switch
        {
            CacheWarmth.Warm => $"idle {HumanIdle(est.Idle)} — the prompt cache is probably still warm, so the first message reads the context back cheaply" +
                                (est.WarmCostUsd is { } w ? $" (≈${w:0.00})." : "."),
            CacheWarmth.Cooling => $"idle {HumanIdle(est.Idle)} — the cache may have lapsed; the first message could re-bill the whole context" +
                                (est.ColdCostUsd is { } cc ? $" (≈${cc:0.00})." : "."),
            CacheWarmth.Cold => $"idle {HumanIdle(est.Idle)} — the cache has certainly gone cold, so the first message re-reads the whole context at full price" +
                                (est.ColdCostUsd is { } c ? $" (≈${c:0.00}), then re-caches." : ", then re-caches."),
            _ => "no activity timestamp, so cache warmth is unknown.",
        };
        return
            $"Resuming re-sends this session's ≈{FormatTokens(est.ContextTokens)}-token context " +
            $"({est.ContextPercent:0}% of a {FormatTokens(est.WindowTokens)} window, {ModelContext.SourceLabel(est.WindowSource)}).\n" +
            $"Cache: {cache}\n" +
            $"≈{est.FiveHourPercent:0.#}% of a 5-hour window — rough: assumes ≈{FormatTokens(est.AssumedFiveHourBudget)} input tokens per 5h, " +
            "as Anthropic's usage endpoint publishes no real token cap.";
    }

    private static string WarmthWord(CacheWarmth w) => w switch
    {
        CacheWarmth.Warm => "warm",
        CacheWarmth.Cooling => "cooling",
        CacheWarmth.Cold => "cold",
        _ => "?",
    };

    private static string HumanIdle(TimeSpan d)
    {
        if (d < TimeSpan.Zero) return "unknown";
        if (d.TotalMinutes < 1) return "under a minute";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    private async System.Threading.Tasks.Task BrowseAsync()
    {
        try
        {
            var current = _folderBox.Text?.Trim();
            var start = Directory.Exists(current) ? await StorageProvider.TryGetFolderFromPathAsync(current!) : null;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a project folder", AllowMultiple = false, SuggestedStartLocation = start,
            });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _folderBox.Text = path;
        }
        catch (Exception ex)
        {
            LaunchFail($"couldn't open the folder picker: {ex.Message}");
        }
    }

    private void UpdateNewEnabled() =>
        _newButton.Enabled = _session is null && Directory.Exists(_folderBox.Text?.Trim() ?? "");

    // ── Session lifecycle ─────────────────────────────────────────────────────────

    private void StartSession()
    {
        if (_session is { IsRunning: true }) return;
        if (!Directory.Exists(_cwd))
        {
            LaunchFail($"folder not found: {_cwd}");
            return;
        }
        if (StartRequested is not { } start)
        {
            LaunchFail("this window can't start sessions");
            return;
        }

        // Collision defences (docs/session-ui-plan.md §Phase 4 (a)): never drive an id that is already
        // running in a real terminal, or under another Perch instance.
        if (_resumeId is { } rid)
        {
            if (LiveLookup?.Invoke(rid) is { IsPerchControlled: false } live)
            {
                LaunchFail($"{live.DisplayName} is live in a terminal (PID {live.Pid}) — Perch can't take it over while " +
                           "it's running. Close it there, or use “Elevate to Perch” on its overlay row.");
                return;
            }
            if (SessionLock.HeldByOther(rid) is { } other)
            {
                LaunchFail($"session {Shorten(rid)} is already controlled by {other.Profile} (PID {other.Pid}).");
                return;
            }
        }

        PerchSession session;
        try
        {
            session = start(new SessionLaunchOptions(_cwd, _model, StartingMode, _effort, _resumeId));
        }
        catch (Exception ex)
        {
            LaunchFail($"failed to start claude: {ex.Message}");
            return;
        }
        Attach(session);
    }

    private void OnSessionEnded(PerchSession session)
    {
        if (!ReferenceEquals(session, _session)) return;
        _resumeId = session.SessionId;   // "Resume" continues this very session
        ApplyRunState();
        RefreshBar();
    }

    private async System.Threading.Tasks.Task ConfirmEndAsync()
    {
        if (_session is not { IsRunning: true } live) return;
        bool ok = await ConfirmDialog.ShowAsync(this, "End this session?",
            "Stops the Claude process and closes this window. The conversation stays on disk and can be resumed " +
            "later — from the launcher, the overlay, or `claude --resume`. (Closing the window with × instead just " +
            "hides this view; the session keeps running.)",
            "End session", "Keep running");
        // End also closes the window so an ended session can't be resumed here by reflex (which would re-send its
        // whole context and burn tokens). The app owns the PerchSession, so End() finishes the process in the
        // background regardless of this view closing.
        if (ok) { live.End(); Close(); }
    }

    private void SendPrompt()
    {
        var text = _composer.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is not { IsRunning: true } live) return;
        live.SendPrompt(text);
        _composer.Text = "";
    }

    private void LaunchFail(string message)
    {
        // In the launcher, a floating toast (not text at the foot of the recents list, which a long list
        // pushes off-screen). Once a thread is showing, the error belongs inline as a note instead.
        if (_thread.IsVisible) { Conv.AddNote(message, NoteKind.Error); return; }
        ShowToast(message);
    }

    private void ShowToast(string message)
    {
        _toastText.Text = message;
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toast.IsVisible = false;
    }

    private void ShowThread()
    {
        _launcher.IsVisible = false;
        _thread.IsVisible = true;
        _composerDock.IsVisible = true;
    }

    // ── Bar ──────────────────────────────────────────────────────────────────────

    private void RefreshBar()
    {
        var project = _cwd.Length > 0 ? (System.IO.Path.GetFileName(_cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : _cwd) : "Perch";
        _projectText.Text = _cwd.Length > 0 ? project : "New session";
        _pathText.Text = _cwd.Length > 0 ? _cwd : "choose a project to begin";
        _pathText.IsVisible = true;
        Title = _cwd.Length > 0 ? $"{project} — Perch session" : "Perch session";

        var conv = Conv;
        _idText.Text = SessionId ?? "";
        _idLine.IsVisible = SessionId is not null;
        // Model: a mid-session switch (session.Model) beats what init reported; before launch, the launcher's
        // pick, else what the session *will* start with (settings.json, else the CLI's built-in default).
        var model = _session?.Model is { Length: > 0 } switched && switched != _model ? switched
                  : conv.Model.Length > 0 ? conv.Model
                  : _model ?? _defaults.Model ?? CliDefaultModel;
        _modelPillText.Text = ShortModel(model);
        _modelHalf[ToolTip.TipProperty] = conv.Model.Length > 0 ? $"Model: {conv.Model} — click to change"
            : _model is not null ? "Model for this session — click to change"
            : _defaults.Model is not null ? "Model from settings.json — click to change"
            : "Claude Code's default model (nothing set in settings.json) — click to change";

        var mode = conv.Model.Length > 0 ? conv.PermissionMode : StartingMode;
        var modeEnum = ModeGlyph.Parse(mode);
        _modeGlyph.Mode = modeEnum;
        _modePillText.Text = SessionThreadView.ModeLabel(mode);
        _modePillText.Foreground = ModeGlyph.BrushFor(modeEnum, _p.Muted, _p.Plan);

        var effort = _session?.Effort ?? _effort ?? _defaults.Effort;
        _effortPillText.Text = effort is { Length: > 0 } ? $"{effort} effort" : "auto effort";

        UpdateUsagePills(conv, model);

        var parts = new List<string>();
        if (conv.TotalCostUsd > 0) parts.Add($"≈ ${conv.TotalCostUsd:0.00} est.");
        _statusText[ToolTip.TipProperty] = conv.TotalCostUsd > 0
            ? "The CLI's API-equivalent cost estimate for this session. On a subscription plan nothing is billed per token — it's a usage gauge only."
            : null;
        if (conv.LastTurn is { } t && t.OutputTokens > 0) parts.Add($"{t.OutputTokens} out");
        if (conv.QueuedPrompts > 0) parts.Add($"{conv.QueuedPrompts} queued");
        if (_session is { HasEnded: true }) parts.Add("ended");
        else if (conv.PendingPermission is { } pending) parts.Add(pending.IsQuestion ? "asking you" : "awaiting you");
        else if (conv.TurnActive) parts.Add("working…");
        _statusText.Text = string.Join("  ·  ", parts);

        _interruptButton.IsVisible = _session is { IsRunning: true } && conv.TurnActive;
        UpdateNewEnabled();
    }

    // Fills the two informational pills from the live conversation: cumulative tokens in/out, and how full
    // the context window is (coloured as pressure rises). Both hide until there's something to show.
    private void UpdateUsagePills(SessionConversation conv, string model)
    {
        bool haveTokens = conv.TotalOutputTokens > 0 || conv.TotalFreshInputTokens > 0;
        _tokensPill.IsVisible = haveTokens;
        if (haveTokens)
        {
            _tokensPillText.Text = $"↑ {FormatTokens(conv.TotalFreshInputTokens)}   ↓ {FormatTokens(conv.TotalOutputTokens)}";
            var lastIn = conv.LastTurn is { } lt ? $"\nLast message: {FormatTokens(lt.FreshInputTokens)} new + {FormatTokens(lt.CacheReadTokens)} cached in, {FormatTokens(lt.OutputTokens)} out" : "";
            _tokensPill[ToolTip.TipProperty] =
                $"Tokens this session — ↑ {FormatTokens(conv.TotalFreshInputTokens)} in (billed: fresh input + cache writes; cache re-reads excluded) · ↓ {FormatTokens(conv.TotalOutputTokens)} out.{lastIn}";
        }

        long ctx = conv.ContextTokens;
        _contextPill.IsVisible = ctx > 0;
        if (ctx > 0)
        {
            int window = ModelContext.WindowFor(model);
            if (ctx > window) window = (int)Math.Min(int.MaxValue, Math.Ceiling(ctx / 1_000_000.0) * 1_000_000);
            double pct = Math.Clamp((double)ctx / window * 100.0, 0, 100);
            float fill = (float)(pct / 100.0);
            _contextPillText.Text = $"ctx {FormatTokens(ctx)} · {pct:0}%";

            // The thermometer + its matching text tint follow the floating overlay: same glyph, same
            // green→yellow→orange→red variants at the same thresholds, hidden below yellow unless the
            // overlay's green-segment variant is on (and gone entirely if context pressure is off there).
            _thermoGlyph.Fill = fill;
            bool crossedYellow = fill >= _thermoGlyph.YellowThreshold;
            _thermoGlyph.IsVisible = _showContextPressure && (crossedYellow || _showContextGreenSegment);
            // Below the yellow threshold the readout stays calm (muted), matching the overlay row, which
            // shows no colour there; at/above it the text warms to the thermometer's variant colour.
            _contextPillText.Foreground = crossedYellow ? new SolidColorBrush(_thermoGlyph.VariantColor) : _p.Muted;

            _contextPill[ToolTip.TipProperty] =
                $"Context window: {FormatTokens(ctx)} of {FormatTokens(window)} ({pct:0}%). This is what every new message re-sends to the model — the fuller it gets, the more each turn costs, and a compaction is coming as it nears full.";
        }
    }

    /// <summary>Compact token count: 940 · 12k · 1.2M.</summary>
    internal static string FormatTokens(long n)
    {
        if (n < 1_000) return n.ToString();
        if (n < 1_000_000) return $"{n / 1_000.0:0.#}k";
        return $"{n / 1_000_000.0:0.0}M";
    }

    private void ShowModeMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        var configured = _defaults.PermissionMode ?? FallbackMode;
        foreach (var mode in Modes)
        {
            var item = new MenuItem
            {
                Header = SessionThreadView.ModeLabel(mode) + (mode == configured ? "  (default)" : ""),
                Icon = new ModeGlyph { Mode = ModeGlyph.Parse(mode), Neutral = _p.Muted, Plan = _p.Plan },
            };
            var chosen = mode;
            item.Click += (_, _) =>
            {
                if (_session is { IsRunning: true } live) live.SetPermissionMode(chosen);   // acked as ModeChanged
                else { _mode = chosen; RefreshBar(); }
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_modePill);
    }

    private void ShowModelMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        if (_session is not { IsRunning: true })
        {
            var reset = new MenuItem { Header = $"{ShortModel(_defaults.Model ?? CliDefaultModel)} (default)" };
            reset.Click += (_, _) => { _model = null; RefreshBar(); };
            flyout.Items.Add(reset);
        }
        foreach (var choice in ModelChoices)
        {
            var item = new MenuItem { Header = ShortModel(choice) };
            var chosen = choice;
            item.Click += (_, _) =>
            {
                if (_session is { IsRunning: true } live) live.SetModel(chosen);
                else { _model = chosen; RefreshBar(); }
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_modelHalf);
    }

    private void ShowEffortMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var level in new[] { "auto" }.Concat(ClaudeSessionController.EffortLevels))
        {
            var item = new MenuItem { Header = level };
            var chosen = level;
            item.Click += (_, _) =>
            {
                if (_session is { IsRunning: true } live) live.SetEffort(chosen);
                else { _effort = chosen == "auto" ? null : chosen; RefreshBar(); }
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(_effortHalf);
    }

    private async System.Threading.Tasks.Task CopySessionIdAsync()
    {
        if (SessionId is not { } id || Clipboard is not { } clip) return;
        try
        {
            await clip.SetTextAsync(id);
            var was = _idText.Text;
            _idText.Text = "copied ✓";
            await System.Threading.Tasks.Task.Delay(900);
            if (_idText.Text == "copied ✓") _idText.Text = was;
        }
        catch { }
    }

    private void ShowMoreMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var handBack = new MenuItem { Header = "Hand back to a terminal", IsEnabled = _session is { IsRunning: true } };
        handBack.Click += (_, _) => _session?.HandBackToTerminal();
        var copy = new MenuItem { Header = "Copy resume command", IsEnabled = SessionId is not null };
        copy.Click += async (_, _) =>
        {
            try { if (SessionId is { } id && Clipboard is { } clip) await clip.SetTextAsync($"claude --resume {id}"); }
            catch { }
        };
        var fresh = new MenuItem { Header = "New session in another folder…" };
        fresh.Click += (_, _) => NewSessionRequested?.Invoke();
        flyout.Items.Add(handBack);
        flyout.Items.Add(copy);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(fresh);
        flyout.ShowAt(_moreButton);
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────────

    // Enter sends; Shift+Enter inserts a newline (the TextBox's default).
    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // With a permission pending and nothing typed, Enter allows (mirrors the TUI). A question card is
            // answered by picking, never by a bare Enter.
            if (Conv.PendingPermission is { IsQuestion: false } pending && string.IsNullOrWhiteSpace(_composer.Text))
                _session?.AnswerPermission(pending, allow: true, switchMode: false);
            else
                SendPrompt();
            e.Handled = true;
        }
    }

    // Esc: deny a pending permission, else interrupt the running turn.
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Conv.PendingPermission is { } pending) { _session?.AnswerPermission(pending, allow: false, switchMode: false); e.Handled = true; }
        else if (_session is { IsRunning: true } live && Conv.TurnActive) { live.Interrupt(); e.Handled = true; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private Border PillHalf(Control content, string tip)
    {
        var half = new Border
        {
            Background = Brushes.Transparent, Padding = new Thickness(10, 4), Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Stretch, Child = content, [ToolTip.TipProperty] = tip,
        };
        half.PointerEntered += (_, _) => half.Background = _p.Raised;
        half.PointerExited += (_, _) => half.Background = Brushes.Transparent;
        return half;
    }

    private Border Pill(Control content, IBrush bg, IBrush line) => new()
    {
        Background = bg, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = SessionPalette.PillRadius,
        Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, Child = content,
    };

    /// <summary>Mirrors the floating overlay's context-pressure configuration onto the context pill's
    /// thermometer: whether the feature is shown at all, its yellow/orange/red colour thresholds, and
    /// whether the below-yellow green segment is drawn — so this glyph reads exactly like the overlay's.
    /// The app pushes the current settings when it builds the window.</summary>
    public void SetContextPressureConfig(bool show, int yellowPercent, int orangePercent, int redPercent, bool greenSegment)
    {
        _showContextPressure = show;
        _showContextGreenSegment = greenSegment;
        _thermoGlyph.SetThresholds(yellowPercent, orangePercent, redPercent);
        if (_session is not null) RefreshBar();   // re-evaluate visibility/colour if a session is already attached
    }

    /// <summary>"claude-opus-5" → "Opus 5"; "claude-haiku-4-5-20251001" → "Haiku 4.5"; null → "default model".</summary>
    internal static string ShortModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "default model";
        var parts = model.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].Equals("claude", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count == 0) return model;
        var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var version = string.Join(".", parts.Skip(1).TakeWhile(p => p.Length <= 2 && p.All(char.IsAsciiDigit)));
        return version.Length > 0 ? $"{name} {version}" : name;
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    // ── Headless render hooks ─────────────────────────────────────────────────────

    /// <summary>HeadlessRenderer: show the thread over synthetic events (no process), so the composed turns —
    /// bubble, prose, thinking, tool cards, a pending permission card — can be captured.</summary>
    internal void FeedSampleForRender(string cwd, string? userPrompt, IEnumerable<SessionEvent> events)
    {
        var sample = PerchSession.ForRender(cwd);
        if (userPrompt is not null) sample.Conversation.AddUserPrompt(userPrompt);
        foreach (var ev in events) sample.Conversation.Apply(ev);
        Attach(sample);
        // A render-only session has no process, so pose it as a live one.
        _composer.IsEnabled = true;
        _composer.PlaceholderText = "Reply, or type / for a command";
        _endButton.IsVisible = true;
        _resumeButton.IsVisible = false;
        RefreshBar();
    }

    /// <summary>HeadlessRenderer: the launcher with a sample recents list (and optional seeded resume
    /// estimates, since the sample sessions have no transcript on disk to compute one from).</summary>
    internal void ShowLauncherSampleForRender(string folder, IReadOnlyList<HistoryEntry> entries,
        IReadOnlyDictionary<string, ResumeEstimate>? estimates = null)
    {
        _folderBox.Text = folder;
        if (estimates is not null)
            foreach (var kv in estimates) _estimates[kv.Key] = kv.Value;
        PopulateRecents(entries);
    }

    // Closing is "close this view", never "stop the session": the app keeps the PerchSession alive and its
    // overlay row opens a new window onto it. Only the End session button stops the process.
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        ThemeService.Changed -= OnThemeChanged;
        Detach();
        base.OnClosed(e);
    }

    // A theme was applied app-wide: the shared SessionPalette already re-tinted every brush in place, so the
    // window chrome and text have followed; rebuild the thread so its markdown re-picks the new side's
    // code-syntax colours. Guarded against a window closed mid-swap.
    private void OnThemeChanged()
    {
        if (_closed) return;
        _thread.Restyle();
        InvalidateVisual();
    }
}
