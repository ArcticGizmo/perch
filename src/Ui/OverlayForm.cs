using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Perch.Data;
using Perch.Ui;

namespace Perch.Ui;

/// <summary>
/// Floating always-on-top status widget.
/// Compact bar by default; click the header to expand to per-session rows.
/// Drag the header to reposition. Right-click for the exit menu.
/// Clicking a session row focuses the terminal running that session.
/// </summary>
internal sealed class OverlayForm : Form, IDenseHost
{
    // ── Layout ────────────────────────────────────────────────────────────────
    private const int FormWidth       = 280;
    private const int HeaderHeight    = 44;
    private const int BarRowHeight    = 18;
    private const int UsageStripHeight= 50;  // two usage bars + padding, shown only when expanded
    private const int SysMetricsStripHeight = 50;  // system CPU + RAM bars + padding, shown only when expanded
    private const int MetricsBarWidth = 28;  // width reserved for a session row's CPU/RAM mini-bars
    private const int RowHeight        = 46;
    private const int SubRowHeight      = 24;
    private const int SectionRowHeight  = 26;  // the collapsible "Autonomous" section-header row
    private const int SubIndent         = 22;
    private const int HorizPad          = 12;
    private const int Corner            = 10;
    private const int RcIconWidth       = 14;  // width reserved for the remote-control glyph in a row
    private const int BotIconWidth      = 16;  // width reserved for the background / SDK-session robot glyph
    private const int MailIconWidth     = 16;  // width reserved for the external-notify (mail) glyph
    private const int ArtifactIconWidth = 16;  // width reserved for the clickable artifact glyph in a row
    private const int ThermoIconWidth   = 12;  // width reserved for the context-pressure thermometer
    private const int WarnIconWidth     = 14;  // width reserved for the stuck-detection warning glyph
    private const int PartyIconWidth    = 16;  // width reserved for the "confetti finish" party-popper glyph
    private const int QuickLinksRowHeight = 24; // height of the quick-links icon strip below the usage bars

    // Default vertical gap below the top of the working area for the floating panel. Sized to
    // clear most applications' window-control (close/minimize) buttons.
    private const int FloatTopGap = 32;

    // Header right-side glyphs (the dense toggle icon and the expand chevron).
    private const int IconBoxW    = 16;
    private const int IconBoxH    = 16;
    private const int IconGap     = 6;

    // ── Palette ───────────────────────────────────────────────────────────────
    private static readonly Color BgColor        = Color.FromArgb(15,  15,  20);
    private static readonly Color BorderNormal   = Color.FromArgb(45,  45,  60);
    private static readonly Color RunningColor      = Color.FromArgb(34,  197, 94);
    private static readonly Color AttentionColor   = Color.FromArgb(251, 146, 60);
    private static readonly Color AwaitingColor    = Color.FromArgb(250, 204, 21);
    private static readonly Color IdleColor        = Color.FromArgb(100, 116, 139);
    private static readonly Color FgColor        = Color.FromArgb(225, 225, 235);
    private static readonly Color MutedColor     = Color.FromArgb(110, 110, 130);
    private static readonly Color SepColor       = Color.FromArgb(35,  35,  50);
    private static readonly Color RowHoverColor  = Color.FromArgb(25,  25,  38);
    private static readonly Color SubAgentColor  = Color.FromArgb(168, 85,  247);
    private static readonly Color RemoteColor    = Color.FromArgb(96,  165, 250);
    private static readonly Color BotColor       = Color.FromArgb(148, 163, 184);  // slate — the background / SDK-session robot glyph
    private static readonly Color MailColor      = Color.FromArgb(94,  234, 212);
    private static readonly Color ArtifactColor  = Color.FromArgb(251, 191, 36);   // amber — the clickable artifact glyph
    private static readonly Color WarnColor      = Color.FromArgb(245, 158, 11);   // deep amber — the "possibly stuck" warning glyph
    private static readonly Color BurnColor      = Color.FromArgb(125, 185, 232);  // soft blue — the live token burn-rate label
    private static readonly Color GitAddColor    = Color.FromArgb(34,  197, 94);   // green — unstaged lines added
    private static readonly Color GitDelColor    = Color.FromArgb(239, 68,  68);   // red — unstaged lines deleted
    private static readonly Color TreeLineColor  = Color.FromArgb(55,  55,  72);
    private static readonly Color UsageRedColor  = Color.FromArgb(239, 68,  68);
    private static readonly Color UsageTrackColor= Color.FromArgb(38,  38,  52);
    private static readonly Color UpdateColor    = Color.FromArgb(255, 68,  45);   // perch-logo red-orange — the update badge

    // ── State ─────────────────────────────────────────────────────────────────
    // A flat render list of parent-session rows interleaved with their running sub-agent
    // child rows, in draw order. Built from the sessions on each update. One special row — the
    // collapsible "Autonomous" section header — carries no session, only a count; it separates the
    // interactive sessions above from the background / SDK-driven ones below.
    private readonly record struct DisplayRow(ClaudeSession? Session, SubAgent? Sub, int SectionCount = -1)
    {
        public bool IsSubAgent => Sub != null;
        // The "Autonomous" divider row: no session, just a count of the background sessions it groups.
        public bool IsSectionHeader => SectionCount >= 0;
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool  _expanded = true;
    // Whether the "Autonomous" section (background / SDK-driven sessions) is expanded. Collapsed by
    // default so background runs stay tucked under a single count until the user opens them.
    private bool  _autonomousExpanded;
    private bool  _dragging;
    private Point _dragStartScreen;
    private Point _formStartLoc;
    private bool  _wasDrag;
    private int   _hoveredRow = -1;
    // The row index whose artifact glyph the cursor is currently over, or -1. Drives the hand cursor
    // and a brighter glyph; the glyph is clickable independently of the row's focus-terminal click.
    private int   _hoveredArtifactRow = -1;
    // Display-only gate for the clickable artifact glyph; when off no glyph is drawn and the row's
    // click falls through to focusing the terminal. The session still tracks its artifacts.
    private bool  _showArtifacts = true;
    // Display-only gate for idle teammates: when on, teammates waiting for the lead are dropped from
    // the roster and only working ones are shown. The teammates are still tracked; a hidden one
    // reappears the moment it starts working again. See SetHideInactiveTeamMembers.
    private bool  _hideInactiveTeamMembers;
    // Attention state: while true, the panel border becomes an animated neon "chase" (a bright comet
    // travelling the perimeter over a soft inward glow) instead of the static grey outline.
    // _chasePhase is the head's position around the loop (0..1, wrapping), advanced by _flashTimer.
    private bool   _attentionFlash;
    private double _chasePhase;

    // How far the chase head advances per animation tick. At the timer's ~33ms interval this sends the
    // comet around the whole border roughly every 1.6s.
    private const double ChaseStep = 0.02;

    // Update-available affordance: an orange (perch-logo coloured) download badge in the header's
    // right-side glyph cluster, shown only while an update is pending. _updateIconRect is its painted
    // hit-box, rebuilt each header paint (Rectangle.Empty while hidden); _hoveredUpdateIcon drives the
    // hand cursor. Clicking it raises UpdateRequested for the owning context to perform the update.
    private bool _updateAvailable;
    private Rectangle _updateIconRect = Rectangle.Empty;
    private bool _hoveredUpdateIcon;

    // Dense mode: an alternate, out-of-the-way presentation (a slim strip hugging a screen edge that
    // expands on hover). The whole state machine — on/off, the hover popup, the docked edge/monitor,
    // the remembered positions, drag-to-redock, and the strip painting — lives in this controller; the
    // form just forwards paint/mouse/relayout to it. Created in the constructor (after _icon exists).
    private readonly DenseModeController _denseMode;

    // Resource monitoring. The whole-machine strip at the top of the panel, and a per-session CPU/RAM
    // mini-bar on each session row. Both are pushed in from the owning context (settings + the metrics
    // monitor's samples); _sessionMetrics is keyed by session pid. See DrawSystemMetricsStrip /
    // DrawMetricsBars and the metrics hover plumbing below.
    private bool _showSystemMetrics;
    private bool _showSessionMetrics;
    private SystemMetrics _sysMetrics = SystemMetrics.Empty;
    private IReadOnlyDictionary<string, SessionMetrics> _sessionMetrics = new Dictionary<string, SessionMetrics>();
    // Metrics mini-bar hover: a per-row hit-rect rebuilt each paint, a dwell timer and a tooltip giving
    // the fine-grained CPU%/RAM numbers — twin to the thermometer/warning/task glyphs' plumbing.
    private readonly Dictionary<int, Rectangle> _metricsRects = new();
    private int _hoveredMetricsRow = -1;
    private readonly System.Windows.Forms.Timer _metricsHoverTimer;
    private readonly HintTooltipForm _metricsTooltip = new();

    private UsageInfo _usage = UsageInfo.Empty;
    private bool _usageEnabled = true;
    private bool _showExpectedRate = true;
    private bool _showContextPressure = true;
    private bool _showContextGreenSegment = false;
    private bool _showModeBadges = true;
    // Context-pressure thresholds as fractions of the window: hidden below yellow, then yellow ->
    // orange -> red. Defaults match the original hard-coded bands; overridden from settings.
    private float _ctxYellow = 0.50f, _ctxOrange = 0.65f, _ctxRed = 0.80f;
    private bool _inUsageStrip;
    private readonly UsageTooltipForm _usageTooltip = new();

    // Context-pressure thermometer hover: the painted hit-rect of each session row's thermometer
    // (rebuilt every paint, keyed by row index), the row currently under the cursor (-1 = none),
    // a 150ms dwell timer, and the little "Context at NN%" tooltip it pops.
    private readonly Dictionary<int, Rectangle> _thermoRects = new();
    private int _hoveredThermoRow = -1;
    private readonly System.Windows.Forms.Timer _thermoHoverTimer;
    private readonly HintTooltipForm _thermoTooltip = new();

    // Stuck-detection warning glyph: drawn when detection is on and a session is flagged. Hover plumbing
    // mirrors the thermometer's — a per-row hit-rect rebuilt each paint, a 150ms dwell timer, and a
    // little tooltip giving the reason. _showStuckWarnings only gates the *display*; the monitor decides
    // whether a session is actually flagged.
    private bool _showStuckWarnings = true;
    private readonly Dictionary<int, Rectangle> _warnRects = new();
    private int _hoveredWarnRow = -1;
    private readonly System.Windows.Forms.Timer _warnHoverTimer;
    private readonly HintTooltipForm _warnTooltip = new();

    // Task-list progress: the "n/m" count drawn on a session row that has a native checklist
    // (TaskCreate/TaskUpdate). Hover plumbing mirrors the thermometer/warning glyphs — a per-row
    // hit-rect rebuilt each paint, a 150ms dwell timer, and a multi-line tooltip listing every task
    // with its status.
    // _showTaskProgress gates only the *display* of the count; the session still tracks its tasks.
    private bool _showTaskProgress = true;
    // Live token burn rate ("12.3k/m") drawn on a running session row. Display-only gate; the rate is
    // computed regardless, just not drawn when off. Off by default — an opt-in indicator. No hover
    // plumbing: the label already shows the number, unlike the glyph/bar indicators around it.
    private bool _showBurnRate;
    // Unstaged git line-churn chip ("+142 -37") drawn on a session row. Display-only gate; the numbers
    // are computed (and, when off, not even fetched) in the data layer. Off by default — an opt-in
    // experimental indicator, like the burn rate beside it.
    private bool _showGitStats;
    // "Waiting on you" timer drawn on an awaiting-input row's second line, warming from yellow toward
    // red as it grows. Display-only gate; the blocked duration is tracked regardless. On by default.
    private bool _showWaitingTimer = true;
    // How many minutes a blocked session takes to warm the waiting timer fully to red. User-tunable.
    private int _waitingTimerRedMinutes = 10;
    private readonly Dictionary<int, Rectangle> _taskRects = new();
    private int _hoveredTaskRow = -1;
    private readonly System.Windows.Forms.Timer _taskHoverTimer;
    private readonly HintTooltipForm _taskTooltip = new();

    // Enabled quick links and their pre-rendered icons, index-aligned. Both are pushed in from the
    // owning context via SetQuickLinks, which is the source of truth; icons are loaded once per
    // update (an exe-extracted or embedded bitmap, or null to fall back to drawn initials).
    private IReadOnlyList<QuickLink> _quickLinks = [];
    private List<Bitmap?> _quickLinkIcons = [];
    // -1 = none hovered, otherwise an index into _quickLinks.
    private int  _hoveredQuickLink = -1;
    // When on, the quick-link icons are painted rotated 180°. Pure whimsy; off by default.
    private bool _upsideDownQuickLinks;

    private bool HasQuickLinksRow => _quickLinks.Count > 0;

    // The stacked strips below the header, in order: system-metrics, usage, quick-links, then rows.
    // Each strip's top is the previous one's bottom, so the layout stays consistent whether or not a
    // given strip is shown.
    private int UsageStripTop => HeaderHeight + (_showSystemMetrics ? SysMetricsStripHeight : 0);
    private int QuickLinksTop => UsageStripTop + (_usageEnabled ? UsageStripHeight : 0);
    private int RowsTop       => QuickLinksTop + (HasQuickLinksRow ? QuickLinksRowHeight : 0);

    private readonly System.Windows.Forms.Timer _flashTimer;
    private readonly System.Windows.Forms.Timer _flashStopTimer;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _usageHoverTimer;

    // Auto-close countdown: while the owning context's "auto-close after last session" grace timer is
    // armed, a thin grey bar across the top of the panel depletes from full to empty, quietly hinting
    // when the window will close itself. Driven entirely from the context via Start/CancelAutoClose-
    // Countdown; _autoCloseEnds is when the bar (and the real close) is due.
    private readonly System.Windows.Forms.Timer _autoCloseBarTimer;
    private bool _autoCloseActive;
    private DateTime _autoCloseEnds;
    private int _autoCloseDurationMs;

    // The perch icon, shown atop the dense strip purely for flair. Null if unavailable.
    private readonly Bitmap? _icon = EmbeddedResources.LoadBitmap("Perch.icon.png");

    // The "perch reacts" mood bird — swapped in from the owning context so the header/dense-strip logo
    // wears the aggregate session mood. Null when the feature is off, falling back to the plain _icon.
    // The bitmap is owned by the context's BirdMoodArt cache, so we only hold the reference.
    private Bitmap? _moodIcon;

    // The logo actually painted: the mood bird when one is set, else the plain perch icon.
    private Bitmap? BrandIcon => _moodIcon ?? _icon;

    // Is the full session body (usage bars + rows) currently on screen? In floating mode that's
    // the expanded state; in dense mode it's the hover-opened popup.
    private bool ShowFullPanel => _denseMode.IsDense ? _denseMode.IsOpen : _expanded;

    // External (ntfy) notifications. _externalNotifyAvailable mirrors the global setting and gates
    // both the per-session glyph and the right-click toggle; _externalNotifySessions holds the
    // session ids opted in. Both are pushed in from the owning context, which is the source of truth.
    private bool _externalNotifyAvailable;
    private HashSet<string> _externalNotifySessions = new();

    // "Confetti finish" (experimental). _confettiAvailable mirrors the global setting and gates both the
    // party-popper glyph and the right-click toggle; _confettiSessions holds the session ids currently
    // armed. Deliberately in-memory only — arming is never persisted, so a celebration can't fire by
    // surprise after a restart. A session's arming is spent (removed) the moment it next finishes.
    private bool _confettiAvailable;
    private readonly HashSet<string> _confettiSessions = new();

    public event EventHandler? ExitRequested;
    public event Action<string>? SessionFocused;

    /// <summary>Raised when the user picks "View history" for a session; carries that session's id so
    /// the owning context can open the history viewer on it.</summary>
    public event Action<string>? HistoryRequested;

    /// <summary>Raised when the user picks "Enable/Disable external notifications" for a session;
    /// carries that session's id for the context to flip its opt-in state.</summary>
    public event Action<string>? ExternalNotifyToggleRequested;

    /// <summary>Raised when the user clicks the header's update badge, asking the owning context to
    /// download and apply the pending update.</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>Raised when the user toggles the system-resource (whole-machine CPU/RAM) strip from the
    /// overlay's right-click menu — either the header menu or a right-click on the strip itself. Carries
    /// the desired new enabled state for the owning context to persist and apply.</summary>
    public event Action<bool>? SystemMetricsToggleRequested;

    /// <summary>Raised when the user toggles the account-usage strip from the overlay's right-click menu —
    /// either the header menu or a right-click on the strip itself. Carries the desired new enabled
    /// state for the owning context to persist and apply.</summary>
    public event Action<bool>? UsageToggleRequested;

    /// <summary>Raised when the user finishes dragging the overlay (mouse released after a real drag),
    /// so the owning context can re-evaluate anything tied to the overlay's screen — chiefly moving the
    /// ambient screen-edge glow onto the monitor the overlay now sits on.</summary>
    public event EventHandler? DragCompleted;

    // ── Construction ──────────────────────────────────────────────────────────
    public OverlayForm()
    {
        FormBorderStyle  = FormBorderStyle.None;
        ShowInTaskbar    = false;
        TopMost          = true;
        AllowTransparency = true;
        BackColor        = Color.Black;
        TransparencyKey  = Color.Black;
        DoubleBuffered   = true;
        StartPosition    = FormStartPosition.Manual;

        var screen = Screen.PrimaryScreen!.WorkingArea;
        Location   = new Point(screen.Right - FormWidth - 16, screen.Top + FloatTopGap);
        ClientSize = new Size(FormWidth, HeaderHeight);

        // Drives the animated chase border: advance the comet's head and repaint. Runs only while
        // attention is active (started in TriggerAttention, stopped by the flash-stop timer below or
        // when nothing needs attention), so it costs nothing at rest.
        _flashTimer = new System.Windows.Forms.Timer { Interval = 33 };
        _flashTimer.Tick += (_, _) => { _chasePhase += ChaseStep; Invalidate(); };

        _flashStopTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _flashStopTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flashStopTimer.Stop();
            _attentionFlash = false;
            Invalidate();
        };

        // Ticks the elapsed run-time labels while the panel is expanded with a running session.
        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) => Invalidate();

        // One-shot dwell timer: pops the usage tooltip 150ms after the cursor enters the bar strip.
        _usageHoverTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _usageHoverTimer.Tick += (_, _) =>
        {
            _usageHoverTimer.Stop();
            if (_inUsageStrip && !_dragging)
                ShowUsageTooltip();
        };

        // One-shot dwell timer: pops the context-pressure tooltip 150ms after the cursor settles
        // over a row's thermometer glyph.
        _thermoHoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _thermoHoverTimer.Tick += (_, _) =>
        {
            _thermoHoverTimer.Stop();
            if (_hoveredThermoRow >= 0 && !_dragging)
                ShowThermoTooltip(_hoveredThermoRow);
        };

        // One-shot dwell timer for the stuck-warning glyph, twin to the thermometer's above.
        _warnHoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _warnHoverTimer.Tick += (_, _) =>
        {
            _warnHoverTimer.Stop();
            if (_hoveredWarnRow >= 0 && !_dragging)
                ShowWarnTooltip(_hoveredWarnRow);
        };

        // One-shot dwell timer for the task-count badge, twin to the warning glyph's above; pops the
        // full checklist tooltip.
        _taskHoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _taskHoverTimer.Tick += (_, _) =>
        {
            _taskHoverTimer.Stop();
            if (_hoveredTaskRow >= 0 && !_dragging)
                ShowTaskTooltip(_hoveredTaskRow);
        };

        // One-shot dwell timer for the per-session metrics mini-bar, twin to the task badge's above;
        // pops the fine-grained CPU%/RAM tooltip.
        _metricsHoverTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _metricsHoverTimer.Tick += (_, _) =>
        {
            _metricsHoverTimer.Stop();
            if (_hoveredMetricsRow >= 0 && !_dragging)
                ShowMetricsTooltip(_hoveredMetricsRow);
        };

        // Repaints the auto-close countdown bar while it's active. Stops itself once the deadline
        // passes (the context's grace timer fires Exit at that point and tears the window down).
        _autoCloseBarTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _autoCloseBarTimer.Tick += (_, _) =>
        {
            if (!_autoCloseActive || DateTime.Now >= _autoCloseEnds)
                _autoCloseBarTimer.Stop();
            Invalidate();
        };

        // The dense-mode controller owns dense state/geometry/painting. Built here (not as a field
        // initializer) so the icon it draws atop the strip is already loaded. The status-dot colours
        // are passed in so the strip's counts match the rest of the overlay exactly.
        _denseMode = new DenseModeController(this, RunningColor, AwaitingColor, AttentionColor, IdleColor);

        // If a monitor is added or removed, re-evaluate the dense docking; the controller resets to
        // the primary screen when the monitor we were pinned to has disappeared.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e))); return; }
        _denseMode.OnDisplaySettingsChanged();
    }

    // ── IDenseHost ───────────────────────────────────────────────────────────
    // The slim surface the dense controller drives; window geometry and Invalidate come straight off
    // Control. Explicit implementations so these stay off the form's own public surface.
    int IDenseHost.FullPanelWidth => FormWidth;
    int IDenseHost.FullPanelHeight() => FullPanelHeight();
    IReadOnlyList<ClaudeSession> IDenseHost.Sessions => _sessions;
    Bitmap? IDenseHost.Icon => BrandIcon;
    void IDenseHost.RelayoutWindow() => RelayoutWindow();
    void IDenseHost.UpdateTickTimer() => UpdateTickTimer();
    void IDenseHost.ClearRowHover() => _hoveredRow = -1;
    void IDenseHost.HideUsageTooltip() => HideUsageTooltip();

    // ── Public API ────────────────────────────────────────────────────────────
    /// <summary>Sets the "perch reacts" mood bird shown as the header / dense-strip logo, or null to
    /// fall back to the plain perch icon. The bitmap is owned by the caller's mood-art cache; the
    /// overlay only paints it. Repaints when the image actually changes.</summary>
    public void SetBirdMood(Bitmap? moodIcon)
    {
        if (ReferenceEquals(_moodIcon, moodIcon)) return;
        _moodIcon = moodIcon;
        Invalidate();  // one repaint covers both the floating header and the dense strip (same window)
    }

    public void UpdateSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;

        // Interactive (terminal) sessions render at the top; background / SDK-driven ones are grouped
        // below under the collapsible "Autonomous" section. Each partition is sorted by display name.
        var interactive = sessions
            .Where(s => !s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        var background = sessions
            .Where(s => s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<DisplayRow>();
        foreach (var session in interactive)
            AddSessionRows(rows, session);

        // The "Autonomous" section: a single count-bearing header row, and — only while expanded — the
        // background session rows beneath it. Omitted entirely when there are no background sessions.
        if (background.Count > 0)
        {
            rows.Add(new DisplayRow(null, null, background.Count));
            if (_autonomousExpanded)
                foreach (var session in background)
                    AddSessionRows(rows, session);
        }
        else
        {
            // No autonomous sessions to expand — don't leave the flag stuck open for the next batch.
            _autonomousExpanded = false;
        }

        _rows = rows;

        // Auto-collapse when all sessions disappear
        if (sessions.Count == 0)
            _expanded = false;

        // Stop flashing if nothing needs attention anymore
        if (_attentionFlash && _sessions.All(s => s.Status != SessionStatus.NeedsAttention && s.Status != SessionStatus.AwaitingInput))
        {
            _flashTimer.Stop();
            _flashStopTimer.Stop();
            _attentionFlash = false;
        }

        RelayoutWindow();
        UpdateTickTimer();
        Invalidate();
    }

    // Appends a session's row plus its running sub-agent / teammate child rows to the render list, in
    // draw order. Shared by the interactive list and the "Autonomous" section so both lay out identically.
    private void AddSessionRows(List<DisplayRow> rows, ClaudeSession session)
    {
        rows.Add(new DisplayRow(session, null));
        // Teammates (the persistent roster) lead, grouped and sorted by name so they read as a team;
        // transient sub-agents follow. Working teammates sort above idle ones within the roster.
        var ordered = session.SubAgents
            // When on, drop idle teammates from the roster — only working ones are shown. Transient
            // sub-agents are already working by definition, so this only ever hides idle teammates.
            .Where(s => !_hideInactiveTeamMembers || !(s.IsTeammate && s.IsIdle))
            .OrderByDescending(s => s.IsTeammate)
            .ThenBy(s => s.IsTeammate && s.IsIdle)
            .ThenBy(s => s.Name ?? s.Description, StringComparer.OrdinalIgnoreCase);
        foreach (var sub in ordered)
            rows.Add(new DisplayRow(session, sub));
    }

    // Latest account-wide rate-limit usage, rendered as the two bars under the banner.
    public void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (_usageEnabled && _usageTooltip.Visible)
            ShowUsageTooltip();  // refresh the tooltip in place if it's currently open
        Invalidate();
    }

    // Toggles the usage strip on/off. When off, the strip is hidden and reserves no space.
    public void SetUsageEnabled(bool enabled)
    {
        if (_usageEnabled == enabled) return;
        _usageEnabled = enabled;
        if (!enabled)
        {
            _usageHoverTimer.Stop();
            _inUsageStrip = false;
            HideUsageTooltip();
        }
        RelayoutWindow();
        Invalidate();
    }

    public void SetShowExpectedRate(bool show)
    {
        if (_showExpectedRate == show) return;
        _showExpectedRate = show;
        Invalidate();
    }

    // ── Resource monitoring ─────────────────────────────────────────────────────
    // Shows or hides the whole-machine CPU/RAM strip at the top of the panel. Reserves (or frees) the
    // strip's height, so it relayouts.
    public void SetShowSystemMetrics(bool show)
    {
        if (_showSystemMetrics == show) return;
        _showSystemMetrics = show;
        RelayoutWindow();
        Invalidate();
    }

    // Shows or hides the per-session CPU/RAM mini-bar on each session row. Display only — the row
    // simply reclaims the freed width when off; the samples keep arriving regardless.
    public void SetShowSessionMetrics(bool show)
    {
        if (_showSessionMetrics == show) return;
        _showSessionMetrics = show;
        if (!show)
        {
            _hoveredMetricsRow = -1;
            _metricsHoverTimer.Stop();
            HideMetricsTooltip();
        }
        Invalidate();
    }

    // Latest whole-machine reading, drawn in the top strip.
    public void UpdateSystemMetrics(SystemMetrics metrics)
    {
        _sysMetrics = metrics;
        if (_showSystemMetrics)
            Invalidate();
    }

    // Latest per-session readings, keyed by session pid, drawn as each row's mini-bar. Refreshes the
    // metrics tooltip in place if it's currently open.
    public void UpdateSessionMetrics(IReadOnlyDictionary<string, SessionMetrics> metrics)
    {
        _sessionMetrics = metrics;
        if (_showSessionMetrics)
        {
            if (_hoveredMetricsRow >= 0 && _metricsTooltip.Visible)
                ShowMetricsTooltip(_hoveredMetricsRow);
            Invalidate();
        }
    }

    public void SetShowContextPressure(bool show)
    {
        if (_showContextPressure == show) return;
        _showContextPressure = show;
        Invalidate();
    }

    /// <summary>Shows or hides the green "first segment" indicator — the below-yellow band. When on, the
    /// thermometer appears (green) as soon as a session's context fill is known, instead of staying
    /// blank until it crosses the yellow threshold.</summary>
    public void SetShowContextGreenSegment(bool show)
    {
        if (_showContextGreenSegment == show) return;
        _showContextGreenSegment = show;
        Invalidate();
    }

    /// <summary>Shows or hides the permission-mode badge. Display only — when off no badge is drawn and
    /// the session name reclaims the freed width; the session's mode is still tracked.</summary>
    public void SetShowModeBadges(bool show)
    {
        if (_showModeBadges == show) return;
        _showModeBadges = show;
        Invalidate();
    }

    /// <summary>Shows or hides the stuck-detection warning glyph. Display only — when off (or when the
    /// monitor isn't flagging anything) no glyph is drawn. Hides any open tooltip on the way out.</summary>
    public void SetStuckDetectionEnabled(bool enabled)
    {
        if (_showStuckWarnings == enabled) return;
        _showStuckWarnings = enabled;
        if (!enabled)
        {
            _hoveredWarnRow = -1;
            _warnHoverTimer.Stop();
            HideWarnTooltip();
        }
        Invalidate();
    }

    /// <summary>Shows or hides the task-list "n/m" progress count. Display only — when off no count is
    /// drawn and the session name reclaims the freed width; the session's checklist is still tracked.
    /// Hides any open tooltip on the way out.</summary>
    public void SetShowTaskProgress(bool show)
    {
        if (_showTaskProgress == show) return;
        _showTaskProgress = show;
        if (!show)
        {
            _hoveredTaskRow = -1;
            _taskHoverTimer.Stop();
            HideTaskTooltip();
        }
        Invalidate();
    }

    /// <summary>Shows or hides the live token burn-rate label. Display only — when off no label is drawn
    /// and the session name reclaims the freed width; the rate is still computed.</summary>
    public void SetShowBurnRate(bool show)
    {
        if (_showBurnRate == show) return;
        _showBurnRate = show;
        Invalidate();
    }

    /// <summary>Shows or hides the unstaged git line-churn chip ("+142 -37"). Display only — when off no
    /// chip is drawn and the session name reclaims the freed width. The data layer stops fetching
    /// entirely when the feature is disabled; this switch just governs whether the chip is painted.</summary>
    public void SetShowGitStats(bool show)
    {
        if (_showGitStats == show) return;
        _showGitStats = show;
        Invalidate();
    }

    /// <summary>Shows or hides the "waiting on you" timer on awaiting-input rows. Display only — when
    /// off the row still shows its "input ↩" status, just without the elapsed-wait line.</summary>
    public void SetShowWaitingTimer(bool show)
    {
        if (_showWaitingTimer == show) return;
        _showWaitingTimer = show;
        UpdateTickTimer();   // an awaiting row now needs (or no longer needs) the per-second repaint
        Invalidate();
    }

    /// <summary>Sets how many minutes a blocked session takes to warm the waiting timer fully to red.
    /// Floored at 1 minute so the ramp is always meaningful.</summary>
    public void SetWaitingTimerRedMinutes(int minutes)
    {
        minutes = Math.Max(1, minutes);
        if (_waitingTimerRedMinutes == minutes) return;
        _waitingTimerRedMinutes = minutes;
        Invalidate();
    }

    /// <summary>Shows or hides the clickable artifact glyph. Display only — when off no glyph is drawn,
    /// the row's click focuses the terminal as usual, and the session name reclaims the freed width;
    /// the session's artifacts are still tracked.</summary>
    public void SetShowArtifacts(bool show)
    {
        if (_showArtifacts == show) return;
        _showArtifacts = show;
        if (!show && _hoveredArtifactRow >= 0)
        {
            _hoveredArtifactRow = -1;
            Cursor = Cursors.Default;
        }
        Invalidate();
    }

    /// <summary>Shows or hides idle teammates. Display only — when on, teammates waiting for the lead
    /// drop off the roster and only working ones remain; a hidden teammate reappears the moment it
    /// starts working again. Rebuilds the row list from the current sessions so the change is immediate.</summary>
    public void SetHideInactiveTeamMembers(bool hide)
    {
        if (_hideInactiveTeamMembers == hide) return;
        _hideInactiveTeamMembers = hide;
        UpdateSessions(_sessions);   // re-run the roster build (which owns the filter) and relayout
    }

    /// <summary>Shows or hides the header's update badge. Repaint only — the badge lives in the
    /// header's right-side glyph cluster and reserves no separate layout.</summary>
    public void SetUpdateAvailable(bool available)
    {
        if (_updateAvailable == available) return;
        _updateAvailable = available;
        if (!available)
        {
            _hoveredUpdateIcon = false;
            _updateIconRect = Rectangle.Empty;
        }
        Invalidate();
    }

    // Toggles the upside-down quick-link icons. Repaint only — layout is unaffected.
    public void SetUpsideDownQuickLinks(bool upsideDown)
    {
        if (_upsideDownQuickLinks == upsideDown) return;
        _upsideDownQuickLinks = upsideDown;
        Invalidate();
    }

    /// <summary>Sets the context-pressure colour thresholds (whole percentages of the window). The
    /// thermometer is hidden below <paramref name="yellow"/>, then warms yellow → orange → red.</summary>
    public void SetContextThresholds(int yellow, int orange, int red)
    {
        _ctxYellow = yellow / 100f;
        _ctxOrange = orange / 100f;
        _ctxRed    = red    / 100f;
        Invalidate();
    }

    // Replaces the quick-links strip with the enabled subset of the given links, (re)loading their
    // icons. Called on startup and whenever the user edits the list in Settings.
    public void SetQuickLinks(IReadOnlyList<QuickLink> links)
    {
        var enabled = links.Where(l => l.Enabled).ToList();

        // Icons come from a process-wide cache keyed by (name, path, size). Resolving an app's icon
        // can be costly (Start Menu lookup), and this runs on every list edit, so unchanged links must
        // be a cheap cache hit. The cache owns the bitmaps, so we never dispose them here.
        _quickLinkIcons   = enabled.Select(l => QuickLinkLauncher.CachedIcon(l, 18)).ToList();
        _quickLinks       = enabled;
        _hoveredQuickLink = -1;

        RelayoutWindow();
        Invalidate();
    }

    // Whether external (ntfy) notifications are switched on globally. Controls whether the per-session
    // mail glyph and the right-click enable/disable item appear at all.
    public void SetExternalNotificationsAvailable(bool available)
    {
        if (_externalNotifyAvailable == available) return;
        _externalNotifyAvailable = available;
        Invalidate();
    }

    // The set of session ids currently opted in to external notifications. Copied so the caller can
    // keep mutating its own set without affecting what we render.
    public void SetExternalNotifySessions(IEnumerable<string> sessionIds)
    {
        _externalNotifySessions = new HashSet<string>(sessionIds);
        Invalidate();
    }

    // True when the mail glyph / "Disable" wording applies to this session: the feature is on and the
    // session has opted in.
    private bool ExternalNotifyEnabled(ClaudeSession session) =>
        _externalNotifyAvailable && _externalNotifySessions.Contains(session.SessionId);

    // Whether the (experimental) "confetti finish" feature is switched on globally. Controls whether the
    // party-popper glyph and the right-click arm/disarm item appear at all. Turning it off clears every
    // armed session so nothing is left primed to fire behind the scenes.
    public void SetConfettiFinishAvailable(bool available)
    {
        if (_confettiAvailable == available) return;
        _confettiAvailable = available;
        if (!available) _confettiSessions.Clear();
        Invalidate();
    }

    // True when this session is armed for a confetti finish (and the feature is on).
    private bool ConfettiArmed(ClaudeSession session) =>
        _confettiAvailable && _confettiSessions.Contains(session.SessionId);

    // Flips a session's confetti arming from the right-click menu. In-memory only; repaints so the
    // party-popper glyph appears/disappears at once.
    private void ToggleConfetti(string sessionId)
    {
        if (!_confettiSessions.Remove(sessionId))
            _confettiSessions.Add(sessionId);
        Invalidate();
    }

    // If the given session was armed, disarm it and report true so the owning context can set off the
    // confetti. Arming is one-shot: a finish spends it. Returns false (and does nothing) otherwise.
    public bool ConsumeConfetti(string sessionId)
    {
        if (!_confettiAvailable || !_confettiSessions.Remove(sessionId)) return false;
        Invalidate();
        return true;
    }

    // The run-time labels only need a per-second repaint when they're actually on screen: a running
    // session's elapsed run time, or a blocked session's "waiting on you" timer (when it's enabled).
    private void UpdateTickTimer()
    {
        bool need = ShowFullPanel && _sessions.Any(s =>
            s.Status == SessionStatus.Running
            || (_showWaitingTimer && s.Status == SessionStatus.AwaitingInput));
        if (need && !_tickTimer.Enabled)
            _tickTimer.Start();
        else if (!need && _tickTimer.Enabled)
            _tickTimer.Stop();
    }

    public void TriggerAttention()
    {
        // Auto-surface the project that needs attention. In dense mode that means popping the
        // hover panel open (it auto-closes after 2s); otherwise expand the floating panel.
        if (_denseMode.IsDense)
        {
            _denseMode.OpenPopup();
        }
        else if (!_expanded && _rows.Count > 0)
        {
            _expanded = true;
            RelayoutWindow();
            UpdateTickTimer();
        }

        _attentionFlash = true;
        _flashTimer.Start();
        _flashStopTimer.Stop();
        _flashStopTimer.Start();
        Invalidate();
    }

    // Begin the auto-close countdown indicator: a depleting bar due durationMs from now. Idempotent
    // restart — the context only calls this on a genuine arm, so it won't reset mid-countdown.
    public void StartAutoCloseCountdown(int durationMs)
    {
        _autoCloseActive     = true;
        _autoCloseDurationMs = durationMs;
        _autoCloseEnds       = DateTime.Now.AddMilliseconds(durationMs);
        if (!_autoCloseBarTimer.Enabled)
            _autoCloseBarTimer.Start();
        Invalidate();
    }

    // Hide the countdown bar (a session reappeared, or auto-close conditions no longer hold).
    public void CancelAutoCloseCountdown()
    {
        if (!_autoCloseActive && !_autoCloseBarTimer.Enabled) return;
        _autoCloseActive = false;
        _autoCloseBarTimer.Stop();
        Invalidate();
    }

    // ── Layout ────────────────────────────────────────────────────────────────
    // Pixel height of a single render row (sub-agent rows are shorter than session rows).
    private static int HeightOf(DisplayRow row) =>
        row.IsSectionHeader ? SectionRowHeight : row.IsSubAgent ? SubRowHeight : RowHeight;

    // Y offset (from the top of the form) of the row at the given index.
    private int RowTop(int index)
    {
        int top = RowsTop;
        for (int i = 0; i < index; i++)
            top += HeightOf(_rows[i]);
        return top;
    }

    // Owns the window's size and position. Floating keeps whatever location it was dragged to; in
    // dense mode the controller docks the strip/popup to its remembered edge and Y.
    private void RelayoutWindow()
    {
        if (_dragging) return;  // never fight an in-progress drag

        if (_denseMode.IsDense)
        {
            _denseMode.ApplyGeometry();
        }
        else
        {
            int h = _expanded ? FullPanelHeight() : HeaderHeight;
            if (ClientSize.Height != h || ClientSize.Width != FormWidth)
                ClientSize = new Size(FormWidth, h);
        }
    }

    // Height of the full panel (header + optional usage strip + all session rows).
    private int FullPanelHeight()
    {
        int h = HeaderHeight;
        if (_rows.Count > 0)
        {
            if (_showSystemMetrics)
                h += SysMetricsStripHeight;  // system CPU/RAM strip sits just under the header
            if (_usageEnabled)
                h += UsageStripHeight;  // usage bars sit between the header and the rows
            if (HasQuickLinksRow)
                h += QuickLinksRowHeight;
            foreach (var row in _rows)
                h += HeightOf(row);
            h += 2;
        }
        return h;
    }

    // ── Painting ──────────────────────────────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var path = PaintKit.RoundedRect(bounds, Corner);

        using (var bg = new SolidBrush(BgColor))
            g.FillPath(bg, path);

        if (_attentionFlash)
            DrawChaseBorder(g, path, AttentionColor);
        else
            using (var pen = new Pen(BorderNormal, 1.5f))
                g.DrawPath(pen, path);

        if (_denseMode.IsClosedStrip)
        {
            _denseMode.PaintStrip(g);
            return;
        }

        DrawHeader(g);

        if (_autoCloseActive)
            DrawAutoCloseBar(g);

        if (ShowFullPanel)
        {
            if (_showSystemMetrics)
                DrawSystemMetricsStrip(g);
            if (_usageEnabled)
                DrawUsageBars(g);
            if (HasQuickLinksRow)
                DrawQuickLinksRow(g);
            // Thermometer and warning hit-rects are rebuilt from scratch each paint; DrawSessionRow
            // repopulates them for any row that actually shows the glyph.
            _thermoRects.Clear();
            _warnRects.Clear();
            _taskRects.Clear();
            _metricsRects.Clear();
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(g, i);
        }
    }

    // Draws the attention border as an animated neon "chase": a bright comet head with a trailing tail
    // travels the rounded-rect perimeter over a faintly-lit base outline, with a few progressively
    // wider/fainter passes for a soft glow. Everything is clipped to the panel so the bloom only spills
    // inward — the crisp outer edge stays the filled rounded-rect boundary, which also keeps the soft
    // colour off the form's black transparency key (where it would otherwise show as a hard halo).
    private void DrawChaseBorder(Graphics g, GraphicsPath path, Color color)
    {
        // Flatten the outline into a polyline. Flatten only subdivides curves, so a straight side stays
        // one long segment while a corner becomes many short ones — drawing per raw segment would light
        // a whole side at once. So we resample the loop into uniform, tiny arc-length steps below, which
        // makes the comet glide at a constant rate everywhere (corners and sides alike).
        using var flat = (GraphicsPath)path.Clone();
        flat.Flatten(null, 0.25f);
        var raw = flat.PathPoints;
        int m = raw.Length;
        if (m < 2)
        {
            using var fallback = new Pen(color, 1.5f);
            g.DrawPath(fallback, path);
            return;
        }

        // Cumulative arc length around the closed loop (raw[m-1] → raw[0] closes it).
        var cum = new float[m + 1];
        for (int i = 0; i < m; i++)
        {
            var a = raw[i];
            var b = raw[(i + 1) % m];
            cum[i + 1] = cum[i] + MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        float total = cum[m];
        if (total <= 0)
        {
            using var fallback = new Pen(color, 1.5f);
            g.DrawPath(fallback, path);
            return;
        }

        // Resample to evenly-spaced points (~3px apart) by walking the cumulative lengths. Uniform
        // spacing is what makes the motion constant-rate — each sample is the same distance along.
        int samples = Math.Clamp((int)(total / 3f), 96, 1024);
        var pts = new PointF[samples];
        int seg = 0;
        for (int k = 0; k < samples; k++)
        {
            float target = total * k / samples;
            while (seg < m && cum[seg + 1] < target) seg++;
            var a = raw[seg % m];
            var b = raw[(seg + 1) % m];
            float segLen = cum[seg + 1] - cum[seg];
            float t = segLen > 0 ? (target - cum[seg]) / segLen : 0;
            pts[k] = new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        double head = _chasePhase - Math.Floor(_chasePhase);   // 0..1 travelling comet head
        const double TailLen = 0.55;                           // comet tail, as a fraction of the loop
        const double BaseA   = 42;                             // faint always-lit outline
        const double PeakA   = 213;                            // extra brightness at the head

        // Comet intensity at an arc fraction p: brightest at the head, fading along the tail behind it.
        double Intensity(double p)
        {
            double dd = head - p;
            dd -= Math.Floor(dd);           // distance behind the head, 0..1 (wrapped)
            double t = 1 - dd / TailLen;    // 1 at the head → 0 at the tail's end
            return t <= 0 ? 0 : t * t;
        }

        // Clip to the panel so the wide bloom passes glow inward only. Intersect (not replace) so the
        // invalidated-region clip is preserved.
        var oldClip = g.Clip;
        g.SetClip(path, CombineMode.Intersect);

        using var pen = new Pen(color, 1.5f)
        {
            StartCap = LineCap.Round,
            EndCap   = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // Widest & faintest first so a bright, near-white core sits on a soft coloured halo.
        (float w, float aMul)[] passes = { (7f, 0.10f), (4f, 0.22f), (2.2f, 0.5f), (1.4f, 1f) };
        foreach (var (w, aMul) in passes)
        {
            pen.Width = w;
            for (int k = 0; k < samples; k++)
            {
                double inten = Intensity((k + 0.5) / samples);
                int a = (int)Math.Clamp((BaseA + inten * PeakA) * aMul, 0, 255);
                if (a <= 1) continue;
                // Heat the colour toward white near the head for the neon "hot core" look.
                Color c = inten > 0.05 ? Theme.Blend(color, Color.White, (float)(inten * 0.5)) : color;
                pen.Color = Color.FromArgb(a, c);
                g.DrawLine(pen, pts[k], pts[(k + 1) % samples]);
            }
        }

        g.Clip = oldClip;
        oldClip.Dispose();
    }

    // A thin bar hugging the top edge that shrinks from full to empty over the auto-close grace
    // period, quietly hinting when the window will close itself. Deliberately drawn in the muted idle
    // grey so it stays unobtrusive. Harmless when the deadline has passed (renders empty) until the
    // window is torn down.
    private void DrawAutoCloseBar(Graphics g)
    {
        double remaining = (_autoCloseEnds - DateTime.Now).TotalMilliseconds;
        double frac = _autoCloseDurationMs > 0 ? Math.Clamp(remaining / _autoCloseDurationMs, 0, 1) : 0;

        const int TrackH = 3;
        int left  = HorizPad;
        int right = ClientSize.Width - HorizPad;
        int w     = right - left;
        int y     = 4;

        int fillW = (int)Math.Round(w * frac);
        if (fillW > 0)
            using (var fill = new SolidBrush(IdleColor))
                PaintKit.FillRoundedBar(g, fill, left, y, fillW, TrackH);
    }

    private void DrawHeader(Graphics g)
    {
        int midY = HeaderHeight / 2;

        using var labelFont = new Font("Segoe UI", 8f,  FontStyle.Regular, GraphicsUnit.Point);
        using var countFont = new Font("Segoe UI", 9f,  FontStyle.Bold,    GraphicsUnit.Point);
        using var chevFont  = new Font("Segoe UI", 7.5f,                   GraphicsUnit.Point);
        using var muted     = new SolidBrush(MutedColor);

        // Perch icon (mirrors the dense strip's logo)
        int brandRight = HorizPad;
        if (BrandIcon is { } icon)
        {
            const int IconSize = 18;
            int iconY = midY - IconSize / 2;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(icon, new Rectangle(HorizPad, iconY, IconSize, IconSize));
            brandRight = HorizPad + IconSize + 5;
        }

        var labelSz = g.MeasureString("Perch", labelFont);
        g.DrawString("Perch", labelFont, muted, brandRight, midY - labelSz.Height / 2 + 2);
        brandRight += (int)labelSz.Width;

        // Separator dot
        int sepX = brandRight + 4;
        g.FillEllipse(muted, sepX, midY - 2, 4, 4);
        int x = sepX + 10;

        if (_sessions.Count == 0)
        {
            var sz = g.MeasureString("no sessions", labelFont);
            g.DrawString("no sessions", labelFont, muted, x, midY - sz.Height / 2);
        }
        else
        {
            int running   = _sessions.Count(s => s.Status == SessionStatus.Running);
            int attention = _sessions.Count(s => s.Status == SessionStatus.NeedsAttention);
            int awaiting  = _sessions.Count(s => s.Status == SessionStatus.AwaitingInput);
            int idle      = _sessions.Count(s => s.Status == SessionStatus.Idle);

            x = DrawStatusPill(g, x, midY, awaiting,  AwaitingColor,  AwaitingColor,  countFont);
            x = DrawStatusPill(g, x, midY, running,   RunningColor,   FgColor,        countFont);
            x = DrawStatusPill(g, x, midY, attention, AttentionColor, AttentionColor, countFont);

            if (running == 0 && attention == 0 && awaiting == 0)
                DrawStatusPill(g, x, midY, idle, IdleColor, IdleColor, countFont);
        }

        // Right-side header glyph cluster, laid out right-to-left: [dense toggle] [expand chevron]
        // [update badge]. clusterLeft tracks the left edge as each glyph is placed.
        //
        // Dense toggle icon (always present). The glyph points along the docked edge: floating mode
        // shows the arrow collapsing toward that edge, dense mode shows it expanding inward. Clicking
        // it enters dense from floating, or leaves dense from the open popup.
        DrawSideCollapseIcon(g, SideIconRect(), reversed: _denseMode.IsDense ^ (_denseMode.Side == DenseSide.Left));
        int clusterLeft = ClientSize.Width - HorizPad - IconBoxW;

        // Expand chevron — floating mode only (hidden in dense), and only when there's something to
        // expand. Sits just to the left of the dense toggle icon.
        if (!_denseMode.IsDense && _sessions.Count > 0)
        {
            var chevron = _expanded ? "▲" : "▼";
            var chSz    = g.MeasureString(chevron, chevFont);
            float chevX = clusterLeft - IconGap - chSz.Width;
            g.DrawString(chevron, chevFont, muted, chevX, midY - chSz.Height / 2);
            clusterLeft = (int)chevX;
        }

        // Update badge — only while an update is pending. Drawn in the perch-logo colour so it stands
        // out from the muted line-art glyphs; clicking it (see OnMouseUp) performs the update.
        if (_updateAvailable)
        {
            int ux = clusterLeft - IconGap - IconBoxW;
            int uy = (HeaderHeight - IconBoxH) / 2;
            _updateIconRect = new Rectangle(ux, uy, IconBoxW, IconBoxH);
            DrawUpdateIcon(g, _updateIconRect, _hoveredUpdateIcon);
        }
        else
        {
            _updateIconRect = Rectangle.Empty;
        }
    }

    // The update badge: a filled perch-orange disc with a white "download" arrow (a down arrow over a
    // short tray line). Pure GDI so it themes and scales with the rest of the header. Brightens a touch
    // while hovered.
    private static void DrawUpdateIcon(Graphics g, Rectangle r, bool hovered)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var disc = hovered ? Color.FromArgb(255, 104, 84) : UpdateColor;
        using (var fill = new SolidBrush(disc))
            g.FillEllipse(fill, r);

        using var pen = new Pen(Color.White, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        int cx  = r.Left + r.Width / 2;
        int midY = r.Top + r.Height / 2;
        int top  = midY - 4;
        int bot  = midY + 2;
        g.DrawLine(pen, cx, top, cx, bot);                 // arrow shaft
        g.DrawLine(pen, cx - 3, bot - 3, cx, bot);         // arrowhead
        g.DrawLine(pen, cx + 3, bot - 3, cx, bot);
        g.DrawLine(pen, cx - 3, midY + 4, cx + 3, midY + 4); // tray line

        g.SmoothingMode = oldSmoothing;
    }

    // Hit-box for the dense toggle glyph. It always takes the rightmost slot in the header; in
    // floating mode the expand chevron sits to its left.
    private Rectangle SideIconRect()
    {
        int top   = (HeaderHeight - IconBoxH) / 2;
        int right = ClientSize.Width - HorizPad;
        return new Rectangle(right - IconBoxW, top, IconBoxW, IconBoxH);
    }

    private static int DrawStatusPill(Graphics g, int x, int midY, int count,
                                      Color dotColor, Color textColor, Font font)
    {
        if (count == 0) return x;

        using var dotBrush  = new SolidBrush(dotColor);
        using var textBrush = new SolidBrush(textColor);

        g.FillEllipse(dotBrush, x, midY - 4, 8, 8);
        x += 12;

        var label = count.ToString();
        var sz    = g.MeasureString(label, font);
        g.DrawString(label, font, textBrush, x, midY - sz.Height / 2);
        return x + (int)sz.Width + 8;
    }

    // Draws the dense toggle glyph: an arrow into a pipe. Plain "->|" collapses to the right edge
    // (enter dense); the reversed "|<-" expands back out (leave dense). Pure GDI so it themes and
    // scales with the rest of the header, like the chevrons and the mode badge.
    private static void DrawSideCollapseIcon(Graphics g, Rectangle r, bool reversed)
    {
        using var pen = new Pen(MutedColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        int midY     = r.Top + r.Height / 2;
        int pad      = 3;
        int left     = r.Left + pad;
        int right    = r.Right - pad;
        int headLen  = 4;

        if (!reversed)
        {
            int pipeX    = right;
            int shaftEnd = pipeX - 2;
            g.DrawLine(pen, left, midY, shaftEnd, midY);                       // shaft
            g.DrawLine(pen, shaftEnd - headLen, midY - headLen, shaftEnd, midY); // arrowhead
            g.DrawLine(pen, shaftEnd - headLen, midY + headLen, shaftEnd, midY);
            g.DrawLine(pen, pipeX, r.Top + pad, pipeX, r.Bottom - pad);        // pipe
        }
        else
        {
            int pipeX    = left;
            int shaftEnd = pipeX + 2;
            g.DrawLine(pen, right, midY, shaftEnd, midY);                      // shaft
            g.DrawLine(pen, shaftEnd + headLen, midY - headLen, shaftEnd, midY); // arrowhead
            g.DrawLine(pen, shaftEnd + headLen, midY + headLen, shaftEnd, midY);
            g.DrawLine(pen, pipeX, r.Top + pad, pipeX, r.Bottom - pad);        // pipe
        }
    }

    // The remote-control indicator: a "broadcast" glyph — a source dot at the lower-left with two
    // quarter-arcs radiating up and to the right. Pure GDI so it themes and scales like the mode
    // badge and the collapse arrow. Drawn on a row only while that session is being remote-controlled
    // (its session file carries a bridgeSessionId); the origin x is the dot, waves grow up-right.
    private static void DrawRemoteIcon(Graphics g, int originX, int midY)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen   = new Pen(RemoteColor, 2.25f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(RemoteColor);

        int oy = midY + 4;                                       // origin sits low so waves rise through the row
        g.FillEllipse(brush, originX - 2, oy - 2, 4, 4);         // source dot
        g.DrawArc(pen, originX - 5, oy - 5, 10, 10, 270, 90);    // inner wave
        g.DrawArc(pen, originX - 9, oy - 9, 18, 18, 270, 90);    // outer wave

        g.SmoothingMode = oldSmoothing;
    }

    // The external-notify indicator: a small envelope with a "send" arrow rising from it, drawn when
    // a session is opted in to ntfy pushes. Pure GDI so it themes and scales like the other glyphs.
    // The "confetti finish" indicator: a little party popper — a gold cone spraying a fan of coloured
    // confetti up and to the right — drawn on a session that's armed to celebrate when it next finishes.
    // Hand-drawn GDI like the other row glyphs (a font emoji would render monochrome under GDI+), so it
    // stays colourful, themes, and scales. x is the left edge, midY the row centre.
    private static void DrawPartyIcon(Graphics g, int x, int midY)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Cone: a narrow gold triangle, apex at the bottom-left, mouth opening toward the upper-right.
        var apex   = new PointF(x + 2,  midY + 6);
        var mouthT = new PointF(x + 11, midY - 4);
        var mouthB = new PointF(x + 6,  midY + 3);
        using (var cone = new SolidBrush(Color.FromArgb(255, 190, 70)))
            g.FillPolygon(cone, new[] { apex, mouthT, mouthB });

        // Confetti: a few small dots sprayed out from the cone's mouth, each a different festive colour.
        (float dx, float dy, float r, Color c)[] bits =
        {
            (13f, -6f, 1.8f, Color.FromArgb(255, 92, 92)),    // red
            (11f, -2f, 1.5f, Color.FromArgb(94, 234, 212)),   // teal
            (14f, -1f, 1.6f, Color.FromArgb(178, 120, 255)),  // purple
            (10f, -7f, 1.4f, Color.FromArgb(255, 236, 92)),   // yellow
            (13f, -9f, 1.5f, Color.FromArgb(92, 214, 122)),   // green
        };
        foreach (var (dx, dy, r, c) in bits)
        {
            using var b = new SolidBrush(c);
            g.FillEllipse(b, x + dx - r, midY + dy - r, r * 2, r * 2);
        }

        g.SmoothingMode = oldSmoothing;
    }

    // The background-session indicator: a little robot head — a rounded square face with two dot eyes
    // and a short antenna — drawn when a session is SDK-driven rather than an interactive CLI session
    // (see ClaudeSession.IsBackground). "No human at the keyboard" reads clearly as a bot at row size.
    // Pure GDI so it themes and scales like the other row glyphs. x is the left edge, midY the centre.
    private static void DrawBotIcon(Graphics g, int x, int midY)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen   = new Pen(BotColor, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(BotColor);

        const int w = 11, h = 9;
        int left = x;
        int top  = midY - h / 2 + 1;                             // +1 leaves room for the antenna above

        // Antenna: a short stalk rising from the head's top-centre, capped with a dot.
        int cx = left + w / 2;
        g.DrawLine(pen, cx, top - 3, cx, top);
        g.FillEllipse(brush, cx - 1.5f, top - 5f, 3f, 3f);

        // Head: a rounded square face.
        var face = new Rectangle(left, top, w, h);
        using (var path = PaintKit.RoundedRect(face, 2))
            g.DrawPath(pen, path);

        // Two dot eyes.
        g.FillEllipse(brush, left + 2.5f,     top + 3f, 2f, 2f);
        g.FillEllipse(brush, left + w - 4.5f, top + 3f, 2f, 2f);

        g.SmoothingMode = oldSmoothing;
    }

    private static void DrawMailIcon(Graphics g, int x, int midY)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(MailColor, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        const int w = 11, h = 8;
        int top  = midY - h / 2;
        var body = new Rectangle(x, top, w, h);
        g.DrawRectangle(pen, body);
        // Envelope flap: two strokes meeting at the centre top, like a "V" tucked under the lid.
        g.DrawLines(pen, new[]
        {
            new Point(x,         top),
            new Point(x + w / 2, top + h / 2),
            new Point(x + w,     top),
        });

        g.SmoothingMode = oldSmoothing;
    }

    // The artifact indicator: Claude's "artifacts" mark — two offset rounded squares — drawn amber when
    // a session has published one or more web artifacts. Rotated 90° from Claude's default so the two
    // squares stagger along the "\" diagonal (one upper-left, one lower-right). Both are drawn as full
    // outlines, so where they overlap the lines cross and stay visible rather than one occluding the
    // other. Unlike the mail/remote glyphs this one is clickable (see HitTestArtifactIcon); it brightens
    // while hovered. Pure GDI so it themes and scales like the other glyphs. x is the left edge, midY
    // the row centre.
    private static void DrawArtifactIcon(Graphics g, int x, int midY, bool hovered)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var color = hovered ? Color.FromArgb(255, 224, 140) : ArtifactColor;
        using var pen = new Pen(color, 1.4f) { LineJoin = LineJoin.Round };

        const int side   = 8;   // edge length of each square
        const int offset = 3;   // diagonal stagger between the two squares
        const int radius = 2;   // corner radius

        int top = midY - (side + offset) / 2;
        var upperLeft  = new Rectangle(x,          top,          side, side);
        var lowerRight = new Rectangle(x + offset, top + offset, side, side);

        using (var p1 = PaintKit.RoundedRect(upperLeft, radius))
            g.DrawPath(pen, p1);
        using (var p2 = PaintKit.RoundedRect(lowerRight, radius))
            g.DrawPath(pen, p2);

        g.SmoothingMode = oldSmoothing;
    }

    // ── System metrics strip ────────────────────────────────────────────────────
    // Two bars at the top of the panel (just under the header): whole-machine CPU and physical-RAM
    // load, drawn with the same shared renderer as the usage bars so the two strips read alike. The
    // percentage doubles as the bar fill and is coloured by load (green → red). Shows em-dashes until
    // the first real sample lands (CPU needs two samples to produce a reading).
    private void DrawSystemMetricsStrip(Graphics g)
    {
        using var capFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var pctFont = new Font("Segoe UI", 7.5f, FontStyle.Bold,    GraphicsUnit.Point);

        bool has = _sysMetrics.HasData;
        int top = HeaderHeight + 2;
        DrawSysBar(g, top,                "CPU", has ? _sysMetrics.CpuPercent : null, capFont, pctFont);
        DrawSysBar(g, top + BarRowHeight, "RAM", has ? _sysMetrics.RamPercent : null, capFont, pctFont);

        // A thin grey rule separating the system strip from the usage strip below it — only drawn when
        // the usage strip is actually there to divide from. Floated a few px above the strip boundary
        // so the clearance to the bars above and the usage bars below reads evenly, not cramped.
        if (_usageEnabled)
        {
            int sepY = UsageStripTop - 4;
            using var sepPen = new Pen(SepColor, 1f);
            g.DrawLine(sepPen, HorizPad, sepY, ClientSize.Width - HorizPad, sepY);
        }
    }

    private void DrawSysBar(Graphics g, int rowTop, string caption, double? percent, Font capFont, Font pctFont) =>
        UsageBarRenderer.Draw(g, HorizPad, ClientSize.Width - HorizPad, rowTop + BarRowHeight / 2,
            caption, percent, expectedPct: null, stale: false, capFont, pctFont,
            MutedColor, UsageTrackColor, Color.FromArgb(180, 180, 195), BgColor,
            captionW: 46, pctW: 34, trackH: 7);

    // ── Usage bars ─────────────────────────────────────────────────────────────
    // Two always-visible bars below the banner: the 5-hour ("Session") and 7-day ("Weekly")
    // rate-limit windows. Dimmed when the data is stale/unavailable.
    private void DrawUsageBars(Graphics g)
    {
        bool stale = _usage.IsStale(DateTime.Now);

        using var capFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var pctFont = new Font("Segoe UI", 7.5f, FontStyle.Bold,    GraphicsUnit.Point);

        int top = UsageStripTop + 2;
        double? sessionExpected = _showExpectedRate ? UsageBarRenderer.ElapsedPercent(_usage.FiveHourResetsAt, TimeSpan.FromHours(5)) : null;
        double? weeklyExpected  = _showExpectedRate ? UsageBarRenderer.ElapsedPercent(_usage.SevenDayResetsAt, TimeSpan.FromDays(7))  : null;
        DrawUsageBar(g, top,                "Session", _usage.FiveHourPercent, sessionExpected, stale, capFont, pctFont);
        DrawUsageBar(g, top + BarRowHeight, "Weekly",  _usage.SevenDayPercent, weeklyExpected,  stale, capFont, pctFont);
    }

    // The overlay's compact bar: a HorizPad inset on both sides, narrow caption/pct columns, the
    // overlay's own muted/track/bg shades. The marker base is the shared (180,180,195).
    private void DrawUsageBar(Graphics g, int rowTop, string caption, double? percent,
                              double? expectedPct, bool stale, Font capFont, Font pctFont) =>
        UsageBarRenderer.Draw(g, HorizPad, ClientSize.Width - HorizPad, rowTop + BarRowHeight / 2,
            caption, percent, expectedPct, stale, capFont, pctFont,
            MutedColor, UsageTrackColor, Color.FromArgb(180, 180, 195), BgColor,
            captionW: 46, pctW: 34, trackH: 7);

    // ── Quick links row ───────────────────────────────────────────────────────
    // Draws the enabled quick-link icons side-by-side, centred horizontally. Each slot shows its
    // pre-loaded icon, or drawn initials over a name-derived colour when no icon is available.
    private void DrawQuickLinksRow(Graphics g)
    {
        const int IconSize = 16;
        const int IconGap  = 14;
        const int HitPad   = 4;

        int rowTop  = QuickLinksTop;
        int centerY = rowTop + QuickLinksRowHeight / 2;

        int count  = _quickLinks.Count;
        int totalW = count * IconSize + (count - 1) * IconGap;
        int startX = (ClientSize.Width - totalW) / 2;

        for (int i = 0; i < count; i++)
        {
            int iconX = startX + i * (IconSize + IconGap);
            int iconY = centerY - IconSize / 2;

            if (_hoveredQuickLink == i)
            {
                using var hover = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
                g.FillRectangle(hover,
                    iconX - HitPad, iconY - HitPad,
                    IconSize + HitPad * 2, IconSize + HitPad * 2);
            }

            // The source icons happen to render upside-down, so we flip each slot 180° about its own
            // centre to set them right way up. When the user opts into upside-down mode, we skip the
            // correction and let them sit as they naturally fall.
            GraphicsState? flip = null;
            if (!_upsideDownQuickLinks)
            {
                flip = g.Save();
                float cx = iconX + IconSize / 2f, cy = iconY + IconSize / 2f;
                g.TranslateTransform(cx, cy);
                g.RotateTransform(180f);
                g.TranslateTransform(-cx, -cy);
            }

            var icon = i < _quickLinkIcons.Count ? _quickLinkIcons[i] : null;
            if (icon != null)
            {
                g.DrawImage(icon, iconX, iconY, IconSize, IconSize);
            }
            else
            {
                using var font  = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
                using var brush = new SolidBrush(FallbackColor(_quickLinks[i].Name));
                var initials = Initials(_quickLinks[i].Name);
                var sz = g.MeasureString(initials, font);
                g.DrawString(initials, font, brush,
                    iconX + (IconSize - sz.Width) / 2, iconY + (IconSize - sz.Height) / 2);
            }

            if (flip != null) g.Restore(flip);
        }
    }

    // Returns the index into _quickLinks under point p, or -1 if none.
    private int HitTestQuickLink(Point p)
    {
        if (!HasQuickLinksRow) return -1;
        int rowTop = QuickLinksTop;
        if (p.Y < rowTop || p.Y >= rowTop + QuickLinksRowHeight) return -1;

        const int IconSize = 16;
        const int IconGap  = 14;
        const int HitPad   = 4;

        int count  = _quickLinks.Count;
        int totalW = count * IconSize + (count - 1) * IconGap;
        int startX = (ClientSize.Width - totalW) / 2;

        for (int i = 0; i < count; i++)
        {
            int iconX = startX + i * (IconSize + IconGap);
            if (p.X >= iconX - HitPad && p.X < iconX + IconSize + HitPad)
                return i;
        }
        return -1;
    }

    // Up to two letters for the icon-less fallback glyph: the initials of the first two words, or the
    // first two characters of a single word. Falls back to "?" for an empty name.
    private static string Initials(string name)
    {
        var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return new string(words[0].Take(2).ToArray()).ToUpperInvariant();
        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
    }

    // A stable, reasonably saturated colour derived from the name, so two icon-less links are
    // visually distinguishable without any per-link configuration.
    private static Color FallbackColor(string name)
    {
        int hash = 0;
        foreach (char c in name) hash = hash * 31 + c;
        int hue = ((hash % 360) + 360) % 360;
        return ColorFromHsv(hue, 0.55, 0.85);
    }

    private static Color ColorFromHsv(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = hi switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    private void DrawRow(Graphics g, int rowIdx)
    {
        if (_rows[rowIdx].IsSectionHeader)
            DrawSectionHeaderRow(g, rowIdx);
        else if (_rows[rowIdx].IsSubAgent)
            DrawSubAgentRow(g, rowIdx);
        else
            DrawSessionRow(g, rowIdx);
    }

    // The "Autonomous" section divider: a chevron (collapsed/expanded), the label, a count badge, and a
    // hairline rule running to the right edge. Clicking anywhere on the row toggles the section (see
    // OnMouseUp). Styled from the muted palette so it reads as a quiet grouping, not another session.
    private void DrawSectionHeaderRow(Graphics g, int rowIdx)
    {
        var row  = _rows[rowIdx];
        int top  = RowTop(rowIdx);
        int midY = top + SectionRowHeight / 2;

        if (rowIdx == _hoveredRow)
        {
            using var hoverBrush = new SolidBrush(RowHoverColor);
            g.FillRectangle(hoverBrush, 1, top, ClientSize.Width - 2, SectionRowHeight);
        }

        using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var chevFont  = new Font("Segoe UI", 6.5f, GraphicsUnit.Point);
        using var muted     = new SolidBrush(MutedColor);

        // Collapse chevron: ▸ when collapsed, ▾ when expanded — matches the affordance users know.
        string chevron = _autonomousExpanded ? "▾" : "▸";
        var chSz = g.MeasureString(chevron, chevFont);
        int x = HorizPad;
        g.DrawString(chevron, chevFont, muted, x, midY - chSz.Height / 2);
        x += (int)chSz.Width + 4;

        // A small robot glyph echoes the per-row marker so the section reads as "the automated ones".
        DrawBotIcon(g, x, midY);
        x += BotIconWidth;

        const string label = "Autonomous";
        var labelSz = g.MeasureString(label, labelFont);
        g.DrawString(label, labelFont, muted, x, midY - labelSz.Height / 2);
        x += (int)labelSz.Width + 6;

        // Count badge: a dim pill giving the number of grouped sessions, so the collapsed row still
        // tells you how many autonomous runs are hiding beneath it.
        string count = row.SectionCount.ToString();
        var countSz  = g.MeasureString(count, labelFont);
        int badgeW   = (int)countSz.Width + 10;
        int badgeH   = (int)labelSz.Height + 2;
        var badge    = new Rectangle(x, midY - badgeH / 2, badgeW, badgeH);
        using (var badgeBrush = new SolidBrush(Color.FromArgb(38, 38, 52)))
        using (var badgePath  = PaintKit.RoundedRect(badge, badgeH / 2))
            g.FillPath(badgeBrush, badgePath);
        g.DrawString(count, labelFont, muted, x + 5, midY - countSz.Height / 2);
        x += badgeW + 8;

        // Hairline rule filling the rest of the row, so the divider reads as a section break.
        if (x < ClientSize.Width - HorizPad)
            using (var pen = new Pen(SepColor, 1f))
                g.DrawLine(pen, x, midY, ClientSize.Width - HorizPad, midY);
    }

    private void DrawSubAgentRow(Graphics g, int rowIdx)
    {
        var sub  = _rows[rowIdx].Sub!;
        int top  = RowTop(rowIdx);
        int midY = top + SubRowHeight / 2;

        if (rowIdx == _hoveredRow)
        {
            using var hoverBrush = new SolidBrush(RowHoverColor);
            g.FillRectangle(hoverBrush, 1, top, ClientSize.Width - 2, SubRowHeight);
        }

        // Tree connector: a stub dropping from the parent row down to this child's marker.
        int branchX = HorizPad + 4;            // aligns under the parent status dot
        int markerX = HorizPad + SubIndent;
        using (var treePen = new Pen(TreeLineColor, 1f))
        {
            g.DrawLine(treePen, branchX, top - SubRowHeight / 2, branchX, midY);
            g.DrawLine(treePen, branchX, midY, markerX - 2, midY);
        }

        // Teammates (Agent Teams) get a person glyph, an @name in their assigned colour, and dim while
        // idle; ordinary sub-agents keep the purple dot + type/description.
        if (sub.IsTeammate)
            DrawTeammateRow(g, sub, markerX, midY);
        else
            DrawPlainSubAgentRow(g, sub, markerX, midY);
    }

    private void DrawPlainSubAgentRow(Graphics g, SubAgent sub, int dotX, int midY)
    {
        using var dotBrush = new SolidBrush(SubAgentColor);
        g.FillEllipse(dotBrush, dotX, midY - 3, 6, 6);

        using var nameFont   = new Font("Segoe UI", 8f, GraphicsUnit.Point);
        using var statusFont = new Font("Segoe UI", 7f, GraphicsUnit.Point);
        using var fgBrush    = new SolidBrush(FgColor);
        using var mutedBrush = new SolidBrush(MutedColor);
        using var subBrush   = new SolidBrush(SubAgentColor);

        const string statusText = "running";
        var statusSz   = g.MeasureString(statusText, statusFont);
        int labelX     = dotX + 12;
        int labelMaxW  = ClientSize.Width - labelX - HorizPad - (int)statusSz.Width - 6;

        // The agent type (e.g. "general-purpose") leads, dim, ahead of the run's description.
        // De-dupe when one is missing so we never show the same token twice or a blank row.
        string type = sub.AgentType?.Trim() ?? "";
        string desc = sub.Description?.Trim() ?? "";
        if (string.Equals(desc, type, StringComparison.Ordinal)) desc = "";
        if (type.Length == 0 && desc.Length == 0) desc = "sub-agent";

        int x = labelX;
        if (type.Length > 0)
        {
            var typeTrunc = TruncateString(g, type, nameFont, labelMaxW / 2);
            var typeSz    = g.MeasureString(typeTrunc, nameFont);
            g.DrawString(typeTrunc, nameFont, mutedBrush, x, midY - typeSz.Height / 2);
            x += (int)typeSz.Width + 8;   // type + gap before the description
        }

        if (desc.Length > 0)
        {
            var descTrunc = TruncateString(g, desc, nameFont, labelMaxW - (x - labelX));
            var descSz    = g.MeasureString(descTrunc, nameFont);
            g.DrawString(descTrunc, nameFont, fgBrush, x, midY - descSz.Height / 2);
        }

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, subBrush, statusX, midY - statusSz.Height / 2);
    }

    // A teammate row: [person glyph] @name   <activity>            <state>
    // The name + glyph take the member's Claude-assigned colour; an idle teammate (waiting on the lead)
    // is dimmed toward the background so the working members stand out, and shows "idle" instead of an
    // activity phrase.
    private void DrawTeammateRow(Graphics g, SubAgent sub, int glyphX, int midY)
    {
        bool idle      = sub.IsIdle;
        Color teamColor = Theme.TeamColor(sub.Color);
        // Dim everything ~55% toward the background while idle; full strength while working.
        Color nameColor = idle ? Theme.Blend(teamColor, BgColor, 0.55f) : teamColor;
        Color textColor = idle ? Theme.Blend(FgColor, BgColor, 0.55f)   : FgColor;

        DrawTeammateGlyph(g, glyphX, midY, nameColor);

        using var nameFont   = new Font("Segoe UI", 8f, GraphicsUnit.Point) ;
        using var statusFont = new Font("Segoe UI", 7f, GraphicsUnit.Point);
        using var nameBrush  = new SolidBrush(nameColor);
        using var textBrush  = new SolidBrush(textColor);

        // Right-aligned state word: "idle" (muted) or "working" (member colour).
        string stateText = idle ? "idle" : "working";
        var stateSz   = g.MeasureString(stateText, statusFont);
        int labelX    = glyphX + 16;                 // clear of the person glyph
        int labelMaxW = ClientSize.Width - labelX - HorizPad - (int)stateSz.Width - 6;

        // "@name" in the member colour leads the row.
        string handle = "@" + (string.IsNullOrWhiteSpace(sub.Name) ? "teammate" : sub.Name!.Trim());
        var handleTrunc = TruncateString(g, handle, nameFont, labelMaxW);
        var handleSz    = g.MeasureString(handleTrunc, nameFont);
        g.DrawString(handleTrunc, nameFont, nameBrush, labelX, midY - handleSz.Height / 2);
        int x = labelX + (int)handleSz.Width + 8;

        // Then what it's doing now (muted), only while working and only if it fits.
        string activity = idle ? "" : (sub.Activity?.Trim() ?? "");
        if (activity.Length > 0)
        {
            int remaining = labelMaxW - (x - labelX);
            if (remaining > 24)
            {
                var actTrunc = TruncateString(g, activity, nameFont, remaining);
                var actSz    = g.MeasureString(actTrunc, nameFont);
                g.DrawString(actTrunc, nameFont, textBrush, x, midY - actSz.Height / 2);
            }
        }

        int stateX = ClientSize.Width - HorizPad - (int)stateSz.Width;
        g.DrawString(stateText, statusFont, idle ? textBrush : nameBrush, stateX, midY - stateSz.Height / 2);
    }

    // A small "person" mark — a head circle above a shoulders arc — in the given colour, centred on
    // (x, midY). Pure GDI so it themes and DPI-scales like the overlay's other glyphs.
    private static void DrawTeammateGlyph(Graphics g, int x, int midY, Color color)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(color);
        // Head: a 5px circle sitting just above centre.
        const int headD = 5;
        g.FillEllipse(brush, x, midY - 5, headD, headD);
        // Shoulders: a half-disc below the head, clipped to its top half so it reads as a torso.
        var shoulders = new Rectangle(x - 1, midY + 1, headD + 3, headD + 2);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(shoulders, 180, 180);
        path.CloseFigure();
        g.FillPath(brush, path);

        g.SmoothingMode = oldSmoothing;
    }

    private void DrawSessionRow(Graphics g, int rowIdx)
    {
        var session = _rows[rowIdx].Session!;
        int top     = RowTop(rowIdx);
        int midY    = top + RowHeight / 2;

        using var sepPen = new Pen(SepColor, 1f);
        g.DrawLine(sepPen, HorizPad, top, ClientSize.Width - HorizPad, top);

        if (rowIdx == _hoveredRow)
        {
            using var hoverBrush = new SolidBrush(RowHoverColor);
            g.FillRectangle(hoverBrush, 1, top + 1, ClientSize.Width - 2, RowHeight - 1);
        }

        // A running session gets a second, dimmer line: the parsed tool call on the left and the
        // elapsed run time on the right. Without either the project name stays vertically centred.
        bool running   = session.Status == SessionStatus.Running;
        // An awaiting-input row shows a "waiting on you" timer on its second line instead of activity.
        bool awaiting  = session.Status == SessionStatus.AwaitingInput && _showWaitingTimer;
        // While working a checklist, the active task's gerund ("Building slash commands…") is more
        // telling than the raw tool phrase, so it wins for the activity line; fall back to the tool.
        var activity   = running
            ? (session.CurrentTask?.ActiveForm is { Length: > 0 } af ? af : session.Activity)
            : awaiting ? "waiting on you"
            : null;
        var elapsed    = running  ? session.RunningElapsedLabel()
                       : awaiting ? session.AwaitingElapsedLabel()
                       : null;
        bool twoLine   = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        int nameMidY   = twoLine ? top + RowHeight / 2 - 8 : midY;

        // The second line is dim for a running row; for a blocked row it warms from the awaiting-yellow
        // toward alarm-red the longer the session has been waiting, so a stale block draws the eye.
        Color secondLineColor = awaiting
            ? WarmWaitingColor(session.AwaitingElapsed() ?? TimeSpan.Zero)
            : MutedColor;

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };

        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, HorizPad, nameMidY - 4, 8, 8);

        using var nameFont     = new Font("Segoe UI", 8.5f, GraphicsUnit.Point);
        using var statusFont   = new Font("Segoe UI", 7.5f, GraphicsUnit.Point);
        using var activityFont = new Font("Segoe UI", 7.5f, GraphicsUnit.Point);
        using var fgBrush      = new SolidBrush(FgColor);
        using var mutedBrush   = new SolidBrush(MutedColor);
        using var secondLineBrush = new SolidBrush(secondLineColor);
        using var attnBrush    = new SolidBrush(AttentionColor);
        using var awaitBrush   = new SolidBrush(AwaitingColor);

        var statusText = session.Status switch
        {
            SessionStatus.Running        => "running",
            SessionStatus.NeedsAttention => "done ↩",
            SessionStatus.AwaitingInput  => "input ↩",
            _                            => "idle",
        };

        Brush statusBrush = session.Status switch
        {
            SessionStatus.NeedsAttention => attnBrush,
            SessionStatus.AwaitingInput  => awaitBrush,
            _                            => mutedBrush,
        };

        bool hasArtifacts= _showArtifacts && session.HasArtifacts;
        int artWidth     = hasArtifacts ? ArtifactIconWidth : 0;
        bool mail        = ExternalNotifyEnabled(session);
        int mailWidth    = mail ? MailIconWidth : 0;
        int badgeWidth   = session.Mode != PermissionMode.Normal && _showModeBadges ? 16 : 0;
        int rcWidth      = session.RemoteControlled ? RcIconWidth : 0;
        bool isBackground= session.IsBackground;
        int botWidth     = isBackground ? BotIconWidth : 0;
        bool party       = ConfettiArmed(session);
        int partyWidth   = party ? PartyIconWidth : 0;
        bool stuck       = _showStuckWarnings && session.IsStuck;
        int warnWidth    = stuck ? WarnIconWidth : 0;
        float ctxFill    = session.ContextFill ?? 0f;
        // Normally the thermometer only appears once fill crosses yellow. With the green "first
        // segment" indicator on, it also shows below yellow (green) whenever a fill is actually known,
        // so a low-but-nonzero context is visible instead of blank.
        bool showThermo  = _showContextPressure
                        && (ctxFill >= _ctxYellow || (_showContextGreenSegment && session.ContextFill.HasValue));
        int thermoWidth  = showThermo ? ThermoIconWidth + 2 : 0;  // icon + 2 px gap right
        // Task checklist progress: a dim "completed/total" count on the name line, hover for the list.
        bool hasTasks    = _showTaskProgress && session.HasTasks;
        string taskLabel = hasTasks ? $"{session.CompletedTaskCount}/{session.Tasks.Count}" : "";
        var taskSz       = hasTasks ? g.MeasureString(taskLabel, statusFont) : SizeF.Empty;
        int taskWidth    = hasTasks ? (int)taskSz.Width + 8 : 0;  // count + gap to the badge/status on its right
        // Per-session resource mini-bars: shown when enabled and a sample exists for this session's pid
        // (ProcessCount 0 means "no reading yet"). Sub-agent rows never show them — a sub-agent shares
        // its parent's OS process, so its usage isn't separable and folds into the parent row's total.
        _sessionMetrics.TryGetValue(session.Pid, out var metrics);
        bool showMetrics = _showSessionMetrics && metrics.ProcessCount > 0;
        int metricsWidth = showMetrics ? MetricsBarWidth : 0;
        // Live token burn rate ("12.3k/m"), on running rows only — a rate is only meaningful while working.
        bool showBurn    = _showBurnRate && running && session.BurnRate is > 0;
        string burnLabel = showBurn ? StatsFormat.Tokens((long)session.BurnRate!.Value) + "/m" : "";
        var burnSz       = showBurn ? g.MeasureString(burnLabel, statusFont) : SizeF.Empty;
        int burnWidth    = showBurn ? (int)burnSz.Width + 8 : 0;  // rate + gap to whatever's on its right
        // Unstaged git line churn ("+142 -37"): the "+added" half in green, the "-deleted" half in red.
        // Only shown when there's actual churn (a clean tree is hidden, like the burn rate when idle).
        var gitStats     = _showGitStats ? session.GitStats : null;
        bool showGit     = gitStats is { IsEmpty: false };
        string gitAdd    = showGit ? $"+{gitStats!.Value.Added}" : "";
        string gitDel    = showGit ? $"-{gitStats!.Value.Deleted}" : "";
        var gitAddSz     = showGit ? g.MeasureString(gitAdd, statusFont) : SizeF.Empty;
        var gitDelSz     = showGit ? g.MeasureString(gitDel, statusFont) : SizeF.Empty;
        const int GitGap = 4;                                    // between the +added and -deleted halves
        int gitWidth     = showGit ? (int)gitAddSz.Width + GitGap + (int)gitDelSz.Width + 8 : 0;
        var statusSz     = g.MeasureString(statusText, statusFont);
        int nameMaxWidth = ClientSize.Width - HorizPad * 3 - 8 - (int)statusSz.Width - badgeWidth - rcWidth - botWidth - partyWidth - mailWidth - artWidth - warnWidth - thermoWidth - taskWidth - metricsWidth - burnWidth - gitWidth;
        var nameTrunc    = TruncateString(g, session.DisplayName, nameFont, nameMaxWidth);
        var nameSz       = g.MeasureString(nameTrunc, nameFont);

        // Glyphs sit just right of the status dot and push the name across: the warning glyph first
        // (the loudest — this session may be stuck), then the artifact glyph (clickable to open/pick a
        // published artifact), then mail (external notifications), then the remote-control glyph.
        if (stuck)
        {
            DrawWarnIcon(g, HorizPad + 14, nameMidY);
            _warnRects[rowIdx] = new Rectangle(HorizPad + 14, nameMidY - 9, WarnIconWidth, 18);
        }
        if (hasArtifacts)
            DrawArtifactIcon(g, HorizPad + 14 + warnWidth, nameMidY, rowIdx == _hoveredArtifactRow);
        if (mail)
            DrawMailIcon(g, HorizPad + 14 + warnWidth + artWidth, nameMidY);
        if (session.RemoteControlled)
            DrawRemoteIcon(g, HorizPad + 16 + warnWidth + artWidth + mailWidth, nameMidY);
        if (party)
            DrawPartyIcon(g, HorizPad + 14 + warnWidth + artWidth + mailWidth + rcWidth, nameMidY);
        if (isBackground)
            DrawBotIcon(g, HorizPad + 14 + warnWidth + artWidth + mailWidth + rcWidth + partyWidth, nameMidY);

        int nameX = HorizPad + 14 + warnWidth + artWidth + mailWidth + rcWidth + partyWidth + botWidth;
        g.DrawString(nameTrunc, nameFont, fgBrush, nameX, nameMidY - nameSz.Height / 2);

        // Unstaged git line churn, immediately right of the name: "+added" in green, "-deleted" in red,
        // so the split reads at a glance like a diff stat. Space for it is reserved in nameMaxWidth above,
        // so a long name truncates to leave room rather than colliding with the chip.
        if (showGit)
        {
            int gitX = nameX + (int)nameSz.Width + 6;
            using var addBrush = new SolidBrush(GitAddColor);
            using var delBrush = new SolidBrush(GitDelColor);
            g.DrawString(gitAdd, statusFont, addBrush, gitX, nameMidY - gitAddSz.Height / 2);
            g.DrawString(gitDel, statusFont, delBrush,
                gitX + (int)gitAddSz.Width + GitGap, nameMidY - gitDelSz.Height / 2);
        }

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, statusBrush,
            statusX, nameMidY - statusSz.Height / 2);

        // Thermometer: to the right of the mode badge (between badge and status text). Remember a
        // generous hit-rect so a hover can pop the "Context at NN%" tooltip.
        if (thermoWidth > 0)
        {
            DrawThermoIcon(g, ctxFill, statusX - thermoWidth, nameMidY);
            _thermoRects[rowIdx] = new Rectangle(statusX - thermoWidth, nameMidY - 9, thermoWidth, 18);
        }

        if (session.Mode != PermissionMode.Normal && _showModeBadges)
        {
            // Idle sessions dim the badge so the permission colour stops drawing the eye when nothing's
            // happening; active/awaiting/attention rows keep it at full strength.
            int badgeAlpha = session.Status == SessionStatus.Idle ? 110 : 255;
            Glyphs.DrawModeBadge(g, session.Mode, statusX - thermoWidth - badgeWidth, nameMidY, 4, 5, badgeAlpha);
        }

        // Task-list progress count, just left of the mode badge. A finished list (all tasks done) is
        // tinted the running-green so it reads as "completed" at a glance; otherwise it's dim like status.
        if (hasTasks)
        {
            int taskX = statusX - thermoWidth - badgeWidth - taskWidth;
            bool allDone = session.CompletedTaskCount == session.Tasks.Count;
            using var taskBrush = new SolidBrush(allDone ? RunningColor : MutedColor);
            g.DrawString(taskLabel, statusFont, taskBrush, taskX, nameMidY - taskSz.Height / 2);
            _taskRects[rowIdx] = new Rectangle(taskX - 2, nameMidY - 9, (int)taskSz.Width + 6, 18);
        }

        // Resource mini-bars, just left of the task count. Remember a generous hit-rect so a hover can
        // pop the fine-grained CPU%/RAM tooltip.
        if (showMetrics)
        {
            int metricsX = statusX - thermoWidth - badgeWidth - taskWidth - metricsWidth;
            DrawMetricsBars(g, metrics, metricsX, nameMidY);
            _metricsRects[rowIdx] = new Rectangle(metricsX, nameMidY - 9, metricsWidth, 18);
        }

        // Live token burn rate, just left of the metrics bars. Drawn in a soft blue so it reads as a
        // live figure distinct from the dim task count, without competing with the status colours.
        if (showBurn)
        {
            int burnX = statusX - thermoWidth - badgeWidth - taskWidth - metricsWidth - burnWidth;
            using var burnBrush = new SolidBrush(BurnColor);
            g.DrawString(burnLabel, statusFont, burnBrush, burnX, nameMidY - burnSz.Height / 2);
        }

        if (twoLine)
        {
            int activityMidY = top + RowHeight / 2 + 9;
            int lineLeft     = HorizPad + 14;

            // Elapsed time, right-aligned. Dim for a running row; warming for a blocked one.
            int elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                var elapsedSz = g.MeasureString(elapsed, activityFont);
                elapsedW = (int)elapsedSz.Width;
                g.DrawString(elapsed, activityFont, secondLineBrush,
                    ClientSize.Width - HorizPad - elapsedW, activityMidY - elapsedSz.Height / 2);
            }

            // Activity phrase (or "waiting on you") fills the remaining width left of the elapsed time.
            if (!string.IsNullOrEmpty(activity))
            {
                int activityMaxW  = ClientSize.Width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                var activityTrunc = TruncateString(g, activity, activityFont, activityMaxW);
                var activitySz    = g.MeasureString(activityTrunc, activityFont);
                g.DrawString(activityTrunc, activityFont, secondLineBrush,
                    lineLeft, activityMidY - activitySz.Height / 2);
            }
        }
    }


    // "Waiting on you" timer colour: interpolates from the awaiting-yellow at 0 to alarm-red at the
    // top of the ramp, so a session that's been blocked a while visibly heats up. Linear in each
    // channel; clamped so it never overshoots red once the wait is long enough. The ramp length is
    // user-tunable via _waitingTimerRedMinutes.
    private Color WarmWaitingColor(TimeSpan waited)
    {
        double fullMinutes = Math.Max(1, _waitingTimerRedMinutes);   // fully red once blocked this long
        double t = Math.Clamp(waited.TotalMinutes / fullMinutes, 0.0, 1.0);
        var to   = Color.FromArgb(239, 68, 68);   // alarm-red
        int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * t);
        return Color.FromArgb(
            Lerp(AwaitingColor.R, to.R),
            Lerp(AwaitingColor.G, to.G),
            Lerp(AwaitingColor.B, to.B));
    }

    // Stuck-detection warning: a small amber triangle with an exclamation mark, drawn at the left of
    // a flagged row. x is the left edge of the reserved WarnIconWidth area; midY is the row centre.
    private void DrawWarnIcon(Graphics g, int x, int midY)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int w = 12, h = 11;
        int top = midY - h / 2;
        int cx  = x + w / 2;

        var triangle = new[]
        {
            new Point(cx,        top),         // apex
            new Point(x,         top + h),     // bottom-left
            new Point(x + w,     top + h),     // bottom-right
        };

        using var fill = new SolidBrush(WarnColor);
        g.FillPolygon(fill, triangle);

        // Exclamation mark punched in the panel background so it reads at small sizes: a short stem
        // above a square dot, both centred on the triangle.
        using var mark = new SolidBrush(BgColor);
        g.FillRectangle(mark, cx - 1, top + 3, 2, 4);   // stem
        g.FillRectangle(mark, cx - 1, top + 8, 2, 2);   // dot

        g.SmoothingMode = old;
    }

    // Context-pressure thermometer: tube (4 px wide, 9 px tall) + bulb (8 px diameter), with mercury
    // rising from the bottom. Drawn at/above the yellow threshold; colour shifts yellow → orange → red
    // at the configured thresholds. With the green "first segment" indicator on it also draws below
    // yellow, in green. x is the left edge of the reserved ThermoIconWidth area; midY is the row centre.
    private void DrawThermoIcon(Graphics g, float fill, int x, int midY)
    {
        if (fill < _ctxYellow && !_showContextGreenSegment) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color col = fill >= _ctxRed    ? Color.FromArgb(239, 68,  68)   // red
                  : fill >= _ctxOrange ? Color.FromArgb(249, 115, 22)   // orange
                  : fill >= _ctxYellow ? Color.FromArgb(234, 179,  8)   // yellow
                                       : Color.FromArgb(34,  197, 94);  // green (below-yellow first segment)

        // Tube: 4 px wide, top at midY-7, bottom at midY+2 (9 px).
        // Bulb: 8 px diameter circle whose top edge overlaps the tube bottom by 2 px.
        int cx     = x + 5;
        var tube   = new Rectangle(cx - 2, midY - 7, 4, 9);
        var bulb   = new Rectangle(cx - 4, midY,     8, 8);

        using var dimBrush     = new SolidBrush(Color.FromArgb(30,  255, 255, 255));
        using var colBrush     = new SolidBrush(col);
        using var outlinePen   = new Pen(Color.FromArgb(80,  255, 255, 255), 1f);

        // Glass background.
        using var tubePath = PaintKit.RoundedRect(tube, 2);
        g.FillPath(dimBrush, tubePath);
        g.FillEllipse(dimBrush, bulb);

        // Mercury fill inside the tube (rises from bottom).
        int innerH  = tube.Height - 2;  // 1 px margin top and bottom
        int fillPx  = Math.Clamp((int)(fill * innerH), 0, innerH);
        if (fillPx > 0)
            g.FillRectangle(colBrush,
                new Rectangle(tube.X + 1, tube.Bottom - 1 - fillPx, tube.Width - 2, fillPx));

        // Bulb is always filled when the icon is visible.
        g.FillEllipse(colBrush,
            new Rectangle(bulb.X + 1, bulb.Y + 1, bulb.Width - 2, bulb.Height - 2));

        // Glass outline.
        g.DrawPath(outlinePen, tubePath);
        g.DrawEllipse(outlinePen, bulb);

        g.SmoothingMode = old;
    }

    // A session's rolled-up resource use as two stacked micro-bars — CPU on top, RAM below — each
    // filled proportionally and coloured by load (green → red via Theme.UsageColor). CPU is a
    // percentage of the whole machine; RAM is drawn against total physical RAM (the same denominator
    // as the top strip's RAM bar), so a row's bars are directly comparable to the machine total. x is
    // the left edge of the reserved MetricsBarWidth; midY is the row's name-line centre.
    private void DrawMetricsBars(Graphics g, SessionMetrics m, int x, int midY)
    {
        const int barH = 3, gap = 2;
        int barW = MetricsBarWidth - 4;      // small inset within the reserved width
        int cpuY = midY - gap / 2 - barH;    // top bar sits just above centre
        int ramY = midY + (gap + 1) / 2;     // bottom bar just below

        double cpuPct = Math.Clamp(m.CpuPercent, 0, 100);
        double ramPct = _sysMetrics.TotalRamBytes > 0
            ? Math.Clamp(100.0 * m.RamBytes / _sysMetrics.TotalRamBytes, 0, 100)
            : 0;

        DrawMiniBar(g, x, cpuY, barW, barH, cpuPct);
        DrawMiniBar(g, x, ramY, barW, barH, ramPct);
    }

    private static void DrawMiniBar(Graphics g, int x, int y, int w, int h, double pct)
    {
        using (var track = new SolidBrush(UsageTrackColor))
            PaintKit.FillRoundedBar(g, track, x, y, w, h);

        int fillW = (int)Math.Round(w * pct / 100.0);
        if (fillW > 0)
            using (var fill = new SolidBrush(Theme.UsageColor(pct)))
                PaintKit.FillRoundedBar(g, fill, x, y, fillW, h);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string TruncateString(Graphics g, string text, Font font, int maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;
        while (text.Length > 0 && g.MeasureString(text + "…", font).Width > maxWidth)
            text = text[..^1];
        return text + "…";
    }

    // ── Mouse interaction ────────────────────────────────────────────────────
    protected override void OnMouseDown(MouseEventArgs e)
    {
        // The closed dense strip is draggable anywhere; otherwise only the header is a drag handle.
        bool inDragHandle = _denseMode.IsClosedStrip || e.Y < HeaderHeight;
        if (e.Button == MouseButtons.Left && inDragHandle)
        {
            _dragging       = true;
            _wasDrag        = false;
            _dragStartScreen = PointToScreen(e.Location);
            _formStartLoc   = Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var cur = PointToScreen(e.Location);
            int dx  = cur.X - _dragStartScreen.X;
            int dy  = cur.Y - _dragStartScreen.Y;

            if (!_wasDrag && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                _wasDrag = true;
                if (_denseMode.IsDense) _denseMode.ShowDropZones();
            }

            if (_wasDrag)
            {
                if (_denseMode.IsDense)
                    // Dense stays hugging the current monitor's docked edge, moving only vertically;
                    // drop lanes let it be re-pinned to another edge or monitor on release.
                    _denseMode.DragVertical(_formStartLoc.Y + dy, cur);
                else
                    Location = new Point(_formStartLoc.X + dx, _formStartLoc.Y + dy);
            }
        }
        else
        {
            int newHover = HitTestRow(e.Location);
            if (newHover != _hoveredRow)
            {
                _hoveredRow = newHover;
                Invalidate();
            }

            // Update badge hover: hand cursor + a brighter disc. Change-detected so it doesn't fight
            // the quick-link/artifact cursor handlers below (they only touch the cursor on transition).
            bool overUpdate = _updateAvailable && !_denseMode.IsClosedStrip && _updateIconRect.Contains(e.Location);
            if (overUpdate != _hoveredUpdateIcon)
            {
                _hoveredUpdateIcon = overUpdate;
                Cursor = overUpdate ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            // Dwell over the usage strip (only the two bar rows, not the quick-links row below them).
            int usageStripTop = UsageStripTop;
            bool inStrip = ShowFullPanel && _usageEnabled && e.Y >= usageStripTop && e.Y < usageStripTop + UsageStripHeight;
            if (inStrip != _inUsageStrip)
            {
                _inUsageStrip = inStrip;
                if (inStrip)
                {
                    _usageHoverTimer.Stop();
                    _usageHoverTimer.Start();
                }
                else
                {
                    _usageHoverTimer.Stop();
                    HideUsageTooltip();
                }
            }

            // Quick-link icons row hover (per-icon hit test).
            int hovered = ShowFullPanel ? HitTestQuickLink(e.Location) : -1;
            if (hovered != _hoveredQuickLink)
            {
                _hoveredQuickLink = hovered;
                Cursor = hovered >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            // Artifact glyph hover (clickable; hand cursor + a brighter glyph). Lives inside the
            // session rows, a different region from the quick-links row above, so the two cursor
            // updates never fight over the same point.
            int artHover = ShowFullPanel ? HitTestArtifactIcon(e.Location) : -1;
            if (artHover != _hoveredArtifactRow)
            {
                _hoveredArtifactRow = artHover;
                Cursor = artHover >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            // Context-pressure thermometer dwell. Not clickable, so it leaves the cursor alone; a
            // 150ms settle pops the tooltip, and moving to a different (or no) thermometer restarts.
            int thermoHover = ShowFullPanel ? HitTestThermoIcon(e.Location) : -1;
            if (thermoHover != _hoveredThermoRow)
            {
                _hoveredThermoRow = thermoHover;
                _thermoHoverTimer.Stop();
                HideThermoTooltip();
                if (thermoHover >= 0)
                    _thermoHoverTimer.Start();
            }

            // Stuck-warning glyph dwell — twin to the thermometer's above.
            int warnHover = ShowFullPanel ? HitTestWarnIcon(e.Location) : -1;
            if (warnHover != _hoveredWarnRow)
            {
                _hoveredWarnRow = warnHover;
                _warnHoverTimer.Stop();
                HideWarnTooltip();
                if (warnHover >= 0)
                    _warnHoverTimer.Start();
            }

            // Task-count badge dwell — twin to the thermometer/warning above; pops the full checklist.
            int taskHover = ShowFullPanel ? HitTestTaskCount(e.Location) : -1;
            if (taskHover != _hoveredTaskRow)
            {
                _hoveredTaskRow = taskHover;
                _taskHoverTimer.Stop();
                HideTaskTooltip();
                if (taskHover >= 0)
                    _taskHoverTimer.Start();
            }

            // Metrics mini-bar dwell — twin to the badges above; pops the CPU%/RAM numbers.
            int metricsHover = ShowFullPanel ? HitTestMetrics(e.Location) : -1;
            if (metricsHover != _hoveredMetricsRow)
            {
                _hoveredMetricsRow = metricsHover;
                _metricsHoverTimer.Stop();
                HideMetricsTooltip();
                if (metricsHover >= 0)
                    _metricsHoverTimer.Start();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        // Clear the drag state first: the click handlers below call RelayoutWindow(), which
        // no-ops while a drag is in progress.
        bool wasDrag = _wasDrag;
        _dragging = false;
        _wasDrag  = false;

        // A dense drag released over another monitor's drop lane re-pins the strip there.
        if (wasDrag && _denseMode.IsDense)
            _denseMode.PinToActiveDropZone();
        _denseMode.HideDropZones();

        // Let the owner move the ambient glow to whatever screen we landed on.
        if (wasDrag)
            DragCompleted?.Invoke(this, EventArgs.Empty);

        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenuAt(e.Location);
            base.OnMouseUp(e);
            return;
        }

        if (e.Button == MouseButtons.Left && !wasDrag)
        {
            bool headerVisible = !_denseMode.IsClosedStrip;

            if (headerVisible && _updateAvailable && _updateIconRect.Contains(e.Location))
            {
                // The update badge: ask the owning context to download and apply the pending update.
                UpdateRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (headerVisible && SideIconRect().Contains(e.Location))
            {
                // The dense toggle: enter dense from floating, or leave it from the open popup.
                _denseMode.Toggle();
            }
            else if (HitTestArtifactIcon(e.Location) is var artRow && artRow >= 0)
            {
                // The artifact glyph sits inside a session row, so it must be checked before the
                // row's focus-terminal click — clicking it opens (or lets you pick) the artifact.
                OpenArtifactsForRow(artRow);
            }
            else
            {
                int row = HitTestRow(e.Location);
                if (row >= 0 && _rows[row].IsSectionHeader)
                {
                    // The "Autonomous" divider: toggle the section open/closed and relayout.
                    _autonomousExpanded = !_autonomousExpanded;
                    UpdateSessions(_sessions);
                }
                else if (row >= 0)
                {
                    // Sub-agent rows resolve to their parent session — the sub-agent runs in the
                    // parent's process, so focusing means focusing the parent terminal.
                    var pid = _rows[row].Session!.Pid;
                    SessionFocused?.Invoke(pid);
                    if (int.TryParse(pid, out int pidInt))
                        NativeMethods.FocusTerminalForProcess(pidInt, _rows[row].Session!.ProjectName);
                }
                else if (HitTestQuickLink(e.Location) is var ql && ql >= 0)
                {
                    QuickLinkLauncher.LaunchOrFocus(_quickLinks[ql]);
                }
                else if (!_denseMode.IsDense && e.Y < HeaderHeight && _sessions.Count > 0)
                {
                    // Header click toggles expand/collapse — floating mode only.
                    _expanded = !_expanded;
                    RelayoutWindow();
                    UpdateTickTimer();
                    Invalidate();
                }
            }
        }

        base.OnMouseUp(e);
    }

    // Hovering the dense strip pops the full panel open; any re-entry cancels a pending auto-close.
    protected override void OnMouseEnter(EventArgs e)
    {
        if (_denseMode.IsDense)
            _denseMode.OnMouseEntered();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoveredRow = -1;
        _inUsageStrip = false;
        _usageHoverTimer.Stop();
        HideUsageTooltip();
        _hoveredThermoRow = -1;
        _thermoHoverTimer.Stop();
        HideThermoTooltip();
        _hoveredWarnRow = -1;
        _warnHoverTimer.Stop();
        HideWarnTooltip();
        _hoveredTaskRow = -1;
        _taskHoverTimer.Stop();
        HideTaskTooltip();
        _hoveredMetricsRow = -1;
        _metricsHoverTimer.Stop();
        HideMetricsTooltip();
        if (_hoveredQuickLink >= 0) { _hoveredQuickLink = -1; Cursor = Cursors.Default; }
        if (_hoveredArtifactRow >= 0) { _hoveredArtifactRow = -1; Cursor = Cursors.Default; }
        if (_hoveredUpdateIcon) { _hoveredUpdateIcon = false; Cursor = Cursors.Default; }

        // Start the countdown to collapse the dense popup back to the strip — but not mid-drag,
        // where the cursor legitimately roams to another monitor's drop lane.
        if (_denseMode.IsDense && _denseMode.IsOpen && !_dragging)
            _denseMode.SchedulePopupClose();

        Invalidate();
        base.OnMouseLeave(e);
    }

    // ── Usage tooltip ──────────────────────────────────────────────────────────
    private void ShowUsageTooltip()
    {
        var stripScreen = RectangleToScreen(new Rectangle(0, UsageStripTop, ClientSize.Width, UsageStripHeight));
        _usageTooltip.ShowFor(_usage, stripScreen);
    }

    private void HideUsageTooltip()
    {
        if (_usageTooltip.Visible)
            _usageTooltip.Hide();
    }

    // ── Context-pressure tooltip ────────────────────────────────────────────────
    // Returns the row whose thermometer hit-rect contains p, or -1. Reads the rects captured at
    // paint time, so it tracks exactly where each glyph was drawn.
    private int HitTestThermoIcon(Point p)
    {
        foreach (var (row, rect) in _thermoRects)
            if (rect.Contains(p))
                return row;
        return -1;
    }

    private void ShowThermoTooltip(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var session = _rows[rowIdx].Session!;
        float fill = session.ContextFill ?? 0f;
        int pct = (int)Math.Round(fill * 100f);
        // Reconstruct the token count from the fill and the resolved window (fill = used / window).
        long window = session.ContextWindow;
        long used   = (long)Math.Round(fill * window);

        // Anchor just below the glyph; HintTooltipForm clamps it onto the screen.
        var anchor = PointToScreen(new Point(_thermoRects[rowIdx].Left, _thermoRects[rowIdx].Bottom + 4));
        _thermoTooltip.ShowText($"{FormatTokens(used)}/{FormatTokens(window)} ({pct}%)", anchor);
    }

    // Compact token count for the thermometer tooltip: 34631 -> "34.6k", 200000 -> "200k", 1000000 -> "1M".
    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.##}M"
      : n >= 1_000     ? $"{n / 1_000.0:0.#}k"
                       : n.ToString();

    private void HideThermoTooltip()
    {
        if (_thermoTooltip.Visible)
            _thermoTooltip.Hide();
    }

    // ── Stuck-warning tooltip ───────────────────────────────────────────────────
    // Returns the row whose warning hit-rect contains p, or -1 (rects captured at paint time).
    private int HitTestWarnIcon(Point p)
    {
        foreach (var (row, rect) in _warnRects)
            if (rect.Contains(p))
                return row;
        return -1;
    }

    private void ShowWarnTooltip(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        if (_rows[rowIdx].Session!.Stuck is not { } stuck) return;

        // Anchor just below the glyph; HintTooltipForm clamps it onto the screen.
        var anchor = PointToScreen(new Point(_warnRects[rowIdx].Left, _warnRects[rowIdx].Bottom + 4));
        _warnTooltip.ShowText(stuck.Reason, anchor);
    }

    private void HideWarnTooltip()
    {
        if (_warnTooltip.Visible)
            _warnTooltip.Hide();
    }

    // ── Task-list tooltip ───────────────────────────────────────────────────────
    // Returns the row whose task-count hit-rect contains p, or -1 (rects captured at paint time).
    private int HitTestTaskCount(Point p)
    {
        foreach (var (row, rect) in _taskRects)
            if (rect.Contains(p))
                return row;
        return -1;
    }

    private void ShowTaskTooltip(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        var tasks = _rows[rowIdx].Session!.Tasks;
        if (tasks.Count == 0) return;

        // One line per task, prefixed by a status glyph: ✓ done, ▸ in progress, ○ pending. The active
        // task reads better as its gerund ("Building …"); the rest by subject. Long labels are clipped
        // so the (single-line-measured) tooltip can't run off the screen edge.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < tasks.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var t = tasks[i];
            char glyph = t.State switch
            {
                TaskState.Completed  => '✓',
                TaskState.InProgress => '▸',
                _                    => '○',
            };
            string label = t.State == TaskState.InProgress && t.ActiveForm.Length > 0 ? t.ActiveForm : t.Subject;
            if (label.Length > 64)
                label = label[..63].TrimEnd() + "…";
            sb.Append(glyph).Append(' ').Append(label);
        }

        // Anchor just below the badge; HintTooltipForm clamps it onto the screen.
        var anchor = PointToScreen(new Point(_taskRects[rowIdx].Left, _taskRects[rowIdx].Bottom + 4));
        _taskTooltip.ShowText(sb.ToString(), anchor);
    }

    private void HideTaskTooltip()
    {
        if (_taskTooltip.Visible)
            _taskTooltip.Hide();
    }

    // ── Metrics tooltip ─────────────────────────────────────────────────────────
    // Returns the row whose metrics-bar hit-rect contains p, or -1 (rects captured at paint time).
    private int HitTestMetrics(Point p)
    {
        foreach (var (row, rect) in _metricsRects)
            if (rect.Contains(p))
                return row;
        return -1;
    }

    private void ShowMetricsTooltip(int rowIdx)
    {
        if (rowIdx < 0 || rowIdx >= _rows.Count) return;
        if (!_sessionMetrics.TryGetValue(_rows[rowIdx].Session!.Pid, out var m)) return;

        // "CPU 34%  ·  RAM 512 MB" — plus the process count when the tree was rolled up (subprocess
        // metrics on), which is what makes the number more than just the bare claude process.
        string text = $"CPU {m.CpuPercent:0}%   ·   RAM {FormatBytes(m.RamBytes)}";
        if (m.ProcessCount > 1)
            text += $"   ·   {m.ProcessCount} procs";

        var anchor = PointToScreen(new Point(_metricsRects[rowIdx].Left, _metricsRects[rowIdx].Bottom + 4));
        _metricsTooltip.ShowText(text, anchor);
    }

    private void HideMetricsTooltip()
    {
        if (_metricsTooltip.Visible)
            _metricsTooltip.Hide();
    }

    // Compact byte count for the metrics tooltip: 536870912 -> "512 MB", 1610612736 -> "1.5 GB".
    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} KB";
        return $"{bytes} B";
    }

    private int HitTestRow(Point p)
    {
        if (!ShowFullPanel || p.Y < RowsTop) return -1;
        int y = RowsTop;
        for (int i = 0; i < _rows.Count; i++)
        {
            int h = HeightOf(_rows[i]);
            if (p.Y >= y && p.Y < y + h)
                return i;
            y += h;
        }
        return -1;
    }

    // The vertical centre of a session row's name line — shifted up when the row shows a second
    // (activity/elapsed) line. Mirrors the layout in DrawSessionRow so the artifact glyph's hit
    // rectangle lines up with where it's painted.
    private int NameMidY(ClaudeSession s, int top)
    {
        bool running  = s.Status == SessionStatus.Running;
        bool awaiting = s.Status == SessionStatus.AwaitingInput && _showWaitingTimer;
        bool twoLine  = (running && (!string.IsNullOrEmpty(s.Activity) || !string.IsNullOrEmpty(s.RunningElapsedLabel())))
                     || (awaiting && !string.IsNullOrEmpty(s.AwaitingElapsedLabel()));
        return twoLine ? top + RowHeight / 2 - 8 : top + RowHeight / 2;
    }

    // The clickable rectangle of a row's artifact glyph, or Rectangle.Empty when the row has none
    // (or is a sub-agent row). Kept generous so the small glyph is easy to click.
    private Rectangle ArtifactIconRect(int rowIdx)
    {
        var row = _rows[rowIdx];
        if (row.IsSectionHeader || row.IsSubAgent || !_showArtifacts || !row.Session!.HasArtifacts)
            return Rectangle.Empty;

        int top  = RowTop(rowIdx);
        int midY = NameMidY(row.Session!, top);
        return new Rectangle(HorizPad + 12, midY - 9, ArtifactIconWidth, 18);
    }

    // Returns the index of the row whose artifact glyph contains p, or -1. Used both for the hand
    // cursor / hover highlight and to route a click to "open artifact(s)" instead of focusing the row.
    private int HitTestArtifactIcon(Point p)
    {
        if (!ShowFullPanel) return -1;
        for (int i = 0; i < _rows.Count; i++)
            if (ArtifactIconRect(i) is { IsEmpty: false } r && r.Contains(p))
                return i;
        return -1;
    }

    // Opens a row's artifact directly when it has just one, or pops a picker to choose when it has
    // several. No-op if the row somehow has none.
    private void OpenArtifactsForRow(int rowIdx)
    {
        var artifacts = _rows[rowIdx].Session!.Artifacts;
        if (artifacts.Count == 0)
            return;

        if (artifacts.Count == 1)
        {
            OpenArtifact(artifacts[0]);
            return;
        }

        // Several artifacts: present them in the same lightweight popover the context menu uses,
        // each item opening that artifact. Anchored just under the glyph that was clicked.
        var items = artifacts
            .Select(a => new PopoverItem(a.Title, () => OpenArtifact(a)))
            .ToList();

        _popover?.Close();
        _popover = new PopoverMenu(items);
        _popover.FormClosed += (_, _) => _popover = null;
        var rect = ArtifactIconRect(rowIdx);
        _popover.ShowAt(PointToScreen(new Point(rect.Left, rect.Bottom + 2)));
    }

    private static void OpenArtifact(Artifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.Url))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(artifact.Url) { UseShellExecute = true });
    }

    // ── Context menu ─────────────────────────────────────────────────────────
    // We use our own lightweight popover (PopoverMenu) rather than a WinForms ContextMenuStrip:
    // the strip wouldn't reliably display from this transparent, top-most tool window.
    private PopoverMenu? _popover;
    private QrCodeForm? _qrForm;

    // Opens the context menu at a right-clicked point, with each item scoped to that location: Exit
    // only over the header, "Show QR code" only over a remote-controlled session row. If neither
    // applies, no menu is shown.
    private void ShowContextMenuAt(Point clientPt)
    {
        var items = new List<PopoverItem>();

        int row = HitTestRow(clientPt);
        // The "Autonomous" divider carries no session, so none of the per-session items apply to it.
        bool sessionRow = row >= 0 && !_rows[row].IsSectionHeader;

        // View history — on any session row (sub-agent rows resolve to their parent session, which
        // owns the transcript). Listed first so it's the primary per-session action.
        if (sessionRow)
        {
            var historySession = _rows[row].Session!;
            items.Add(("View history", () => HistoryRequested?.Invoke(historySession.SessionId)));
        }

        if (sessionRow)
        {
            var idSession = _rows[row].Session!;
            items.Add(("Copy session ID", () => Clipboard.SetText(idSession.SessionId)));
        }

        if (sessionRow)
        {
            var txSession = _rows[row].Session!;
            items.Add(("Open transcript in VS Code", () =>
            {
                var path = TranscriptLocator.Resolve(txSession.SessionId, txSession.Cwd);
                if (path != null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
            }));
        }

        if (sessionRow && _rows[row].Session is { RemoteControlled: true } rc)
            items.Add(("Show QR code", () => ShowQrCode(rc)));

        // Per-session external-notify toggle — only on a real session row, and only while the feature
        // is switched on globally. Sub-agent rows share the parent session, so skip them.
        if (sessionRow && !_rows[row].IsSubAgent && _externalNotifyAvailable)
        {
            var s = _rows[row].Session!;
            string label = ExternalNotifyEnabled(s)
                ? "Disable external notifications"
                : "Enable external notifications";
            items.Add((label, () => ExternalNotifyToggleRequested?.Invoke(s.SessionId)));
        }

        // Confetti finish (experimental): arm/disarm a one-shot celebration for when this session next
        // finishes. Real session rows only, and only while the feature is switched on globally.
        if (sessionRow && !_rows[row].IsSubAgent && _confettiAvailable)
        {
            var s = _rows[row].Session!;
            string label = ConfettiArmed(s) ? "Cancel confetti finish" : "Confetti finish";
            items.Add(new PopoverItem(label, () => ToggleConfetti(s.SessionId), DrawPartyIcon));
        }

        // Right-clicking a strip toggles just that strip off. Only offered when the strip is actually
        // showing (it's the thing under the cursor); the header menu below can turn either back on.
        bool overSystemStrip = ShowFullPanel && _showSystemMetrics
            && clientPt.Y >= HeaderHeight && clientPt.Y < HeaderHeight + SysMetricsStripHeight;
        bool overUsageStrip = ShowFullPanel && _usageEnabled
            && clientPt.Y >= UsageStripTop && clientPt.Y < UsageStripTop + UsageStripHeight;

        if (overSystemStrip)
            items.Add(("Hide system metrics", () => SystemMetricsToggleRequested?.Invoke(false)));
        if (overUsageStrip)
            items.Add(("Hide usage", () => UsageToggleRequested?.Invoke(false)));

        bool headerVisible = !_denseMode.IsClosedStrip;
        bool overHeader = headerVisible && clientPt.Y >= 0 && clientPt.Y < HeaderHeight;
        if (overHeader)
        {
            // Both strip toggles, regardless of their current state, so the header is the one place that
            // can bring a hidden strip back. Wording flips with the current state.
            bool sysTarget = !_showSystemMetrics;
            items.Add((_showSystemMetrics ? "Hide system metrics" : "Show system metrics",
                () => SystemMetricsToggleRequested?.Invoke(sysTarget)));
            bool usageTarget = !_usageEnabled;
            items.Add((_usageEnabled ? "Hide usage" : "Show usage",
                () => UsageToggleRequested?.Invoke(usageTarget)));
            items.Add(("Exit Perch", () => ExitRequested?.Invoke(this, EventArgs.Empty)));
        }

        if (items.Count == 0) return;

        _popover?.Close();
        _popover = new PopoverMenu(items);
        _popover.FormClosed += (_, _) => _popover = null;
        _popover.ShowAt(PointToScreen(clientPt));
    }

    // Pops a centered QR card encoding the session's remote-control deep link. Only one is shown at
    // a time; opening another (or this one losing focus) closes the previous.
    private void ShowQrCode(ClaudeSession session)
    {
        if (string.IsNullOrEmpty(session.BridgeSessionId)) return;

        _qrForm?.Close();
        var url = $"https://claude.ai/code/{session.BridgeSessionId}";
        _qrForm = new QrCodeForm(session.DisplayName, url);
        _qrForm.FormClosed += (_, _) => _qrForm = null;
        _qrForm.CenterOn(Screen.FromControl(this));
        _qrForm.Show();
        _qrForm.Activate();
    }

    // ── Hot key ────────────────────────────────────────────────────────────────
    // Alt+Shift+W toggles dense mode from anywhere via a system-wide hotkey. Registered against
    // the form's window handle; Windows posts WM_HOTKEY (handled in WndProc) when it fires.
    private const int  HotkeyId    = 0xB001;
    private const int  WM_HOTKEY   = 0x0312;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_NOREPEAT= 0x4000;  // don't auto-repeat while the keys are held
    private const uint VK_W        = 0x57;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Best-effort: if another app already owns Alt+Shift+W this fails silently and the
        // hotkey simply won't work, rather than crashing.
        NativeMethods.RegisterHotKey(Handle, HotkeyId, MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_W);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            _denseMode.Toggle();
            return;
        }
        base.WndProc(ref m);
    }

    // ── Window style: no taskbar entry, no Alt+Tab ───────────────────────────
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _denseMode.Dispose();
            _flashTimer.Dispose();
            _flashStopTimer.Dispose();
            _tickTimer.Dispose();
            _usageHoverTimer.Dispose();
            _thermoHoverTimer.Dispose();
            _warnHoverTimer.Dispose();
            _taskHoverTimer.Dispose();
            _metricsHoverTimer.Dispose();
            _autoCloseBarTimer.Dispose();
            _usageTooltip.Dispose();
            _thermoTooltip.Dispose();
            _warnTooltip.Dispose();
            _taskTooltip.Dispose();
            _metricsTooltip.Dispose();
            _popover?.Dispose();
            _qrForm?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
