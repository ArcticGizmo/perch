using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Windows;

/// <summary>One quick-action icon in the composer toolbar, mirroring a glyph the user has enabled in the
/// floating overlay. The app builds these (it owns <c>AppSettings</c> and the actions). Icon precedence:
/// <see cref="GlyphFactory"/> (an owner-drawn control matching the overlay's exact glyph, tinted with the
/// toolbar's foreground brush) → <see cref="Icon"/> (a bitmap) → <see cref="Glyph"/> (drawn as text).</summary>
internal sealed record ComposerAction(string Glyph, string Tooltip, Action<Control> Invoke, Bitmap? Icon = null,
    Func<IBrush, Control>? GlyphFactory = null, Action<Control>? MiddleInvoke = null);

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
internal sealed partial class SessionWindow : Window
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
    // Perch-managed early auto-compaction: when enabled, once the context fill reaches the threshold Perch
    // fires `/compact` itself (rather than waiting for the CLI's near-full compaction). Pushed from settings
    // by SetAutoCompactConfig and edited in the /autocompact modal. `_autoCompactArmed` disarms after a fire
    // and re-arms once the fill drops back below the threshold, so it fires once per crossing, never in a loop.
    private bool _autoCompactEnabled;
    private int _autoCompactThreshold = 80;
    private bool _autoCompactArmed = true;
    private readonly ModeGlyph _modeGlyph;
    private readonly SessionButton _interruptButton, _resumeButton, _endButton, _moreButton;

    // Centre: launcher or thread
    private readonly Panel _center;
    private readonly Control _launcher;
    private readonly SessionThreadView _thread;
    private readonly Border _jumpBottomBtn, _jumpPromptBtn;   // floating "jump to bottom" / "jump to last prompt"

    // Background attention: a paused-turn prompt (permission / question / plan) arriving while this window
    // isn't active raises a desktop toast so the user working elsewhere doesn't miss it. The decider is
    // edge-triggered (one cue per pending item); the app wires AttentionRequested to the notifier.
    private readonly SessionAttention _attention = new();

    /// <summary>Raised (title, body) when a background session needs the user's attention.</summary>
    public event Action<string, string>? AttentionRequested;

    // Launcher
    private readonly AutoCompleteBox _folderBox;   // free-text project folder, searchable over past projects
    private IReadOnlyList<string> _folderSuggestions = [];  // recency-ordered projects, for Tab-completion
    private readonly SessionButton _newButton;
    private readonly StackPanel _recentsList;
    private readonly TextBox _recentsSearch;
    private readonly Border _recentsSearchFrame;
    private readonly TextBlock _recentsHeader;
    // The "start a new session" chrome (heading, folder box, New button) and the recents section, kept apart so
    // /resume can hide the former and show only the search + list.
    private StackPanel _newSessionChrome = null!;
    private Border _recentsSection = null!;
    // A spinner shown in the recents area until the (off-thread) machine-wide session scan lands.
    private readonly Border _recentsLoadingRow;
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

    // Attachments staged on the next message (dropped/pasted images shown as chips; dropped non-image files
    // go straight into the text as a path). The tray sits above the text area and hides when empty.
    private readonly WrapPanel _attachTray;
    private readonly List<MessageAttachment> _pendingAttachments = new();

    // Composer quick-action toolbar (the overlay's enabled, actionable glyphs mirrored above the input).
    private readonly WrapPanel _composerToolbar;
    private IReadOnlyList<ComposerAction> _overlayActions = [];
    private string? _overlayActionsSig;   // guards the toolbar against needless rebuilds on every scan

    // Rich input highlighting: the composer's own text is painted transparent and a TextBlock behind it draws
    // the same text with coloured runs (slash commands, links) from ComposerHighlighter. The two share font
    // metrics + width so glyphs and caret line up; the layer is translated to follow the box's scroll.
    private readonly TextBlock _highlightLayer;
    private ScrollViewer? _composerScroll;

    // Command palette (type "/" in the composer): a popover above the composer of the built-in slash commands
    // (docs/session-slash-commands-plan.md). Driven entirely by composer text — focus stays on the composer,
    // so selection is tracked here and drawn manually rather than via ListBox focus.
    private readonly Popup _palettePopup;
    private readonly StackPanel _paletteRows;
    private IReadOnlyList<SlashCommandInfo> _paletteItems = [];
    private int _paletteIndex;

    // Skills (user/project/plugin slash commands) offered in the palette below the built-ins. Discovered off
    // the UI thread from disk + the session's advertised command list; rebuilt when the cwd or that list
    // changes. See SkillCatalog + MaybeRebuildSkills.
    private IReadOnlyList<SlashCommandInfo> _skillCommands = [];
    private (string Cwd, int Advertised) _skillsBuiltFor = ("", -1);

    // File mentions (type "@" in the composer): a popover of the project's files, fuzzy-filtered, mirroring the
    // command palette (docs/session-composer-enhancements-plan.md §1). The file list is scanned off the UI
    // thread once per cwd; _mentionStart is the caret index of the "@" that opened the current token.
    private readonly Popup _mentionPopup;
    private readonly StackPanel _mentionRows;
    private IReadOnlyList<string> _projectFiles = [];
    private string _projectFilesFor = "";
    private IReadOnlyList<string> _mentionItems = [];
    private int _mentionIndex;
    private int _mentionStart = -1;

    // Input history (↑/↓ in the composer recalls this session's past prompts). _historyIndex is -1 when not
    // navigating; _historyDraft stashes the in-progress text so ↓ past the newest restores it.
    private int _historyIndex = -1;
    private string _historyDraft = "";
    private bool _suppressHistoryReset;

    // In-window /autocompact modal: a scrim + card with an on/off toggle and a threshold slider.
    private Panel _autoCompactOverlay = null!;

    // In-window /usage readout: a scrim + card of labelled percentage bars (5-hour, weekly, model-scoped,
    // monthly spend) with a time-to-reset countdown on each — the account rate-limit picture, painted from
    // the same UsageInfo the floating overlay strip uses, so it never dumps the CLI's markdown into the thread.
    private Panel _usageOverlay = null!;
    private StackPanel _usageBars = null!;
    private TextBlock _usageStatus = null!;
    private bool _usageRefreshing;

    // In-window /resume quick-open: a searchable, keyboard-navigable overlay of this project's sessions.
    private Panel _resumeOverlay = null!;
    private TextBox _resumeSearch = null!;
    private StackPanel _resumeList = null!;
    private Border _resumeSpinnerRow = null!;
    private TextBlock _resumeSubtitle = null!;
    private IReadOnlyList<HistoryEntry> _resumeAll = [];
    private List<HistoryEntry> _resumeShown = new();
    private int _resumeIndex;
    private bool _resumeLoaded;

    /// <summary>Resolves a session id to its live (terminal-hosted) session, if any — the refuse-if-live
    /// guard's oracle. The app wires it to the monitor's latest roster.</summary>
    public Func<string, ClaudeSession?>? LiveLookup { get; set; }

    /// <summary>Starts a session on the app's behalf (so the app owns it): (cwd, model, mode, resumeId) → the
    /// live session. Throws when the process can't start.</summary>
    public Func<SessionLaunchOptions, PerchSession>? StartRequested { get; set; }

    /// <summary>The user wants a launcher for another session (the app opens a fresh window).</summary>
    public event Action? NewSessionRequested;

    /// <summary>A native command wants Perch's Settings window opened at a given page key (e.g. "appearance"
    /// for <c>/theme</c>). The app owns the Settings window, so it handles this.</summary>
    public event Action<string>? OpenSettingsRequested;

    /// <summary><c>/resume</c> picked a session (sessionId, cwd, newWindow): resume it. <c>newWindow</c> false
    /// replaces this window's view with the resumed session (the current one keeps running in the background);
    /// true opens it in a separate window.</summary>
    public event Action<string, string, bool>? ResumeSessionRequested;

    /// <summary>The ids of sessions currently live in a terminal (so the resume overlay can mark them). The app
    /// wires it to its monitor roster.</summary>
    public Func<IReadOnlySet<string>>? ActiveSessionIdsProvider { get; set; }

    /// <summary>The user changed the Perch auto-compaction setting in the <c>/autocompact</c> modal (enabled,
    /// threshold %). The app persists it to <c>AppSettings</c> and pushes it back to every session window.</summary>
    public event Action<bool, int>? AutoCompactChanged;

    /// <summary>A file reference was picked to open in the Markdown viewer (absolute path). The app owns the
    /// viewer window, so it handles this.</summary>
    public event Action<string>? OpenFileInViewerRequested;

    /// <summary>A file reference's "View diff" was picked (absolute path). The app opens the git tree on it.</summary>
    public event Action<string>? ViewFileDiffRequested;

    /// <summary>Supplies the last-known account usage reading (the tray's <see cref="UsageMonitorHost.Last"/>),
    /// so <c>/usage</c> can paint its overlay from the same data the floating strip uses. Null-safe: the
    /// overlay falls back to <see cref="UsageInfo.Empty"/> when unset.</summary>
    public Func<UsageInfo>? UsageProvider { get; set; }

    /// <summary>Forces a fresh usage fetch (the tray's <see cref="UsageMonitorHost.RefreshAsync"/>), so the
    /// <c>/usage</c> overlay can show current numbers on open and on the Refresh button — independent of the
    /// 5-minute poll and of whether the overlay's usage strip is even enabled.</summary>
    public Func<Task<UsageInfo>>? UsageRefresh { get; set; }

    /// <summary>The session this window currently views, or null on the launcher.</summary>
    public PerchSession? Session => _session;

    /// <summary>The id of the session this window views (known from launch), or null on the launcher.</summary>
    public string? SessionId => _session?.SessionId;

    /// <summary>The working directory this window is bound to (the session's cwd, a resumed recent's folder,
    /// or the launcher pick), or "" before a project is chosen. Keys the composer's project note.</summary>
    public string Cwd => _cwd;

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
        _resumeButton.Click += () => StartSession();
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
            // The foreground is transparent: the coloured copy is drawn by _highlightLayer behind it. The caret
            // stays visible (its own brush), and the placeholder uses its own brush too, so the empty state reads.
            FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = Brushes.Transparent, CaretBrush = _p.Brand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            MinHeight = 24, MaxHeight = 180, IsEnabled = false,
        };
        _composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // The highlight layer sits behind the box with identical metrics so its glyphs sit under the real ones;
        // it follows the box's internal scroll via a translate transform once the template is up.
        _highlightLayer = new TextBlock
        {
            FontSize = SessionPalette.ProseSize, FontFamily = _p.Body, Foreground = _p.Text,
            TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top,
        };
        _composer.TemplateApplied += (_, e) =>
        {
            _composerScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_composerScroll is { } sv)
                sv.ScrollChanged += (_, _) => _highlightLayer.RenderTransform = new TranslateTransform(0, -sv.Offset.Y);
        };
        // The coloured highlight layer sits ON TOP of the transparent-text box (hit-test-transparent) so the
        // box's selection rectangle paints *behind* the glyphs — otherwise selecting text hid it under a solid
        // accent block. The selection brush is a soft brand wash that reads under the coloured glyphs.
        _composer.SelectionBrush = _p.BrandWash;
        _composer.SelectionForegroundBrush = Brushes.Transparent;   // the layer already draws the (coloured) glyphs
        var textArea = new Panel { ClipToBounds = true, Children = { _composer, _highlightLayer } };
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
        // The quick-action toolbar (enabled overlay glyphs, filled by SetComposerActions) and the staged
        // attachments tray sit above the text area; both hide until they have content.
        _composerToolbar = new WrapPanel { IsVisible = false };
        // The quick-action glyphs fill the row's left; the changed-files toggle sits at its far right.
        _changesToggle = BuildChangesToggle();
        var composerHeader = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 9),
            Children = { _changesToggle, _composerToolbar },
        };
        _changesToggle[DockPanel.DockProperty] = Dock.Right;
        _attachTray = new WrapPanel { IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
        var composerStack = new StackPanel { Children = { composerHeader, _attachTray, textArea, cbar } };
        _composerFrame = new Border
        {
            MaxWidth = SessionPalette.ThreadMaxWidth, Background = _p.Raised, BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 12, 14, 10),
            Child = composerStack,
        };
        _composer.GotFocus += (_, _) => _composerFrame.BorderBrush = _p.BrandLine;
        _composer.LostFocus += (_, _) => _composerFrame.BorderBrush = _p.Border;
        _composer.TextChanged += (_, _) =>
        {
            UpdateHighlight();
            UpdatePaletteFromText();
            UpdateMentionsFromText();
            if (!_suppressHistoryReset) _historyIndex = -1;   // any real edit stops history navigation
        };

        // Drag-and-drop files/images onto the composer, and paste images from the clipboard. Both the frame
        // and the inner text box are drop targets, and the handlers run even if the TextBox marks the event
        // handled (handledEventsToo) — a file drop over the text area must still reach us.
        DragDrop.SetAllowDrop(_composerFrame, true);
        DragDrop.SetAllowDrop(_composer, true);
        _composerFrame.AddHandler(DragDrop.DragOverEvent, OnComposerDragOver, handledEventsToo: true);
        _composerFrame.AddHandler(DragDrop.DropEvent, OnComposerDrop, handledEventsToo: true);

        // The command palette floats above the composer frame; it lives inside the composer stack so it shares
        // the tree (Popups take no layout space).
        _paletteRows = new StackPanel { Spacing = 1 };
        var paletteHint = new TextBlock
        {
            Text = "↑↓ select   ·   ↹ complete   ·   ↵ run   ·   esc dismiss",
            FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, Margin = new Thickness(9, 6, 9, 3),
        };
        _palettePopup = new Popup
        {
            PlacementTarget = _composerFrame, Placement = PlacementMode.Top,
            HorizontalOffset = 0, VerticalOffset = -8, IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(6, 6, 6, 2),
                MinWidth = 420, MaxWidth = SessionPalette.ThreadMaxWidth,
                BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000"),
                Child = new StackPanel
                {
                    Children =
                    {
                        // The full list (a bare "/" shows every command) can be long — scroll it, and keep the
                        // selected row in view as the arrows move through it.
                        new ScrollViewer
                        {
                            MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _paletteRows,
                        },
                        paletteHint,
                    },
                },
            },
        };
        composerStack.Children.Add(_palettePopup);

        // The file-mention popup mirrors the command palette (type "@" for a fuzzy file picker).
        _mentionRows = new StackPanel { Spacing = 1 };
        _mentionPopup = new Popup
        {
            PlacementTarget = _composerFrame, Placement = PlacementMode.Top,
            HorizontalOffset = 0, VerticalOffset = -8, IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(6, 6, 6, 2),
                MinWidth = 420, MaxWidth = SessionPalette.ThreadMaxWidth,
                BoxShadow = BoxShadows.Parse("0 10 30 0 #55000000"),
                Child = new StackPanel
                {
                    Children =
                    {
                        new ScrollViewer
                        {
                            MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _mentionRows,
                        },
                        new TextBlock
                        {
                            Text = "↑↓ select   ·   ↹/↵ insert path   ·   esc dismiss",
                            FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, Margin = new Thickness(9, 6, 9, 3),
                        },
                    },
                },
            },
        };
        composerStack.Children.Add(_mentionPopup);
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
        // File references in tool cards route their open/diff intents up to the app (which owns those windows).
        _thread.OpenFileRequested += p => OpenFileInViewerRequested?.Invoke(p);
        _thread.ViewDiffRequested += p => ViewFileDiffRequested?.Invoke(p);
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
        _recentsLoadingRow = new Border
        {
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 11, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new LoadingSpinner { Stroke = _p.Brand, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = "Finding your sessions…", FontFamily = _p.Mono, FontSize = 12.5,
                        Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
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

        // Floating scroll buttons, bottom-right over the thread (above the composer). Shown only when useful.
        _jumpPromptBtn = JumpButton("↑", "Jump to the previous prompt", () => _thread.JumpToPreviousPrompt());
        _jumpBottomBtn = JumpButton("↓", "Jump to the latest", () => _thread.JumpToBottom());
        var jumpStack = new StackPanel
        {
            Orientation = Orientation.Vertical, Spacing = 9,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 20, 16),
            Children = { _jumpPromptBtn, _jumpBottomBtn },
        };
        _thread.ScrollStateChanged += UpdateJumpButtons;

        BuildResumeOverlay();
        BuildAutoCompactOverlay();
        BuildUsageOverlay();
        _center = new Panel { Children = { _launcher, _thread, jumpStack, _toast, _resumeOverlay, _autoCompactOverlay, _usageOverlay } };
        _changesPanel = BuildChangesPanel();   // docked to the right of the centre; hidden until toggled on
        // Dock order matters: the changed-files panel docks Right *before* the composer docks Bottom, so the
        // panel spans the full height (down past the composer) and the composer + thread stay aligned to its
        // left — rather than the composer running full-width underneath the panel.
        Content = new DockPanel { Children = { barFrame, _changesPanel, _composerDock, _center } };

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        // Focus changes can turn a pending prompt into a "background" one (or acknowledge it), so re-evaluate.
        Activated += (_, _) => MaybeAlert(active: true);
        Deactivated += (_, _) => MaybeAlert(active: false);
        RenderComposerToolbar();   // the attach buttons show from the start; the app adds overlay actions later
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
        session.Conversation.StateChanged += OnStateForAlert;
        session.TitleChanged += RefreshBar;
        session.Ended += OnSessionEnded;
        session.Conversation.Changed += OnConversationChangedForChanges;   // live-refresh the changed-files panel
        _thread.Cwd = session.Cwd;   // set before Bind so tool cards built during materialisation arm file refs
        _thread.Bind(session.Conversation);
        ScanProjectFilesAsync();     // warm the "@"-mention file list for this project
        ShowThread();
        if (_changesOpen) RefreshChangesNow();
        ApplyRunState();
        RefreshBar();
        if (session.IsRunning) _composer.Focus();
    }

    private void Detach()
    {
        if (_session is not { } s) return;
        s.Conversation.StateChanged -= RefreshBar;
        s.Conversation.StateChanged -= OnStateForAlert;
        s.TitleChanged -= RefreshBar;
        s.Ended -= OnSessionEnded;
        s.Conversation.Changed -= OnConversationChangedForChanges;
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
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || _session is not null) return;
                if (t.IsCompletedSuccessfully) PopulateRecents(t.Result);
                else RecentsLoadFailed();   // stop the spinner rather than leave it turning forever
            });
        });
    }

    // The scan couldn't complete — drop the spinner and say so, in place of the recents list.
    private void RecentsLoadFailed()
    {
        _recentsLoadingRow.IsVisible = false;
        _recentsList.Children.Clear();
        _recentsList.Children.Add(new TextBlock
        {
            Text = "couldn't read past sessions", FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint,
            Margin = new Thickness(10, 4),
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

        _recentsSection = new Border
        {
            BorderBrush = _p.Separator, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0),
            Margin = new Thickness(0, 20, 0, 0),
            Child = new StackPanel
            {
                Children =
                {
                    _recentsHeader,
                    _recentsSearchFrame,
                    _recentsLoadingRow,
                    _recentsList,
                },
            },
        };

        // The new-session chrome — hidden in resume-picker mode, where only the search + list show.
        _newSessionChrome = new StackPanel
        {
            Spacing = 0,
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
            },
        };

        var column = new StackPanel
        {
            MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 0,
            Children = { _newSessionChrome, _recentsSection },
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
        _recentsLoadingRow.IsVisible = false;
        _allRecents = entries.Where(e => !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Cwd)).ToList();

        // The folder box searches the distinct projects you've launched sessions in before (recency order),
        // so a familiar project is a few keystrokes — or one focus, which drops the whole list open.
        _folderSuggestions = SessionHistory.DistinctFolders(entries);
        _folderBox.ItemsSource = _folderSuggestions;
        // If the user is already sitting in the (empty) folder box waiting, drop the freshly-loaded list open
        // now rather than making them click away and back.
        if (_folderBox.IsFocused) OpenFolderDropdownIfEmpty();

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
            _recentsList.Children.Add(RecentRow(e, ResumeInLauncher));
        if (matches.Count == 0)
            _recentsList.Children.Add(new TextBlock
            {
                Text = searching ? "no sessions match that search" : "no past sessions yet",
                FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint, Margin = new Thickness(10, 4),
            });

        _recentsHeader.Text = searching ? "SEARCH RESULTS" : "RESUME RECENT";
        // The search box only earns its space once there's a corpus to search.
        _recentsSearchFrame.IsVisible = _allRecents.Count > 0;

        EnsureEstimates(matches, RenderRecents);
    }

    // Two cwds name the same project when their paths match (trailing separators + case ignored).
    private static bool SameProject(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
        && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Computes the resume estimate for any on-screen row that lacks one, off the UI thread, then re-renders
    // once so the freshly-cached figures appear. Only the displayed rows pay the transcript read, and each is
    // computed once (cached by session id), so typing in the search box stays cheap.
    private void EnsureEstimates(IReadOnlyList<HistoryEntry> shown, Action reRender)
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
                if (_closed) return;
                foreach (var (id, est) in t.Result) { _estimates[id] = est; _estimating.Remove(id); }
                reRender();   // repaint the rows (launcher or resume overlay) now their estimates are known
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

    // Shared row for the launcher recents and the in-window resume overlay. onChoose runs after the guards
    // (live-check + heavy-resume confirm) pass; selected paints the keyboard-highlighted row.
    private Control RecentRow(HistoryEntry e, Func<HistoryEntry, System.Threading.Tasks.Task> onChoose, bool selected = false)
    {
        // Three states: already controlled by Perch (multi-UI — open another window on it), live in a real
        // terminal (can't take over), or a plain resumable session on disk.
        var liveSession = e.SessionId is { } lid ? LiveLookup?.Invoke(lid) : null;
        bool perchOpen = liveSession is { IsPerchControlled: true };
        bool terminalLive = e.IsActive && !perchOpen;

        var dot = new Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = terminalLive ? _p.Err : perchOpen ? _p.Brand : _p.Faint,
        };
        // Project name, with the /rename custom title appended inline when the session has one.
        var name = new TextBlock { FontFamily = _p.Body, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines?.Add(new Run(e.ProjectName) { FontWeight = FontWeight.SemiBold, Foreground = _p.Title });
        if (!string.IsNullOrWhiteSpace(e.Title))
            name.Inlines?.Add(new Run($"  ·  {e.Title}") { Foreground = _p.Muted });
        var sub = new TextBlock
        {
            Text = terminalLive ? "live in a terminal — can't control"
                 : perchOpen ? "open in Perch — opens another window"
                 : e.Cwd,
            FontFamily = terminalLive || perchOpen ? _p.Body : _p.Mono, FontSize = 12.5,
            Foreground = perchOpen ? _p.Brand : _p.Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var when = new TextBlock
        {
            Text = terminalLive ? "now" : perchOpen ? "open" : $"{e.RelativeTime} · {e.SizeLabel}",
            FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0),
        };
        var text = new StackPanel { Children = { name, sub }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        // The resume estimate (once computed off-thread) — only for a plain resumable session, not a live one.
        if (!e.IsActive && e.SessionId is { } id && _estimates.TryGetValue(id, out var est) && est.HasData)
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
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(10, 9),
            Background = selected ? _p.Raised2 : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = row, Opacity = terminalLive ? 0.75 : 1,
        };
        frame.PointerEntered += (_, _) => { if (!selected) frame.Background = _p.Raised2; };
        frame.PointerExited += (_, _) => { if (!selected) frame.Background = Brushes.Transparent; };
        frame.PointerReleased += async (_, ev) =>
        {
            if (ev.InitialPressMouseButton == MouseButton.Left) await ChooseResume(e, onChoose);
        };
        return frame;
    }

    // The launcher's resume: continue the picked session in this (idle) window.
    private System.Threading.Tasks.Task ResumeInLauncher(HistoryEntry e)
    {
        _resumeId = e.SessionId;
        _cwd = e.Cwd;
        _folderBox.Text = e.Cwd;
        StartSession();
        return System.Threading.Tasks.Task.CompletedTask;
    }

    // The guards every resume shares. A Perch-controlled session is already running under Perch, which supports
    // many UIs on one session — so open another window on it rather than resuming a new process. A session live
    // in a real terminal can't be taken over. Otherwise: warn before a heavy resume, then run onChoose.
    private async System.Threading.Tasks.Task ChooseResume(HistoryEntry e, Func<HistoryEntry, System.Threading.Tasks.Task> onChoose)
    {
        var liveSession = e.SessionId is { } sid ? LiveLookup?.Invoke(sid) : null;
        if (liveSession is { IsPerchControlled: true })
        {
            if (_resumeOverlay.IsVisible) CloseResumeOverlay();
            if (e.SessionId is { } id) ResumeSessionRequested?.Invoke(id, e.Cwd, true);   // → open/view the existing session
            return;
        }
        if (e.IsActive)
        {
            LaunchFail($"{e.DisplayName} is live in a terminal — Perch can't take it over while it's running. " +
                       "Close it there, or use “Elevate to Perch” on its overlay row.");
            return;
        }
        if (!await ConfirmHeavyResumeAsync(e)) return;
        await onChoose(e);
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

    /// <summary>Resumes <paramref name="sessionId"/> <em>in this window</em>, replacing whatever it currently
    /// views (the previous session keeps running under the app; <see cref="Attach"/> detaches the view). Used
    /// by the /resume overlay's default "resume here".</summary>
    public void ResumeReplace(string sessionId, string cwd)
    {
        _resumeId = sessionId;
        _cwd = cwd;
        StartSession(replace: true);
    }

    private void StartSession(bool replace = false)
    {
        // A window already driving a live session normally ignores a start; "resume here" (replace) lets it swap
        // to a different session instead — Attach detaches the current view, which the app keeps running.
        if (_session is { IsRunning: true } && !replace) return;
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
        var text = _composer.Text?.Trim() ?? "";
        if (_session is not { IsRunning: true } live) return;
        if (text.Length == 0 && _pendingAttachments.Count == 0) return;   // nothing to send
        ClosePalette();
        CloseMention();
        _historyIndex = -1;   // a fresh send ends any history navigation
        // A bare, no-argument native command runs its Perch action instead of going to the CLI as text (only
        // when there are no attachments — an attachment always means a real message to the model).
        if (_pendingAttachments.Count == 0 && !text.Contains(' ')
            && SlashCommandCatalog.CommandName(text) is { } name && RunNativeCommand(name))
        {
            _composer.Text = "";
            return;
        }
        live.SendPrompt(text, _pendingAttachments.Count > 0 ? _pendingAttachments.ToList() : null);
        _composer.Text = "";
        ClearAttachments();
    }

    // ── Composer attachments (drag-drop + paste) ───────────────────────────────────

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static bool IsImagePath(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private void OnComposerDragOver(object? sender, DragEventArgs e)
    {
        // Accept file drops (images become chips, other files insert their path); ignore anything else.
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) continue;
            if (IsImagePath(path))
                AddAttachment(new MessageAttachment
                {
                    Kind = AttachmentKind.Image, Path = path, MediaType = PerchSession.MediaTypeForPath(path),
                });
            else
                InsertPathIntoComposer(path);
        }
        if (_session is { IsRunning: true }) _composer.Focus();
    }

    // Insert a dropped (non-image) file's path at the caret — quoted if it has spaces — so Claude can read it.
    private void InsertPathIntoComposer(string path)
    {
        var token = path.Contains(' ') ? $"\"{path}\"" : path;
        var text = _composer.Text ?? "";
        int caret = Math.Clamp(_composer.CaretIndex, 0, text.Length);
        var lead = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) ? " " : "";
        var insert = lead + token + " ";
        _composer.Text = text[..caret] + insert + text[caret..];
        _composer.CaretIndex = caret + insert.Length;
    }

    // Ctrl+V handler: stage a clipboard image as an attachment if one is present. The clipboard's bitmap is
    // saved to a temp PNG so the attachment has a real path to open/preview; when there's no image, this is a
    // no-op and the normal text paste stands.
    private async System.Threading.Tasks.Task TryPasteImageAsync()
    {
        try
        {
            if (Clipboard is not { } clip) return;
            var data = await clip.TryGetDataAsync();
            if (data is null) return;
            try
            {
                if (await data.TryGetBitmapAsync() is not { } bmp) return;
                if (SaveTempBitmap(bmp) is { } path)
                    AddAttachment(new MessageAttachment { Kind = AttachmentKind.Image, Path = path, MediaType = "image/png" });
            }
            finally { (data as IDisposable)?.Dispose(); }
        }
        catch { /* clipboard read is best-effort */ }
    }

    // Write a pasted bitmap to a per-session temp folder (as PNG) so the attachment has a real path.
    private string? SaveTempBitmap(Bitmap bmp)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perch-attach", SessionId ?? "new");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            bmp.Save(path);
            return path;
        }
        catch { return null; }
    }

    private void AddAttachment(MessageAttachment a) { _pendingAttachments.Add(a); RenderAttachTray(); }
    private void RemoveAttachment(MessageAttachment a) { _pendingAttachments.Remove(a); RenderAttachTray(); }
    private void ClearAttachments() { _pendingAttachments.Clear(); RenderAttachTray(); }

    private void RenderAttachTray()
    {
        _attachTray.Children.Clear();
        foreach (var a in _pendingAttachments)
        {
            var chip = a;
            _attachTray.Children.Add(new AttachmentChip(_p, chip, removable: true, onRemove: () => RemoveAttachment(chip)));
        }
        _attachTray.IsVisible = _pendingAttachments.Count > 0;
    }

    // ── Composer quick-action toolbar ──────────────────────────────────────────────

    /// <summary>Sets the overlay-mirrored quick actions (the app builds them from the currently *enabled*
    /// overlay glyphs and knows how to perform each). The composer-native attach buttons are always shown
    /// alongside. Pushed by the app when it builds the window.</summary>
    public void SetComposerActions(IReadOnlyList<ComposerAction> actions)
    {
        // Called on every monitor scan; only redraw when the visible set actually changed (glyph + tooltip
        // capture the count/identity), so a hovering pointer isn't reset a few times a second.
        var sig = string.Join("|", actions.Select(a => $"{a.Glyph}␟{a.Tooltip}␟{a.GlyphFactory is not null}␟{a.Icon is not null}"));
        _overlayActions = actions;
        if (sig == _overlayActionsSig) return;
        _overlayActionsSig = sig;
        RenderComposerToolbar();
    }

    private void RenderComposerToolbar()
    {
        _composerToolbar.Children.Clear();
        // Composer-native attach: one paperclip — pick any file, images become chips, other files insert their
        // path. The discoverable face of drag-drop/paste. Owner-drawn (AttachGlyph), not the OS paperclip emoji
        // — that renders as "Clippy" on Windows, which we don't ship for copyright reasons.
        _composerToolbar.Children.Add(ToolbarButton("", "Attach a file or image", _ => PickAttachmentsFireAndForget(),
            glyphFactory: brush => new AttachGlyph(brush)));
        // Then the overlay's enabled, actionable glyphs — separated by a thin divider when there are any.
        if (_overlayActions.Count > 0)
        {
            _composerToolbar.Children.Add(new Border
            {
                Width = 1, Height = 18, Background = _p.Border, Margin = new Thickness(4, 6, 8, 6),
                VerticalAlignment = VerticalAlignment.Center,
            });
            foreach (var a in _overlayActions)
                _composerToolbar.Children.Add(ToolbarButton(a.Glyph, a.Tooltip, a.Invoke, a.Icon, a.GlyphFactory, a.MiddleInvoke));
        }
        _composerToolbar.IsVisible = true;
    }

    private Control ToolbarButton(string glyph, string tip, Action<Control> invoke, Bitmap? icon = null, Func<IBrush, Control>? glyphFactory = null, Action<Control>? middleInvoke = null)
    {
        Control content = glyphFactory is not null
            ? glyphFactory(_p.Muted)
            : icon is not null
                ? new Image { Source = icon, Width = 18, Height = 18, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = glyph, FontSize = 14.5, Foreground = _p.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = SessionPalette.ButtonRadius, Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = content, Margin = new Thickness(0, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center, [ToolTip.TipProperty] = tip,
        };
        b.PointerEntered += (_, _) => b.Background = _p.Raised2;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) invoke(b);
            else if (e.InitialPressMouseButton == MouseButton.Middle && middleInvoke is not null) { middleInvoke(b); e.Handled = true; }
        };
        return b;
    }

    // Fire-and-forget wrapper for the paperclip button (the toolbar action is synchronous).
    private void PickAttachmentsFireAndForget() => _ = PickAttachmentsAsync();

    // The paperclip: pick one or more files; images become chips, other files insert their path.
    private async System.Threading.Tasks.Task PickAttachmentsAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach a file or image", AllowMultiple = true,
            });
            foreach (var f in files)
            {
                var path = f.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;
                if (IsImagePath(path))
                    AddAttachment(new MessageAttachment
                    {
                        Kind = AttachmentKind.Image, Path = path, MediaType = PerchSession.MediaTypeForPath(path),
                    });
                else
                    InsertPathIntoComposer(path);
            }
        }
        catch (Exception ex) { Conv.AddNote($"couldn't attach: {ex.Message}", NoteKind.Error); }
        if (_session is { IsRunning: true }) _composer.Focus();
    }

    // ── Native command dispatch ────────────────────────────────────────────────────

    // Commands Perch answers with its own UI rather than sending to the CLI. Returns true when handled (the
    // caller then clears the composer and does not send). One place, so both the palette and a typed Enter
    // route the same way (docs/slash-command-group-a-plan.md, Step 0).
    private bool RunNativeCommand(string name)
    {
        switch (name)
        {
            case "model":  ShowModelMenu();  return true;
            case "effort": ShowEffortMenu(); return true;
            case "theme":  OpenSettingsRequested?.Invoke("appearance"); return true;
            case "config": OpenClaudeDesktop(); return true;
            case "resume": ShowResumeOverlay(); return true;
            case "login":  RunClaudeAuth("auth login");  return true;
            case "logout": RunClaudeAuth("auth logout"); return true;
            case "mcp":    _ = new McpStatusWindow(Conv.McpServers, _p).ShowDialog(this); return true;
            case "autocompact": ShowAutoCompactOverlay(); return true;
            case "usage":  ShowUsageOverlay(); return true;
            default:       return false;
        }
    }

    // /config → the Claude Desktop app (its GUI settings live there). Best-effort; a note if it isn't installed.
    private void OpenClaudeDesktop()
    {
        if (!PlatformServices.SessionLauncher.OpenClaudeDesktop())
            Conv.AddNote("couldn't open Claude Desktop — it may not be installed", NoteKind.Error);
    }

    // /login, /logout → shell out to `claude auth …` in a terminal: the OAuth flow opens a browser and prompts
    // in the terminal, which the stream-json channel can't host. Perch picks up the new auth on its next poll.
    private void RunClaudeAuth(string args)
    {
        if (PlatformServices.SessionLauncher.RunClaudeCommand(_cwd, args, TerminalApp.Auto))
            Conv.AddNote($"opened a terminal — finish in it: claude {args}");
        else
            Conv.AddNote("couldn't open a terminal for authentication", NoteKind.Error);
    }

    // ── Auto-compaction modal (/autocompact) ──────────────────────────────────────

    private CheckBox _acToggle = null!;
    private Slider _acSlider = null!;
    private TextBlock _acSliderLabel = null!;

    // A small modal: a toggle for Perch-managed early auto-compaction and a slider for the context-fill point
    // at which Perch runs /compact. Layered over the thread like the resume overlay.
    private void BuildAutoCompactOverlay()
    {
        var title = new TextBlock
        {
            Text = "Auto-compaction", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 17,
            Foreground = _p.Title,
        };
        var blurb = new TextBlock
        {
            Text = "When the context window fills past the point below, Perch runs /compact for you — "
                 + "summarising the conversation to reclaim room before the model starts costing more per turn.",
            FontFamily = _p.Body, FontSize = 12.5, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 14),
        };

        _acToggle = new CheckBox { Content = "Compact automatically", FontFamily = _p.Body, FontSize = 14, Foreground = _p.Text };

        _acSliderLabel = new TextBlock { FontFamily = _p.Mono, FontSize = 12.5, Foreground = _p.Brand, Margin = new Thickness(0, 12, 0, 2) };
        _acSlider = new Slider
        {
            Minimum = AutoCompactMin, Maximum = AutoCompactMax, TickFrequency = 5, IsSnapToTickEnabled = true,
            SmallChange = 5, LargeChange = 10,
        };
        _acSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) _acSliderLabel.Text = $"Compact at {(int)_acSlider.Value}% full";
        };
        // The slider only bites when the toggle is on.
        _acToggle.IsCheckedChanged += (_, _) =>
        {
            bool on = _acToggle.IsChecked == true;
            _acSlider.IsEnabled = on;
            _acSliderLabel.Foreground = on ? _p.Brand : _p.Faint;
        };

        var save = new SessionButton(_p, "Save", SessionButtonKind.Primary, "↵");
        save.Click += SaveAutoCompact;
        var cancel = new SessionButton(_p, "Cancel", SessionButtonKind.Quiet, "esc") { Margin = new Thickness(9, 0, 0, 0) };
        cancel.Click += CloseAutoCompactOverlay;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        actions.Children.Add(save);
        actions.Children.Add(cancel);

        var card = new Border
        {
            Background = _p.Surface, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(20),
            Width = 440, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 80, 16, 0), BoxShadow = BoxShadows.Parse("0 18 50 0 #66000000"),
            Child = new StackPanel { Children = { title, blurb, _acToggle, _acSliderLabel, _acSlider, actions } },
        };

        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
        scrim.PointerReleased += (_, _) => CloseAutoCompactOverlay();

        _autoCompactOverlay = new Panel { IsVisible = false, Children = { scrim, card } };
    }

    private void ShowAutoCompactOverlay()
    {
        _acToggle.IsChecked = _autoCompactEnabled;
        _acSlider.Value = _autoCompactThreshold;
        _acSliderLabel.Text = $"Compact at {_autoCompactThreshold}% full";
        _acSlider.IsEnabled = _autoCompactEnabled;
        _acSliderLabel.Foreground = _autoCompactEnabled ? _p.Brand : _p.Faint;
        _autoCompactOverlay.IsVisible = true;
    }

    private void CloseAutoCompactOverlay()
    {
        _autoCompactOverlay.IsVisible = false;
        if (_session is { IsRunning: true }) _composer.Focus();
    }

    // Save: update this window immediately and let the app persist + fan the setting out to sibling windows.
    private void SaveAutoCompact()
    {
        bool enabled = _acToggle.IsChecked == true;
        int threshold = Math.Clamp((int)_acSlider.Value, AutoCompactMin, AutoCompactMax);
        SetAutoCompactConfig(enabled, threshold);
        AutoCompactChanged?.Invoke(enabled, threshold);
        CloseAutoCompactOverlay();
        Conv.AddNote(enabled ? $"auto-compaction on · at {threshold}% full" : "auto-compaction off");
    }

    // Fires /compact once the context fill crosses the threshold (armed → disarmed until it drops back below),
    // but only on a settled, live session with a real completed turn — never on attach, a queued/running turn,
    // a pending permission, or while a compaction is already in flight (TurnActive covers that).
    private void MaybeAutoCompact(double pct)
    {
        if (!_autoCompactEnabled) return;
        if (pct < _autoCompactThreshold) { _autoCompactArmed = true; return; }
        if (!_autoCompactArmed) return;
        if (_session is not { IsRunning: true, HasEnded: false } live) return;
        var conv = Conv;
        if (conv.LastTurn is null || conv.TurnActive || conv.QueuedPrompts > 0 || conv.PendingPermission is not null) return;

        _autoCompactArmed = false;   // one fire per crossing
        // Defer the send so it runs after this state-change unwinds (RefreshBar is called from StateChanged).
        Dispatcher.UIThread.Post(() =>
        {
            if (_session is not { IsRunning: true, HasEnded: false } s) return;
            conv.AddNote($"auto-compacting · context reached {(int)pct}%");
            s.SendPrompt("/compact");
        });
    }

    // ── Usage overlay (/usage) ─────────────────────────────────────────────────────

    // A scrim + card that paints the account's rate-limit windows as labelled percentage bars with a
    // time-to-reset countdown on each — the same UsageInfo the floating overlay strip reads, so /usage stays
    // out of the chat and reads at a glance. Layered over the thread like the resume/autocompact overlays.
    private void BuildUsageOverlay()
    {
        _usageBars = new StackPanel { Spacing = 16 };
        _usageStatus = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
        };

        var close = new SessionButton(_p, "✕", SessionButtonKind.Quiet, compact: true) { [DockPanel.DockProperty] = Dock.Right };
        close.Click += CloseUsageOverlay;
        var titleRow = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 16), [DockPanel.DockProperty] = Dock.Top,
            Children =
            {
                close,
                new StackPanel { Children =
                {
                    new TextBlock { Text = "Plan usage", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 17, Foreground = _p.Title },
                    new TextBlock { Text = "Rate-limit windows for your Claude plan", FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint },
                } },
            },
        };

        var refresh = new SessionButton(_p, "Refresh", SessionButtonKind.Quiet, "⟳");
        refresh.Click += () => _ = RefreshUsageAsync();
        var done = new SessionButton(_p, "Done", SessionButtonKind.Primary, "esc") { Margin = new Thickness(9, 0, 0, 0) };
        done.Click += CloseUsageOverlay;
        var actions = new DockPanel
        {
            Margin = new Thickness(0, 18, 0, 0), [DockPanel.DockProperty] = Dock.Bottom,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    [DockPanel.DockProperty] = Dock.Right, Children = { refresh, done },
                },
                _usageStatus,
            },
        };

        var card = new Border
        {
            Background = _p.Surface, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(20),
            Width = 460, MaxHeight = 560, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 72, 16, 0), BoxShadow = BoxShadows.Parse("0 18 50 0 #66000000"),
            Child = new DockPanel
            {
                Children =
                {
                    titleRow, actions,
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _usageBars,
                    },
                },
            },
        };

        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
        scrim.PointerReleased += (_, _) => CloseUsageOverlay();

        _usageOverlay = new Panel { IsVisible = false, Children = { scrim, card } };
    }

    private void ShowUsageOverlay()
    {
        // Paint the cached reading immediately so the card is never empty, then kick off a fresh fetch.
        RenderUsageBars(UsageProvider?.Invoke() ?? UsageInfo.Empty, refreshing: false);
        _usageOverlay.IsVisible = true;
        _ = RefreshUsageAsync();
    }

    private void CloseUsageOverlay()
    {
        _usageOverlay.IsVisible = false;
        if (_session is { IsRunning: true }) _composer.Focus();
    }

    // Fetches a fresh reading off the UI thread (UsageMonitorHost.RefreshAsync never throws) and repaints. The
    // card shows the cached bars tagged "Refreshing…" meanwhile. Guarded against a double fetch and a card that
    // closed mid-flight. A no-op when the app didn't wire a refresh (e.g. the headless render harness).
    private async Task RefreshUsageAsync()
    {
        if (UsageRefresh is not { } refresh || _usageRefreshing) return;
        _usageRefreshing = true;
        RenderUsageBars(UsageProvider?.Invoke() ?? UsageInfo.Empty, refreshing: true);

        UsageInfo fresh;
        try { fresh = await refresh(); }
        catch { fresh = UsageProvider?.Invoke() ?? UsageInfo.Empty; }
        finally { _usageRefreshing = false; }

        if (_closed || !_usageOverlay.IsVisible) return;
        RenderUsageBars(fresh, refreshing: false);
    }

    // Rebuilds the bar list + status from a reading. Session (5h) and the weekly windows always show once real
    // data exists; the model-scoped weekly buckets and the monthly extra-usage spend show when the account has
    // them. A pristine/empty reading shows a hint instead of blank bars.
    private void RenderUsageBars(UsageInfo u, bool refreshing)
    {
        _usageBars.Children.Clear();
        var now = DateTime.Now;
        bool stale = u.IsStale(now);
        bool hasData = u.FiveHourPercent is not null || u.SevenDayPercent is not null
            || u.Scoped.Count > 0 || u.ExtraUsage is { Enabled: true };

        if (hasData)
        {
            _usageBars.Children.Add(UsageBar("Session · 5 hours", u.FiveHourPercent, u.FiveHourResetsAt, now, stale));
            _usageBars.Children.Add(UsageBar("Weekly · all models", u.SevenDayPercent, u.SevenDayResetsAt, now, stale));
            foreach (var s in u.Scoped)
                _usageBars.Children.Add(UsageBar($"Weekly · {s.Label}", s.Percent, s.ResetsAt, now, stale));
            if (u.ExtraUsage is { Enabled: true } x)
                _usageBars.Children.Add(UsageBar("Monthly extra usage", x.Percent, null, now, stale,
                    valueText: x.Compact, resetText: x.LimitReached ? "limit reached" : "rolls on the billing month"));
        }
        else if (!refreshing)
        {
            _usageBars.Children.Add(new TextBlock
            {
                Text = "No usage data yet. Perch reads this from your Claude account — if you're not signed in, run /login.",
                FontFamily = _p.Body, FontSize = 13, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
            });
        }

        _usageStatus.Text = refreshing ? "Refreshing…"
            : stale
                ? (!string.IsNullOrEmpty(u.Error) ? u.Error
                    : u.LastUpdated == DateTime.MinValue ? "No usage data yet"
                    : $"Updated {Ago(now - u.LastUpdated)} ago — couldn't refresh")
                : $"Updated {Ago(now - u.LastUpdated)} ago";
        _usageStatus.Foreground = stale && !refreshing ? _p.Await : _p.Faint;
    }

    // One labelled bar: caption + right-aligned value over a rounded track whose fill length and colour track
    // the percentage (Palette.UsageColor), with a muted "resets in …" countdown beneath. A null percent draws
    // an em-dash and an empty track; valueText replaces the percentage (the spend bar's dollar figure); when
    // stale every colour is blended toward the surface so the reading reads as "last known".
    private Control UsageBar(string caption, double? percent, DateTime? resetsAt, DateTime now, bool stale,
        string? valueText = null, string? resetText = null)
    {
        Color usage = percent is { } p ? Palette.UsageColor(Math.Clamp(p, 0, 100)) : _p.Muted.Color;
        if (stale) usage = Palette.Blend(usage, _p.Surface.Color, 0.5f);

        var cap = new TextBlock
        {
            Text = caption, FontFamily = _p.Body, FontSize = 13.5, Foreground = _p.Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var val = new TextBlock
        {
            Text = valueText ?? (percent is { } pv ? $"{(int)Math.Round(Math.Clamp(pv, 0, 100))}%" : "—"),
            FontFamily = _p.Mono, FontSize = 13.5, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(usage),
            [DockPanel.DockProperty] = Dock.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };

        var track = new Border
        {
            Height = 10, CornerRadius = new CornerRadius(5), Background = _p.Raised2, ClipToBounds = true,
            Margin = new Thickness(0, 7, 0, 0),
        };
        double fillPct = Math.Clamp(percent ?? 0, 0, 100);
        if (percent is not null && fillPct > 0)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions
                {
                    new ColumnDefinition(new GridLength(fillPct, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(Math.Max(0.0001, 100 - fillPct), GridUnitType.Star)),
                },
            };
            var fill = new Border { Background = new SolidColorBrush(usage), CornerRadius = new CornerRadius(5) };
            Grid.SetColumn(fill, 0);
            grid.Children.Add(fill);
            track.Child = grid;
        }

        var col = new StackPanel { Children = { new DockPanel { Children = { val, cap } }, track } };

        string? sub = resetText;
        if (sub is null && resetsAt is { } r && r > now) sub = $"resets in {Until(r - now)}";
        if (sub is not null)
            col.Children.Add(new TextBlock
            {
                Text = sub, FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, Margin = new Thickness(0, 5, 0, 0),
            });

        return col;
    }

    // Compact "time remaining" and "time since" phrasings for the countdowns and the status line.
    private static string Until(TimeSpan t) =>
        t.TotalDays >= 1  ? $"{(int)t.TotalDays}d {t.Hours}h"
      : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                          : $"{Math.Max(1, (int)t.TotalMinutes)}m";

    private static string Ago(TimeSpan t) =>
        t.TotalHours >= 1   ? $"{(int)t.TotalHours}h"
      : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m"
                            : $"{Math.Max(1, (int)t.TotalSeconds)}s";

    // ── Resume overlay (/resume) ───────────────────────────────────────────────────

    // A searchable, keyboard-navigable quick-open of this project's past sessions, layered over the thread.
    private void BuildResumeOverlay()
    {
        _resumeSearch = new TextBox
        {
            FontFamily = _p.Body, FontSize = 14, Foreground = _p.Text,
            PlaceholderText = "Search this project's sessions — name, title or id",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _resumeSearch.TextChanged += (_, _) => { _resumeIndex = 0; RenderResumeOverlay(); };
        _resumeSearch.AddHandler(KeyDownEvent, OnResumeSearchKeyDown, RoutingStrategies.Tunnel);
        var searchFrame = new Border
        {
            Background = _p.Raised, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 10),
            Child = _resumeSearch, [DockPanel.DockProperty] = Dock.Top,
        };

        _resumeList = new StackPanel { Spacing = 2 };
        _resumeSpinnerRow = new Border
        {
            Padding = new Thickness(6, 8), [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 11,
                Children =
                {
                    new LoadingSpinner { Stroke = _p.Brand, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Finding sessions…", FontFamily = _p.Mono, FontSize = 12.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _resumeSubtitle = new TextBlock { FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint };

        var close = new SessionButton(_p, "✕", SessionButtonKind.Quiet, compact: true) { [DockPanel.DockProperty] = Dock.Right };
        close.Click += CloseResumeOverlay;
        var titleRow = new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 12), [DockPanel.DockProperty] = Dock.Top,
            Children =
            {
                close,
                new StackPanel { Children =
                {
                    new TextBlock { Text = "Resume a session", FontFamily = _p.Display, FontWeight = FontWeight.Bold, FontSize = 17, Foreground = _p.Title },
                    _resumeSubtitle,
                } },
            },
        };

        var card = new Border
        {
            Background = _p.Surface, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(18),
            Width = 560, MaxHeight = 520, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 64, 16, 0), BoxShadow = BoxShadows.Parse("0 18 50 0 #66000000"),
            Child = new DockPanel
            {
                Children =
                {
                    titleRow, searchFrame, _resumeSpinnerRow,
                    new TextBlock
                    {
                        Text = "↑↓ select   ·   ↵ resume here   ·   ⇧↵ new window   ·   esc close",
                        FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint,
                        Margin = new Thickness(2, 10, 2, 0), [DockPanel.DockProperty] = Dock.Bottom,
                    },
                    new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _resumeList,
                    },
                },
            },
        };

        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };
        scrim.PointerReleased += (_, _) => CloseResumeOverlay();

        _resumeOverlay = new Panel { IsVisible = false, Children = { scrim, card } };
    }

    private void ShowResumeOverlay()
    {
        var project = _cwd;
        _resumeOverlay.IsVisible = true;
        _resumeSearch.Text = "";
        _resumeIndex = 0;
        _resumeLoaded = false;
        _resumeAll = [];
        _resumeShown = new();
        _resumeList.Children.Clear();
        _resumeSpinnerRow.IsVisible = true;
        _resumeSubtitle.Text = System.IO.Path.GetFileName(project.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : project;
        LoadResumeList(project);
        Dispatcher.UIThread.Post(() => _resumeSearch.Focus(), DispatcherPriority.Input);
    }

    private void CloseResumeOverlay()
    {
        _resumeOverlay.IsVisible = false;
        if (_session is { IsRunning: true }) _composer.Focus();
    }

    private void LoadResumeList(string project)
    {
        var active = ActiveSessionIdsProvider?.Invoke() ?? new HashSet<string>();
        System.Threading.Tasks.Task.Run(() => SessionHistory.ListAll(active)).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || !_resumeOverlay.IsVisible) return;
                _resumeSpinnerRow.IsVisible = false;
                if (!t.IsCompletedSuccessfully) { _resumeLoaded = true; RenderResumeOverlay(); return; }
                _resumeAll = t.Result
                    .Where(e => !string.IsNullOrEmpty(e.SessionId) && !string.IsNullOrEmpty(e.Cwd) && SameProject(e.Cwd, project))
                    .ToList();
                _resumeLoaded = true;
                RenderResumeOverlay();
            });
        });
    }

    private void RenderResumeOverlay()
    {
        if (!_resumeOverlay.IsVisible) return;
        var query = _resumeSearch.Text?.Trim() ?? "";
        var matches = (query.Length > 0 ? _resumeAll.Where(e => MatchesSearch(e, query)) : _resumeAll)
            .Take(SearchResultCap).ToList();
        _resumeShown = matches;
        if (matches.Count > 0) _resumeIndex = Math.Clamp(_resumeIndex, 0, matches.Count - 1);

        _resumeList.Children.Clear();
        for (int i = 0; i < matches.Count; i++)
            _resumeList.Children.Add(RecentRow(matches[i], e => ResumeChosen(e, newWindow: false), selected: i == _resumeIndex));
        if (matches.Count == 0 && _resumeLoaded)
            _resumeList.Children.Add(new TextBlock
            {
                Text = query.Length > 0 ? "no sessions match that search" : "no past sessions in this project",
                FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint, Margin = new Thickness(10, 6),
            });

        EnsureEstimates(matches, RenderResumeOverlay);

        if (_resumeIndex >= 0 && _resumeIndex < _resumeList.Children.Count)
            Dispatcher.UIThread.Post(() =>
            {
                if (_resumeIndex < _resumeList.Children.Count) (_resumeList.Children[_resumeIndex] as Control)?.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    // The overlay's resume: hand the id to the app. newWindow false replaces this window's view with the
    // resumed session; true opens it in a separate window (Shift+Enter).
    private System.Threading.Tasks.Task ResumeChosen(HistoryEntry e, bool newWindow)
    {
        CloseResumeOverlay();
        if (e.SessionId is { } id) ResumeSessionRequested?.Invoke(id, e.Cwd, newWindow);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private void MoveResume(int delta)
    {
        if (_resumeShown.Count == 0) return;
        _resumeIndex = (_resumeIndex + delta + _resumeShown.Count) % _resumeShown.Count;
        RenderResumeOverlay();
    }

    private async void OnResumeSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: MoveResume(1); e.Handled = true; break;
            case Key.Up: MoveResume(-1); e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (_resumeShown.Count > 0 && _resumeIndex >= 0 && _resumeIndex < _resumeShown.Count)
                {
                    bool newWindow = e.KeyModifiers.HasFlag(KeyModifiers.Shift);   // Shift+Enter → separate window
                    await ChooseResume(_resumeShown[_resumeIndex], x => ResumeChosen(x, newWindow));
                }
                break;
            case Key.Escape: CloseResumeOverlay(); e.Handled = true; break;
        }
    }

    // ── Rich input highlighting ────────────────────────────────────────────────────

    // Rebuilds the coloured copy of the composer text behind it. Slash commands and links get their own hue
    // (the brushes are the shared palette instances, so a theme swap re-tints them in place — no rebuild).
    private void UpdateHighlight()
    {
        var text = _composer.Text ?? "";
        _highlightLayer.Inlines?.Clear();
        foreach (var tok in ComposerHighlighter.Tokenize(text, IsKnownCommand))
        {
            var run = new Run(text.Substring(tok.Start, tok.Length));
            switch (tok.Kind)
            {
                case InputTokenKind.Command:
                    run.Foreground = _p.Brand; run.FontWeight = FontWeight.SemiBold; break;
                case InputTokenKind.Link:
                    run.Foreground = _p.Violet; run.TextDecorations = TextDecorations.Underline; break;
                default:
                    run.Foreground = _p.Text; break;
            }
            _highlightLayer.Inlines?.Add(run);
        }
    }

    // A slash token counts as a command (and gets coloured) only if it names a real one: a built-in, or a
    // command the running session advertised in init (which includes skills). Internal plumbing is excluded.
    private bool IsKnownCommand(string name)
    {
        if (SlashCommandCatalog.IsInternal(name)) return false;
        return SlashCommandCatalog.IsBuiltIn(name)
            || Conv.SlashCommands.Any(c => string.Equals(c.TrimStart('/'), name, StringComparison.OrdinalIgnoreCase))
            || _skillCommands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // ── Command palette ────────────────────────────────────────────────────────────

    // Opens/updates the palette while the composer holds a lone slash-command token: a leading "/" (which on
    // its own lists every command, alphabetically), then the name being typed. Closes once the user adds a
    // space (they're onto arguments) or the text stops starting with "/". Only meaningful while running.
    private void UpdatePaletteFromText()
    {
        var t = (_composer.Text ?? "").TrimStart();
        bool typingCommand = t.StartsWith('/') && !t.Contains(' ') && !t.Contains('\n');
        if (_session is not { IsRunning: true } || !typingCommand)
        {
            ClosePalette();
            return;
        }
        _paletteItems = SlashCommandCatalog.Search(t[1..], _skillCommands);   // "" on a bare "/" → all commands
        if (_paletteItems.Count == 0) { ClosePalette(); return; }
        _paletteIndex = Math.Clamp(_paletteIndex, 0, _paletteItems.Count - 1);
        RenderPalette();
        _palettePopup.IsOpen = true;
    }

    private void ClosePalette()
    {
        _palettePopup.IsOpen = false;
        _paletteIndex = 0;
    }

    private bool PaletteOpen => _palettePopup.IsOpen;

    private void MovePalette(int delta)
    {
        if (_paletteItems.Count == 0) return;
        _paletteIndex = (_paletteIndex + delta + _paletteItems.Count) % _paletteItems.Count;
        RenderPalette();
    }

    private void RenderPalette()
    {
        _paletteRows.Children.Clear();
        for (int i = 0; i < _paletteItems.Count; i++)
            _paletteRows.Children.Add(PaletteRow(_paletteItems[i], i));
        // Keep the highlighted row visible in the scroll region (after layout settles).
        if (_paletteIndex >= 0 && _paletteIndex < _paletteRows.Children.Count)
            Dispatcher.UIThread.Post(() =>
            {
                if (_paletteIndex < _paletteRows.Children.Count)
                    (_paletteRows.Children[_paletteIndex] as Control)?.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    private Control PaletteRow(SlashCommandInfo cmd, int index)
    {
        var name = new TextBlock { Text = "/" + cmd.Name, FontFamily = _p.Mono, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (cmd.ArgHint.Length > 0)
            head.Children.Add(new TextBlock { Text = cmd.ArgHint, FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center });
        var desc = new TextBlock
        {
            Text = cmd.Description, FontFamily = _p.Body, FontSize = 12.5, Foreground = _p.Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 0, 14, 0),
        };
        var tier = new TextBlock { Text = TierLabel(cmd.Tier), FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center };
        head[DockPanel.DockProperty] = Dock.Left;
        tier[DockPanel.DockProperty] = Dock.Right;
        var row = new DockPanel { LastChildFill = true, Children = { head, tier, desc } };
        var frame = new Border
        {
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(9, 7),
            Background = index == _paletteIndex ? _p.Raised2 : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = row,
        };
        frame.PointerEntered += (_, _) => { _paletteIndex = index; RenderPalette(); };
        frame.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { _paletteIndex = index; AcceptPalette(run: true); } };
        return frame;
    }

    // A short tag on the right of a palette row, hinting how the command behaves in Perch.
    private static string TierLabel(SlashCommandTier tier) => tier switch
    {
        SlashCommandTier.PlainText       => "text",
        SlashCommandTier.Native          => "perch",
        SlashCommandTier.SessionMutating => "session",
        SlashCommandTier.TuiOnly         => "terminal",
        SlashCommandTier.Skill           => "skill",
        _                                => "",
    };

    // Accept the highlighted command. Model/effort are fully native (pills + control requests), so accepting
    // those opens the pill rather than sending text that would fight the echo-guard. Otherwise: a no-arg
    // command with run:true sends immediately; anything with arguments is completed into the composer (with a
    // trailing space) so the user can add them, then Enter sends. Tab always just completes (run:false).
    private void AcceptPalette(bool run)
    {
        if (_paletteItems.Count == 0) { ClosePalette(); return; }
        var cmd = _paletteItems[Math.Clamp(_paletteIndex, 0, _paletteItems.Count - 1)];
        ClosePalette();

        // A native command opens its Perch surface (pill, Settings, Claude Desktop) rather than being sent.
        if (RunNativeCommand(cmd.Name)) { _composer.Text = ""; return; }

        if (run && !cmd.TakesArgs)
        {
            _composer.Text = "/" + cmd.Name;
            SendPrompt();
            return;
        }

        var text = "/" + cmd.Name + (cmd.TakesArgs ? " " : "");
        _composer.Text = text;
        _composer.CaretIndex = text.Length;
        _composer.Focus();
    }

    // ── File mentions (docs/session-composer-enhancements-plan.md §1) ─────────────

    // Scans the project's files (gitignore-aware) off the UI thread, once per cwd, for the "@" picker.
    private void ScanProjectFilesAsync()
    {
        var cwd = _cwd;
        if (string.IsNullOrEmpty(cwd) || cwd == _projectFilesFor) return;
        _projectFilesFor = cwd;
        Task.Run(() => ProjectFileScan.Scan(cwd)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            var files = t.Result.RelativePaths;
            Dispatcher.UIThread.Post(() => { if (_cwd == cwd) _projectFiles = files; });
        });
    }

    // The active "@token" at the caret, or null: a "@" that starts the line or follows whitespace, with no
    // whitespace between it and the caret. Returns the "@" index and the text typed after it.
    private (int Start, string Query)? ActiveMention()
    {
        var text = _composer.Text ?? "";
        int caret = Math.Clamp(_composer.CaretIndex, 0, text.Length);
        for (int i = caret - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '@')
                return (i == 0 || char.IsWhiteSpace(text[i - 1])) ? (i, text[(i + 1)..caret]) : null;
            if (char.IsWhiteSpace(c)) return null;   // a boundary before any "@"
        }
        return null;
    }

    private void UpdateMentionsFromText()
    {
        if (_session is not { IsRunning: true } || ActiveMention() is not { } m)
        {
            CloseMention();
            return;
        }
        _mentionStart = m.Start;
        _mentionItems = RankFiles(m.Query);
        if (_mentionItems.Count == 0) { CloseMention(); return; }
        _mentionIndex = Math.Clamp(_mentionIndex, 0, _mentionItems.Count - 1);
        RenderMention();
        _mentionPopup.IsOpen = true;
    }

    private const int MaxMentionRows = 40;

    // Fuzzy-rank the project files for a query (bare "@" → the first files alphabetically), best first.
    private IReadOnlyList<string> RankFiles(string query)
    {
        if (_projectFiles.Count == 0) return [];
        if (query.Length == 0) return _projectFiles.Take(MaxMentionRows).ToList();
        var scored = new List<(int Score, string Path)>();
        foreach (var f in _projectFiles)
            if (FuzzyMatch.TryMatch(query, f, out var r)) scored.Add((r.Score, f));
        scored.Sort((a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return scored.Take(MaxMentionRows).Select(s => s.Path).ToList();
    }

    private bool MentionOpen => _mentionPopup.IsOpen;

    private void CloseMention()
    {
        _mentionPopup.IsOpen = false;
        _mentionIndex = 0;
        _mentionStart = -1;
    }

    private void MoveMention(int delta)
    {
        if (_mentionItems.Count == 0) return;
        _mentionIndex = (_mentionIndex + delta + _mentionItems.Count) % _mentionItems.Count;
        RenderMention();
    }

    private void RenderMention()
    {
        _mentionRows.Children.Clear();
        for (int i = 0; i < _mentionItems.Count; i++)
            _mentionRows.Children.Add(MentionRow(_mentionItems[i], i));
        if (_mentionIndex >= 0 && _mentionIndex < _mentionRows.Children.Count)
            Dispatcher.UIThread.Post(() =>
            {
                if (_mentionIndex < _mentionRows.Children.Count)
                    (_mentionRows.Children[_mentionIndex] as Control)?.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    private Control MentionRow(string path, int index)
    {
        int slash = path.LastIndexOf('/');
        var dir = slash >= 0 ? path[..slash] : "";
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        // VS Code-style: the file name always shows in full on the left; the containing directory rides to its
        // right, dimmed, and truncates from its *start* (leading ellipsis) so the closest folders stay visible.
        var nameBlock = new TextBlock
        {
            Text = name, FontFamily = _p.Mono, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _p.Brand,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left,
        };
        var dirBlock = new TextBlock
        {
            Text = dir, FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Faint,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0),
        };
        var line = new DockPanel { LastChildFill = true, Children = { nameBlock, dirBlock } };
        var frame = new Border
        {
            CornerRadius = SessionPalette.ButtonRadius, Padding = new Thickness(9, 7),
            Background = index == _mentionIndex ? _p.Raised2 : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = line,
        };
        frame.PointerEntered += (_, _) => { _mentionIndex = index; RenderMention(); };
        frame.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { _mentionIndex = index; AcceptMention(); } };
        return frame;
    }

    // Replace the "@token" at the caret with "@path " (the @ mention syntax the CLI expands), caret after it.
    private void AcceptMention()
    {
        if (_mentionItems.Count == 0 || _mentionStart < 0) { CloseMention(); return; }
        var path = _mentionItems[Math.Clamp(_mentionIndex, 0, _mentionItems.Count - 1)];
        var text = _composer.Text ?? "";
        int caret = Math.Clamp(_composer.CaretIndex, 0, text.Length);
        if (_mentionStart > text.Length) { CloseMention(); return; }
        var insert = "@" + path + " ";
        var newText = text[.._mentionStart] + insert + text[caret..];
        _suppressHistoryReset = true;
        _composer.Text = newText;
        _composer.CaretIndex = _mentionStart + insert.Length;
        _suppressHistoryReset = false;
        CloseMention();
        _composer.Focus();
    }

    // ── Input history (docs/session-composer-enhancements-plan.md §2) ─────────────

    // Recall a past prompt: direction -1 = older (↑), +1 = newer (↓). Stashes the in-progress draft on the
    // first ↑ and restores it when ↓ walks past the newest. Returns false when there's nothing to recall.
    private bool RecallHistory(int direction)
    {
        var prompts = _session?.Conversation.UserPromptHistory() ?? [];
        if (prompts.Count == 0) return false;
        if (_historyIndex == -1)
        {
            if (direction > 0) return false;         // ↓ does nothing when not already navigating
            _historyDraft = _composer.Text ?? "";
            _historyIndex = prompts.Count;           // one past the end → first ↑ lands on the newest
        }
        int next = _historyIndex + direction;
        if (next >= prompts.Count)                   // walked past the newest → back to the draft
        {
            _historyIndex = -1;
            SetComposerTextSilently(_historyDraft);
            return true;
        }
        _historyIndex = Math.Max(0, next);
        SetComposerTextSilently(prompts[_historyIndex]);
        return true;
    }

    private void SetComposerTextSilently(string text)
    {
        _suppressHistoryReset = true;
        _composer.Text = text;
        _composer.CaretIndex = text.Length;
        _suppressHistoryReset = false;
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
        _changesToggle.IsVisible = true;   // the changed-files toggle rides with the thread, not the launcher
        UpdateJumpButtons();
    }

    // ── Jump buttons (docs/session-composer-enhancements-plan.md §4) ──────────────

    // A round, translucent scroll button that floats over the thread.
    private Border JumpButton(string glyph, string tip, Action onClick)
    {
        var b = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = _p.Raised2, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            BoxShadow = BoxShadows.Parse("0 6 18 0 #40000000"), [ToolTip.TipProperty] = tip,
            Child = new TextBlock
            {
                Text = glyph, FontSize = 16, Foreground = _p.Muted, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        b.PointerEntered += (_, _) => b.Background = _p.Border;
        b.PointerExited += (_, _) => b.Background = _p.Raised2;
        b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) onClick(); };
        return b;
    }

    // ── Background attention ─────────────────────────────────────────────────────

    private void OnStateForAlert() => MaybeAlert();

    // Raise a desktop cue when a paused-turn prompt is pending while this window isn't the active one. The
    // decider fires at most once per pending item; passing the activation explicitly avoids racing IsActive
    // during the Activated/Deactivated events themselves.
    private void MaybeAlert(bool? active = null)
    {
        if (_session is null) return;
        if (!_attention.Evaluate(Conv.PendingPermission, active ?? IsActive)) return;
        var p = Conv.PendingPermission!;
        string what = p.IsQuestion ? "has a question for you"
            : p.IsPlan ? "has a plan to review"
            : "needs your permission to continue";
        var project = _cwd.Length > 0 && System.IO.Path.GetFileName(_cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "A session";
        AttentionRequested?.Invoke("Perch — a session needs you", $"{project} {what}.");
    }

    // Show each jump button only when it would do something: "to bottom" when not already at the tail; "to last
    // prompt" when a prompt exists but is scrolled out of view. Hidden entirely off the thread.
    private void UpdateJumpButtons()
    {
        bool onThread = _thread.IsVisible;
        _jumpBottomBtn.IsVisible = onThread && !_thread.AtBottom;
        _jumpPromptBtn.IsVisible = onThread && _thread.HasPromptAbove;
    }

    // ── Bar ──────────────────────────────────────────────────────────────────────

    private void RefreshBar()
    {
        var project = _cwd.Length > 0 ? (System.IO.Path.GetFileName(_cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : _cwd) : "Perch";
        var label = _cwd.Length > 0 ? project : "New session";
        // The heading mirrors the session rows: project name, with the /rename custom title appended inline.
        var title = _session?.Title;
        _projectText.Inlines?.Clear();
        _projectText.Inlines?.Add(new Run(label) { FontWeight = FontWeight.Bold, Foreground = _p.Title });
        if (title is { Length: > 0 })
            _projectText.Inlines?.Add(new Run($"  ·  {title}") { FontWeight = FontWeight.Normal, Foreground = _p.Muted });
        _pathText.Text = _cwd.Length > 0 ? _cwd : "choose a project to begin";
        _pathText.IsVisible = true;
        Title = _cwd.Length > 0
            ? $"{project}{(title is { Length: > 0 } ? $" · {title}" : "")} — Perch session"
            : "Perch session";

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

        // A mid-session mode switch (session.PermissionMode, set optimistically) beats the CLI's reported mode
        // (conv.PermissionMode, which only updates on the deferred ack) — mirroring how the model pill works.
        var mode = _session?.PermissionMode is { Length: > 0 } switchedMode && switchedMode != _mode ? switchedMode
                 : conv.Model.Length > 0 ? conv.PermissionMode
                 : StartingMode;
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
        MaybeRebuildSkills();
    }

    // Rebuilds the palette's skill list off the UI thread when the cwd or the session's advertised command
    // set changes (the latter arrives with init, and again after a /clear). Cheap signature check first so
    // the common RefreshBar (fired on every state change) does no work once the list is current.
    private void MaybeRebuildSkills()
    {
        var signature = (_cwd, Conv.SlashCommands.Count);
        if (signature == _skillsBuiltFor) return;
        _skillsBuiltFor = signature;

        var cwd = _cwd;
        var advertised = Conv.SlashCommands.ToList();
        Task.Run(() =>
        {
            try { return SlashCommandCatalog.ToPaletteItems(SkillCatalog.ForSession(cwd, advertised)); }
            catch { return (IReadOnlyList<SlashCommandInfo>)[]; }
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            _skillCommands = t.Result;
            if (PaletteOpen) UpdatePaletteFromText();   // fold newly-found skills into an open palette
        }, TaskScheduler.FromCurrentSynchronizationContext());
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

            var nearFull = _autoCompactEnabled
                ? $"and Perch will auto-compact it at {_autoCompactThreshold}% (/autocompact)."
                : "and a compaction is coming as it nears full.";
            _contextPill[ToolTip.TipProperty] =
                $"Context window: {FormatTokens(ctx)} of {FormatTokens(window)} ({pct:0}%). This is what every new message re-sends to the model — the fuller it gets, the more each turn costs, {nearFull}";

            MaybeAutoCompact(pct);
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
                if (_session is { IsRunning: true } live) live.SetPermissionMode(chosen);   // optimistic; nudges RefreshBar
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
        // Ctrl+V: let the normal text paste happen, but also check the clipboard for an image to stage as an
        // attachment (an image isn't text, so nothing is pasted into the box for it).
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            _ = TryPasteImageAsync();

        // While the command palette is open it owns the arrow/Tab/Enter keys: navigate, complete, or run.
        if (PaletteOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MovePalette(1); e.Handled = true; return;
                case Key.Up: MovePalette(-1); e.Handled = true; return;
                case Key.Tab: AcceptPalette(run: false); e.Handled = true; return;
                case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): AcceptPalette(run: true); e.Handled = true; return;
            }
        }

        // The file-mention popup owns the same keys while open: navigate, then Tab/Enter inserts the path.
        if (MentionOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveMention(1); e.Handled = true; return;
                case Key.Up: MoveMention(-1); e.Handled = true; return;
                case Key.Tab: AcceptMention(); e.Handled = true; return;
                case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift): AcceptMention(); e.Handled = true; return;
            }
        }

        // ↑/↓ with no popup open recalls this session's past prompts (↑ only from the very start of the text,
        // so arrowing through a multi-line draft still works; ↓ only once recall is under way).
        if (!PaletteOpen && !MentionOpen)
        {
            if (e.Key == Key.Up && (_historyIndex != -1 || _composer.CaretIndex == 0) && RecallHistory(-1)) { e.Handled = true; return; }
            if (e.Key == Key.Down && _historyIndex != -1 && RecallHistory(1)) { e.Handled = true; return; }
        }

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

    // Esc: dismiss the command palette first, else deny a pending permission, else interrupt the running turn.
    // Ctrl+C with no text selected also interrupts (so it stops a prompt when there's nothing to copy); with a
    // selection it falls through to the normal copy. (This window-level tunnel handler runs before the
    // composer's, so the palette guard must live here too.)
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            if (HasTextSelection()) return;   // let the copy happen
            if (_session is { IsRunning: true } live && Conv.TurnActive) { live.Interrupt(); e.Handled = true; }
            return;
        }
        if (e.Key != Key.Escape) return;
        if (_usageOverlay.IsVisible) { CloseUsageOverlay(); e.Handled = true; return; }
        if (_autoCompactOverlay.IsVisible) { CloseAutoCompactOverlay(); e.Handled = true; return; }
        if (_resumeOverlay.IsVisible) { CloseResumeOverlay(); e.Handled = true; return; }
        if (PaletteOpen) { ClosePalette(); e.Handled = true; return; }
        if (MentionOpen) { CloseMention(); e.Handled = true; return; }
        if (Conv.PendingPermission is { } pending) { _session?.AnswerPermission(pending, allow: false, switchMode: false); e.Handled = true; }
        else if (_session is { IsRunning: true } live && Conv.TurnActive) { live.Interrupt(); e.Handled = true; }
    }

    // Whether the focused control holds a live text selection (so Ctrl+C should copy rather than interrupt).
    private bool HasTextSelection()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused switch
        {
            TextBox tb => tb.SelectionStart != tb.SelectionEnd,
            SelectableTextBlock stb => !string.IsNullOrEmpty(stb.SelectedText),
            _ => false,
        };
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

    /// <summary>Pushes the Perch auto-compaction setting (enabled + threshold %) from settings. Re-arms the
    /// trigger so a fresh setting takes effect on the next crossing. The app pushes this when it builds the
    /// window and again whenever the modal changes it.</summary>
    public void SetAutoCompactConfig(bool enabled, int thresholdPercent)
    {
        _autoCompactEnabled = enabled;
        _autoCompactThreshold = Math.Clamp(thresholdPercent, AutoCompactMin, AutoCompactMax);
        _autoCompactArmed = true;
        if (_session is not null) RefreshBar();
    }

    // The threshold slider's band. Below ~50% compaction is pointless; above ~95% the CLI's own near-full
    // compaction gets there first, so Perch's early trigger would rarely beat it.
    private const int AutoCompactMin = 50, AutoCompactMax = 95;

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
    internal void FeedSampleForRender(string cwd, string? userPrompt, IEnumerable<SessionEvent> events,
        IReadOnlyList<MessageAttachment>? attachments = null)
    {
        var sample = PerchSession.ForRender(cwd);
        if (userPrompt is not null) sample.Conversation.AddUserPrompt(userPrompt, attachments);
        foreach (var ev in events) sample.Conversation.Apply(ev);
        Attach(sample);
        // A render-only session has no process, so pose it as a live one.
        _composer.IsEnabled = true;
        _composer.PlaceholderText = "Reply, or type / for a command";
        _endButton.IsVisible = true;
        _resumeButton.IsVisible = false;
        RefreshBar();
    }

    /// <summary>HeadlessRenderer: seed a fixed usage reading and open the /usage overlay for a capture.</summary>
    internal void ShowUsageOverlayForRender(UsageInfo info)
    {
        UsageProvider = () => info;
        ShowUsageOverlay();
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
