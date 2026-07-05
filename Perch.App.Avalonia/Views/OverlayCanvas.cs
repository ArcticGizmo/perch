using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The owner-drawn overlay body — the Avalonia port of <c>OverlayForm</c>'s painting, replacing the
/// thin-vertical XAML <c>OverlayView</c>. A single <see cref="Draw"/> routine both measures (returns the
/// content height when given a null context) and paints (when given a real one), so the measured height
/// and painted layout can never drift — the same measure-or-paint discipline the WinForms dashboards use.
///
/// Built up over Phase 4. Done so far: rounded panel (4.1); header/collapsed bar (4.2); expanded session
/// rows (4.3); sub-agents, teammates, and the collapsible "Autonomous" section (4.4). Per-row glyphs,
/// bars, and interaction follow.
/// </summary>
public sealed class OverlayCanvas : Control
{
    // ── Layout (mirrors OverlayForm's constants) ──────────────────────────────
    private const double FormWidth        = 280;
    private const double HeaderHeight     = 44;
    private const double Corner           = 10;
    private const double HorizPad         = 12;
    private const double IconBoxW         = 16;
    private const double IconBoxH         = 16;
    private const double IconGap          = 6;
    private const double RowHeight        = 46;
    private const double SubRowHeight     = 24;
    private const double SectionRowHeight = 26;
    private const double SubIndent        = 22;
    private const double BarRowHeight     = 18;
    private const double UsageStripHeight = 50; // two usage bars + padding, shown only when expanded
    private const double SysMetricsStripHeight = 50; // system CPU + RAM bars + padding, shown only when expanded
    private const double MetricsBarWidth  = 28; // width reserved for a session row's CPU/RAM mini-bars
    private const double QuickLinksRowHeight = 24; // height of the quick-links icon strip below the usage bars
    private const double BotIconWidth     = 16;
    private const double RcIconWidth      = 14;
    private const double MailIconWidth    = 16;
    private const double ModeBadgeWidth   = 16;
    private const double WarnIconWidth    = 14;
    private const double ThermoIconWidth  = 12;
    private const double ArtifactIconWidth = 16;

    // Font sizes (px ~= the WinForms point sizes).
    private const double NameSize       = 11.5;
    private const double StatusSize     = 10;
    private const double ActivitySize   = 10;
    private const double SubNameSize    = 11;
    private const double SubStatusSize  = 9.5;
    private const double SectionLabel   = 10;
    private const double SectionChev    = 9;

    // ── Palette (the overlay's own; matches OverlayForm) ──────────────────────
    private static readonly Color  BgColor        = Color.FromRgb(15, 15, 20);
    private static readonly Color  FgColor        = Color.FromRgb(225, 225, 235);
    private static readonly IBrush BgBrush        = new SolidColorBrush(Color.FromArgb(245, 15, 15, 20));
    private static readonly IPen   BorderPen      = new Pen(new SolidColorBrush(Color.FromRgb(45, 45, 60)), 1);
    private static readonly IBrush MutedBrush     = new SolidColorBrush(Color.FromRgb(110, 110, 130));
    private static readonly IBrush FgBrush        = new SolidColorBrush(FgColor);
    private static readonly Color  RunningColor   = Color.FromRgb(34, 197, 94);
    private static readonly Color  AttentionColor = Color.FromRgb(251, 146, 60);
    private static readonly Color  AwaitingColor  = Color.FromRgb(250, 204, 21);
    private static readonly Color  IdleColor      = Color.FromRgb(100, 116, 139);
    private static readonly IBrush AttentionBrush = new SolidColorBrush(AttentionColor);
    private static readonly IBrush AwaitingBrush  = new SolidColorBrush(AwaitingColor);
    private static readonly IPen   SepPen         = new Pen(new SolidColorBrush(Color.FromRgb(35, 35, 50)), 1);
    private static readonly Color  SubAgentColor  = Color.FromRgb(168, 85, 247);
    private static readonly IBrush SubAgentBrush  = new SolidColorBrush(SubAgentColor);
    private static readonly IPen   TreeLinePen    = new Pen(new SolidColorBrush(Color.FromRgb(55, 55, 72)), 1);
    private static readonly IBrush BotBrush       = new SolidColorBrush(Color.FromRgb(148, 163, 184));
    private static readonly IBrush BadgeBrush     = new SolidColorBrush(Color.FromRgb(38, 38, 52));
    private static readonly Color  MailColor      = Color.FromRgb(94, 234, 212);
    private static readonly IBrush MailBrush      = new SolidColorBrush(MailColor);
    private static readonly Color  RemoteColor    = Color.FromRgb(96, 165, 250);
    private static readonly IBrush RemoteBrush    = new SolidColorBrush(RemoteColor);
    private static readonly Color  WarnColor      = Color.FromRgb(245, 158, 11);
    private static readonly IBrush WarnBrush      = new SolidColorBrush(WarnColor);
    private static readonly IBrush BgFillBrush    = new SolidColorBrush(BgColor);
    private static readonly IBrush BurnBrush      = new SolidColorBrush(Color.FromRgb(125, 185, 232));
    private static readonly IBrush GitAddBrush    = new SolidColorBrush(Color.FromRgb(34, 197, 94));
    private static readonly IBrush GitDelBrush    = new SolidColorBrush(Color.FromRgb(239, 68, 68));
    private static readonly IBrush RunningBrush   = new SolidColorBrush(RunningColor);
    private static readonly IBrush ArtifactBrush  = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly IBrush ArtifactHover  = new SolidColorBrush(Color.FromRgb(255, 224, 140));
    private static readonly IBrush ThermoGlassFill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IPen   ThermoOutline  = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
    private static readonly Color  MutedColor     = Color.FromRgb(110, 110, 130);
    private static readonly Color  SepColor       = Color.FromRgb(35, 35, 50);
    private static readonly IBrush RowHoverBrush  = new SolidColorBrush(Color.FromRgb(25, 25, 38));
    private static readonly Color  UsageTrackColor = Color.FromRgb(38, 38, 52);
    private static readonly Color  ExpectedMarkColor = Color.FromRgb(180, 180, 195);

    // Brand mark (the app icon), loaded once.
    private static readonly Bitmap? Brand = LoadBrand();

    // "Waiting on you" ramp length (minutes to fully red); user-tunable later (SetWaitingTimerRedMinutes).
    private int _waitingTimerRedMinutes = 3;

    // Display gates + context thresholds (wired to Settings in 4.17; defaults mirror the WinForms app).
    private bool _hideInactiveTeamMembers;
    private bool _showModeBadges = true;
    private bool _showContextPressure = true;
    private bool _showContextGreenSegment;
    private bool _showTaskProgress = true;
    private bool _showBurnRate = true;
    private bool _showGitStats = true;
    private bool _showStuckWarnings = true;
    private bool _showArtifacts = true;
    private float _ctxYellow = 0.60f, _ctxOrange = 0.75f, _ctxRed = 0.90f;

    // Rate-limit usage strip (5-hour + weekly bars), shown between the header and the rows when expanded.
    private UsageInfo _usage = UsageInfo.Empty;
    private bool _usageEnabled = true;
    private bool _showExpectedRate = true;

    /// <summary>Feeds the latest account-wide rate-limit usage (on the UI thread) and repaints the strip.
    /// Internal because <see cref="UsageInfo"/> is a Core-internal type shared via InternalsVisibleTo.</summary>
    internal void UpdateUsage(UsageInfo usage)
    {
        _usage = usage;
        if (_usageEnabled) InvalidateVisual();
    }

    /// <summary>Show/hide the whole usage strip. Toggling it changes the panel height, so relayout.</summary>
    public void SetShowUsage(bool enabled)
    {
        if (_usageEnabled == enabled) return;
        _usageEnabled = enabled;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Show/hide the expected-rate marker on each usage bar.</summary>
    public void SetShowExpectedRate(bool show)
    {
        if (_showExpectedRate == show) return;
        _showExpectedRate = show;
        InvalidateVisual();
    }

    // Resource metrics: the whole-machine strip (CPU + RAM, just under the header) and the per-row
    // mini-bars. _sysMetrics is the latest machine reading; _sessionMetrics is keyed by session pid.
    // Defaulted on for the port so a live launch shows them; 4.17 will drive these from Settings.
    private bool _showSystemMetrics = true;
    private bool _showSessionMetrics = true;
    private SystemMetrics _sysMetrics = SystemMetrics.Empty;
    private IReadOnlyDictionary<string, SessionMetrics> _sessionMetrics = new Dictionary<string, SessionMetrics>();

    /// <summary>Show/hide the whole-machine metrics strip. Changes the panel height, so relayout.</summary>
    public void SetShowSystemMetrics(bool show)
    {
        if (_showSystemMetrics == show) return;
        _showSystemMetrics = show;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Show/hide the per-row CPU/RAM mini-bars (row height is unchanged; only the glyph cluster).</summary>
    public void SetShowSessionMetrics(bool show)
    {
        if (_showSessionMetrics == show) return;
        _showSessionMetrics = show;
        InvalidateVisual();
    }

    /// <summary>Feeds the latest whole-machine CPU/RAM reading (on the UI thread) and repaints the strip.</summary>
    internal void UpdateSystemMetrics(SystemMetrics metrics)
    {
        _sysMetrics = metrics;
        if (_showSystemMetrics || _showSessionMetrics) InvalidateVisual();
    }

    /// <summary>Feeds the latest per-session CPU/RAM map (on the UI thread) and repaints the mini-bars.</summary>
    internal void UpdateSessionMetrics(IReadOnlyDictionary<string, SessionMetrics> metrics)
    {
        _sessionMetrics = metrics;
        if (_showSessionMetrics) InvalidateVisual();
    }

    // Quick-links strip: the enabled subset of the user's links, their decoded icons (null → draw
    // initials), and the hover index. Icons are resolved to PNG file paths by the app (off the platform
    // seam) and decoded here once per SetQuickLinks. The source icons render upside-down, so each is
    // flipped 180° unless the user opts into the upside-down look.
    private IReadOnlyList<QuickLink> _quickLinks = [];
    private List<Bitmap?> _quickLinkIcons = [];
    private bool _upsideDownQuickLinks;
    private int _hoveredQuickLink = -1;

    private bool HasQuickLinksRow => _quickLinks.Count > 0;
    private double QuickLinksTop => UsageStripTop + (_usageEnabled ? UsageStripHeight : 0);

    // Top of the first session row: below the header and whichever strips are showing. Mirrors the
    // painted layout so hit-testing lines up (guarded by the expanded/rows check in HitTestRow).
    private double RowsTop => QuickLinksTop + (HasQuickLinksRow ? QuickLinksRowHeight : 0);

    // The top of a given display row.
    private double RowTop(int index)
    {
        double top = RowsTop;
        for (int i = 0; i < index && i < _rows.Count; i++) top += HeightOf(_rows[i]);
        return top;
    }

    /// <summary>Raised when a quick-link icon is clicked; the app wires this to the launcher's
    /// LaunchOrFocus. Internal because <see cref="QuickLink"/> is a Core-internal type.</summary>
    internal event Action<QuickLink>? QuickLinkActivated;

    /// <summary>Replaces the quick-links strip with the given (already enabled-filtered) links and their
    /// resolved icon file paths — a null path draws name-derived initials. Called on the UI thread on
    /// startup and whenever the list is edited. Changes the panel height, so relayout.</summary>
    internal void SetQuickLinks(IReadOnlyList<QuickLink> links, IReadOnlyList<string?> iconFiles)
    {
        _quickLinks = links;
        _quickLinkIcons = new List<Bitmap?>(links.Count);
        for (int i = 0; i < links.Count; i++)
            _quickLinkIcons.Add(DecodeIcon(i < iconFiles.Count ? iconFiles[i] : null));
        _hoveredQuickLink = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Toggles the upside-down quick-link icons. Repaint only — layout is unaffected.</summary>
    public void SetUpsideDownQuickLinks(bool upsideDown)
    {
        if (_upsideDownQuickLinks == upsideDown) return;
        _upsideDownQuickLinks = upsideDown;
        InvalidateVisual();
    }

    private static Bitmap? DecodeIcon(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try { return new Bitmap(file); }
        catch { return null; }
    }

    /// <summary>Show/hide the clickable artifact glyph (the session still tracks its artifacts).</summary>
    public void SetShowArtifacts(bool show)
    {
        if (_showArtifacts == show) return;
        _showArtifacts = show;
        InvalidateVisual();
    }

    /// <summary>Show/hide the permission-mode badge on session rows.</summary>
    public void SetShowModeBadges(bool show)
    {
        if (_showModeBadges == show) return;
        _showModeBadges = show;
        InvalidateVisual();
    }

    /// <summary>When on, the context thermometer also shows below the yellow threshold (in green), so a
    /// low-but-known context is visible instead of blank.</summary>
    public void SetShowContextGreenSegment(bool show)
    {
        if (_showContextGreenSegment == show) return;
        _showContextGreenSegment = show;
        InvalidateVisual();
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool _expanded = true;
    private bool _autonomousExpanded;

    // Hover state + click routing. _hoveredRow drives the row highlight; _hoveredArtifactRow the brighter
    // artifact glyph + hand cursor. The glyph hit-rects (artifact/thermo/warn/task/metrics) are captured
    // at paint time, keyed by row index, so a hit-test tracks exactly where each glyph landed — the
    // thermo/warn/task/metrics rects feed the tooltips wired in 4.12.
    private int _hoveredRow = -1;
    private int _hoveredArtifactRow = -1;
    private readonly Dictionary<int, Rect> _artifactRects = new();
    private readonly Dictionary<int, Rect> _thermoRects = new();
    private readonly Dictionary<int, Rect> _warnRects = new();
    private readonly Dictionary<int, Rect> _taskRects = new();
    private readonly Dictionary<int, Rect> _metricsRects = new();

    /// <summary>Raised when a session row is clicked (sub-agent rows resolve to their parent). The app
    /// focuses the session's terminal via the platform seam. Internal — <see cref="ClaudeSession"/> is
    /// a Core-internal type.</summary>
    internal event Action<ClaudeSession>? SessionActivated;

    /// <summary>Raised when a row's artifact glyph is clicked; the app opens the artifact(s).</summary>
    internal event Action<ClaudeSession>? ArtifactActivated;

    // ── Right-click context menu (4.13) ───────────────────────────────────────
    // External (ntfy) notifications + the experimental confetti-finish are gated by global settings that
    // decide whether their per-session items appear at all. Confetti arming is in-memory only (never
    // persisted, so a celebration can't fire by surprise after a restart) and is spent the moment the
    // session next finishes (ConsumeConfetti). The QR / history windows themselves are Phase 5; the menu
    // wires only their triggers.
    private bool _externalNotifyAvailable;
    private bool _confettiAvailable;
    private readonly HashSet<string> _confettiSessions = new();

    /// <summary>Raised when the user picks "Exit Perch" from the header's right-click menu.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when the user picks "View history" for a session; carries the session id so the
    /// app can open the history viewer on it.</summary>
    public event Action<string>? HistoryRequested;

    /// <summary>Raised when the user toggles a session's external-notify opt-in; carries the session id
    /// for the app to flip its marker file.</summary>
    public event Action<string>? ExternalNotifyToggleRequested;

    /// <summary>Raised when the user toggles the whole-machine metrics strip from the right-click menu;
    /// carries the desired new enabled state for the app to persist and apply.</summary>
    public event Action<bool>? SystemMetricsToggleRequested;

    /// <summary>Raised when the user toggles the account-usage strip from the right-click menu; carries
    /// the desired new enabled state for the app to persist and apply.</summary>
    public event Action<bool>? UsageToggleRequested;

    /// <summary>Raised when the user picks "Show QR code" for a remote-controlled session. The QR window
    /// is Phase 5; this wires only the trigger. Internal — <see cref="ClaudeSession"/> is Core-internal.</summary>
    internal event Action<ClaudeSession>? QrRequested;

    /// <summary>Whether external (ntfy) notifications are switched on globally — gates the right-click
    /// enable/disable item. (The per-row mail glyph reads the session's own state.)</summary>
    public void SetExternalNotificationsAvailable(bool available)
    {
        if (_externalNotifyAvailable == available) return;
        _externalNotifyAvailable = available;
        InvalidateVisual();
    }

    /// <summary>Whether the experimental confetti-finish is switched on globally — gates the right-click
    /// arm/disarm item. Turning it off clears every armed session so nothing stays primed.</summary>
    public void SetConfettiFinishAvailable(bool available)
    {
        if (_confettiAvailable == available) return;
        _confettiAvailable = available;
        if (!available) _confettiSessions.Clear();
        InvalidateVisual();
    }

    /// <summary>If the session was armed for a confetti finish, disarm it and report true so the app can
    /// set off the celebration. Arming is one-shot: a finish spends it. Returns false otherwise.</summary>
    public bool ConsumeConfetti(string sessionId)
    {
        if (!_confettiAvailable || !_confettiSessions.Remove(sessionId)) return false;
        InvalidateVisual();
        return true;
    }

    private bool ConfettiArmed(ClaudeSession s) => _confettiAvailable && _confettiSessions.Contains(s.SessionId);

    // Flips a session's confetti arming from the right-click menu (in-memory only).
    private void ToggleConfetti(string sessionId)
    {
        if (!_confettiSessions.Remove(sessionId)) _confettiSessions.Add(sessionId);
        InvalidateVisual();
    }

    // Dwell tooltips: hovering an info glyph (thermometer / stuck-warning / task-count / metrics bars)
    // or the usage strip for ~150ms pops a hint. A single timer serves whichever the cursor last
    // settled on; moving to a different (or no) target restarts it and hides the current tip.
    private enum TipKind { None, Usage, Thermo, Warn, Task, Metrics }
    private TipKind _tipKind = TipKind.None;
    private int _tipRow = -1;
    private DispatcherTimer? _dwellTimer;
    private OverlayTooltip? _tooltip;

    // A flat render row: a parent session, one of its sub-agents, or the "Autonomous" section header.
    private readonly record struct DisplayRow(ClaudeSession? Session, SubAgent? Sub, int SectionCount = -1)
    {
        public bool IsSubAgent => Sub != null;
        public bool IsSectionHeader => SectionCount >= 0;
    }

    /// <summary>When on, idle teammates are dropped from the roster (only working ones show). Wired to
    /// the Settings toggle in 4.17; a hidden teammate reappears the moment it starts working again.</summary>
    public void SetHideInactiveTeamMembers(bool hide)
    {
        if (_hideInactiveTeamMembers == hide) return;
        _hideInactiveTeamMembers = hide;
        Update(_sessions); // rebuild the render list under the new gate
    }

    /// <summary>Feeds the latest session list (on the UI thread) and rebuilds the render list.</summary>
    public void Update(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;

        // Interactive sessions render at the top; background/SDK-driven ones group under the
        // collapsible "Autonomous" section. Each partition sorted by display name.
        var interactive = sessions.Where(s => !s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase);
        var background = sessions.Where(s => s.IsBackground)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        var rows = new List<DisplayRow>();
        foreach (var s in interactive) AddSessionRows(rows, s);

        if (background.Count > 0)
        {
            rows.Add(new DisplayRow(null, null, background.Count));
            if (_autonomousExpanded)
                foreach (var s in background) AddSessionRows(rows, s);
        }
        else _autonomousExpanded = false;

        _rows = rows;
        if (sessions.Count == 0) _expanded = false;

        InvalidateMeasure();
        InvalidateVisual();
    }

    // A session's row plus its running sub-agent / teammate child rows, in draw order.
    private void AddSessionRows(List<DisplayRow> rows, ClaudeSession session)
    {
        rows.Add(new DisplayRow(session, null));
        var ordered = session.SubAgents
            .Where(s => !_hideInactiveTeamMembers || !(s.IsTeammate && s.IsIdle))
            .OrderByDescending(s => s.IsTeammate)
            .ThenBy(s => s.IsTeammate && s.IsIdle)
            .ThenBy(s => s.Name ?? s.Description, StringComparer.OrdinalIgnoreCase);
        foreach (var sub in ordered) rows.Add(new DisplayRow(session, sub));
    }

    private static double HeightOf(DisplayRow row) =>
        row.IsSectionHeader ? SectionRowHeight : row.IsSubAgent ? SubRowHeight : RowHeight;

    protected override Size MeasureOverride(Size availableSize)
        => new(FormWidth, Draw(null, FormWidth));

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Width);

    // Measure-or-paint: returns the content height; paints only when ctx is non-null.
    private double Draw(DrawingContext? ctx, double width)
    {
        bool showRows = _expanded && _rows.Count > 0;
        bool showSys = showRows && _showSystemMetrics;        // machine CPU/RAM strip, just under the header
        bool showUsage = showRows && _usageEnabled;           // rate-limit bars, below the metrics strip
        bool showQuickLinks = showRows && HasQuickLinksRow;   // app icon strip, below the usage bars

        double height = HeaderHeight;
        if (showRows)
        {
            if (showSys) height += SysMetricsStripHeight;
            if (showUsage) height += UsageStripHeight;
            if (showQuickLinks) height += QuickLinksRowHeight;
            foreach (var r in _rows) height += HeightOf(r);
            height += 2;
        }

        if (ctx != null)
        {
            OverlayDraw.Panel(ctx, new Rect(0.5, 0.5, width - 1, height - 1), BgBrush, BorderPen, Corner);
            DrawHeader(ctx, width);

            if (showSys) DrawSystemMetricsStrip(ctx, width);
            if (showUsage) DrawUsageBars(ctx, width);
            if (showQuickLinks) DrawQuickLinksRow(ctx, width);

            if (showRows)
            {
                // Glyph hit-rects are rebuilt from scratch each paint; DrawSessionRow repopulates them
                // for any row that actually shows the glyph.
                _artifactRects.Clear();
                _thermoRects.Clear();
                _warnRects.Clear();
                _taskRects.Clear();
                _metricsRects.Clear();

                double top = RowsTop;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    if (r.IsSectionHeader) DrawSectionHeaderRow(ctx, r, top, width);
                    else if (r.IsSubAgent) DrawSubAgentRow(ctx, r.Sub!, top, width);
                    else DrawSessionRow(ctx, i, r.Session!, top, width);
                    top += HeightOf(r);
                }
            }
        }

        return height;
    }

    private void DrawHeader(DrawingContext ctx, double width)
    {
        double midY = HeaderHeight / 2;

        double brandRight = HorizPad;
        if (Brand is { })
        {
            const double iconSize = 18;
            ctx.DrawImage(Brand, new Rect(HorizPad, midY - iconSize / 2, iconSize, iconSize));
            brandRight = HorizPad + iconSize + 5;
        }

        var label = OverlayDraw.Text("Perch", 11, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, label, brandRight, midY);
        brandRight += label.Width;

        double sepX = brandRight + 4;
        ctx.DrawEllipse(MutedBrush, null, new Point(sepX + 2, midY), 2, 2);
        double x = sepX + 10;

        if (_sessions.Count == 0)
        {
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text("no sessions", 11, MutedBrush), x, midY);
        }
        else
        {
            int running   = _sessions.Count(s => s.Status == SessionStatus.Running);
            int attention = _sessions.Count(s => s.Status == SessionStatus.NeedsAttention);
            int awaiting  = _sessions.Count(s => s.Status == SessionStatus.AwaitingInput);
            int idle      = _sessions.Count(s => s.Status == SessionStatus.Idle);

            x = DrawStatusPill(ctx, x, midY, awaiting,  AwaitingColor,  AwaitingColor);
            x = DrawStatusPill(ctx, x, midY, running,   RunningColor,   FgColor);
            x = DrawStatusPill(ctx, x, midY, attention, AttentionColor, AttentionColor);
            if (running == 0 && attention == 0 && awaiting == 0)
                DrawStatusPill(ctx, x, midY, idle, IdleColor, IdleColor);
        }

        // Right cluster: dense toggle (drawn; click wiring 4.12) + expand chevron. Update badge is 4.14.
        DrawSideCollapseIcon(ctx, SideIconRect(width), reversed: false);
        double clusterLeft = width - HorizPad - IconBoxW;

        if (_sessions.Count > 0)
        {
            var chevron = OverlayDraw.Text(_expanded ? "▲" : "▼", 9, MutedBrush);
            double chevX = clusterLeft - IconGap - chevron.Width;
            OverlayDraw.TextLeftMid(ctx, chevron, chevX, midY);
        }
    }

    private Rect SideIconRect(double width)
    {
        double top = (HeaderHeight - IconBoxH) / 2;
        return new Rect(width - HorizPad - IconBoxW, top, IconBoxW, IconBoxH);
    }

    private static double DrawStatusPill(DrawingContext ctx, double x, double midY, int count,
                                         Color dotColor, Color textColor)
    {
        if (count == 0) return x;
        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(x + 4, midY), 4, 4);
        x += 12;
        var label = OverlayDraw.Text(count.ToString(), 12, new SolidColorBrush(textColor), FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        return x + label.Width + 8;
    }

    private static void DrawSideCollapseIcon(DrawingContext ctx, Rect r, bool reversed)
    {
        var pen = new Pen(MutedBrush, 1.6, lineCap: PenLineCap.Round);
        double midY = r.Top + r.Height / 2;
        double pad = 3, left = r.Left + pad, right = r.Right - pad, headLen = 4;

        if (!reversed)
        {
            double pipeX = right, shaftEnd = pipeX - 2;
            ctx.DrawLine(pen, new Point(left, midY), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY - headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd - headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));
        }
        else
        {
            double pipeX = left, shaftEnd = pipeX + 2;
            ctx.DrawLine(pen, new Point(right, midY), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY - headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(shaftEnd + headLen, midY + headLen), new Point(shaftEnd, midY));
            ctx.DrawLine(pen, new Point(pipeX, r.Top + pad), new Point(pipeX, r.Bottom - pad));
        }
    }

    // ── System metrics strip ─────────────────────────────────────────────────
    // Two bars just under the header: whole-machine CPU and physical-RAM load, drawn with the same
    // shared renderer as the usage bars so the two strips read alike. The percentage doubles as the
    // fill and is coloured by load (green → red). Shows em-dashes until the first real sample lands
    // (CPU needs two samples to produce a reading).
    private void DrawSystemMetricsStrip(DrawingContext ctx, double width)
    {
        bool has = _sysMetrics.HasData;
        double top = HeaderHeight + 2;
        DrawSysBar(ctx, width, top,                "CPU", has ? _sysMetrics.CpuPercent : null);
        DrawSysBar(ctx, width, top + BarRowHeight, "RAM", has ? _sysMetrics.RamPercent : null);

        // A thin grey rule separating the system strip from the usage strip below — only when the usage
        // strip is there to divide from. Floated a few px above the boundary so the clearance reads even.
        if (_usageEnabled)
        {
            double sepY = UsageStripTop - 4;
            ctx.DrawLine(new Pen(new SolidColorBrush(SepColor), 1),
                new Point(HorizPad, sepY), new Point(width - HorizPad, sepY));
        }
    }

    private void DrawSysBar(DrawingContext ctx, double width, double rowTop, string caption, double? percent) =>
        UsageBarRenderer.Draw(ctx, HorizPad, width - HorizPad, rowTop + BarRowHeight / 2,
            caption, percent, expectedPct: null, stale: false, 10, 10,
            MutedColor, UsageTrackColor, ExpectedMarkColor, BgColor,
            captionW: 46, pctW: 34, trackH: 7);

    // ── Usage bars ─────────────────────────────────────────────────────────────
    // Two bars below the header (or the metrics strip, when shown) when expanded: the 5-hour ("Session")
    // and 7-day ("Weekly") rate-limit windows. Dimmed when the reading is stale/unavailable.
    private double UsageStripTop => HeaderHeight + (_showSystemMetrics ? SysMetricsStripHeight : 0);

    private void DrawUsageBars(DrawingContext ctx, double width)
    {
        bool stale = _usage.IsStale(DateTime.Now);
        double top = UsageStripTop + 2;
        double? sessionExpected = _showExpectedRate
            ? UsageBarRenderer.ElapsedPercent(_usage.FiveHourResetsAt, TimeSpan.FromHours(5)) : null;
        double? weeklyExpected = _showExpectedRate
            ? UsageBarRenderer.ElapsedPercent(_usage.SevenDayResetsAt, TimeSpan.FromDays(7)) : null;
        DrawUsageBar(ctx, width, top,                 "Session", _usage.FiveHourPercent, sessionExpected, stale);
        DrawUsageBar(ctx, width, top + BarRowHeight,  "Weekly",  _usage.SevenDayPercent, weeklyExpected,  stale);
    }

    // The overlay's compact bar: a HorizPad inset on both sides, narrow caption/pct columns, the
    // overlay's own muted/track/bg shades. Caption/pct at 10px (the WinForms 7.5pt at 96→72).
    private void DrawUsageBar(DrawingContext ctx, double width, double rowTop, string caption,
                              double? percent, double? expectedPct, bool stale) =>
        UsageBarRenderer.Draw(ctx, HorizPad, width - HorizPad, rowTop + BarRowHeight / 2,
            caption, percent, expectedPct, stale, 10, 10,
            MutedColor, UsageTrackColor, ExpectedMarkColor, BgColor,
            captionW: 46, pctW: 34, trackH: 7);

    // ── Quick-links row ────────────────────────────────────────────────────────
    // The enabled quick-link icons side-by-side, centred horizontally. Each slot shows its pre-decoded
    // icon, or drawn initials over a name-derived colour when no icon resolved. The source icons render
    // upside-down, so each is flipped 180° about its own centre unless the user opts into upside-down.
    private void DrawQuickLinksRow(DrawingContext ctx, double width)
    {
        const double IconSize = 16, IconGap = 14, HitPad = 4;
        double rowTop = QuickLinksTop;
        double centerY = rowTop + QuickLinksRowHeight / 2;

        int count = _quickLinks.Count;
        double totalW = count * IconSize + (count - 1) * IconGap;
        double startX = (width - totalW) / 2;

        for (int i = 0; i < count; i++)
        {
            double iconX = startX + i * (IconSize + IconGap);
            double iconY = centerY - IconSize / 2;

            if (_hoveredQuickLink == i)
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                    new Rect(iconX - HitPad, iconY - HitPad, IconSize + HitPad * 2, IconSize + HitPad * 2));

            var icon = i < _quickLinkIcons.Count ? _quickLinkIcons[i] : null;
            var iconRect = new Rect(iconX, iconY, IconSize, IconSize);

            if (icon != null)
            {
                if (_upsideDownQuickLinks)
                {
                    ctx.DrawImage(icon, iconRect);
                }
                else
                {
                    double cx = iconX + IconSize / 2, cy = iconY + IconSize / 2;
                    var flip = Matrix.CreateTranslation(-cx, -cy)
                             * Matrix.CreateRotation(Math.PI)
                             * Matrix.CreateTranslation(cx, cy);
                    using (ctx.PushTransform(flip))
                        ctx.DrawImage(icon, iconRect);
                }
            }
            else
            {
                var initials = Initials(_quickLinks[i].Name);
                var ft = OverlayDraw.Text(initials, 9.5, new SolidColorBrush(FallbackColor(_quickLinks[i].Name)),
                    FontWeight.Bold);
                ctx.DrawText(ft, new Point(iconX + (IconSize - ft.Width) / 2, iconY + (IconSize - ft.Height) / 2));
            }
        }
    }

    // Returns the index into _quickLinks under point p, or -1 if none (or the strip isn't shown).
    private int HitTestQuickLink(Point p)
    {
        if (!(_expanded && _rows.Count > 0 && HasQuickLinksRow)) return -1;
        double rowTop = QuickLinksTop;
        if (p.Y < rowTop || p.Y >= rowTop + QuickLinksRowHeight) return -1;

        const double IconSize = 16, IconGap = 14, HitPad = 4;
        int count = _quickLinks.Count;
        double totalW = count * IconSize + (count - 1) * IconGap;
        double startX = (Bounds.Width - totalW) / 2;

        for (int i = 0; i < count; i++)
        {
            double iconX = startX + i * (IconSize + IconGap);
            if (p.X >= iconX - HitPad && p.X < iconX + IconSize + HitPad) return i;
        }
        return -1;
    }

    // Returns the display-row index under p, or -1 (only while the panel is expanded with rows).
    private int HitTestRow(Point p)
    {
        if (!(_expanded && _rows.Count > 0) || p.Y < RowsTop) return -1;
        double y = RowsTop;
        for (int i = 0; i < _rows.Count; i++)
        {
            double h = HeightOf(_rows[i]);
            if (p.Y >= y && p.Y < y + h) return i;
            y += h;
        }
        return -1;
    }

    // The glyph hit-tests read the rects captured at paint time, so they track exactly where each glyph
    // was drawn. Artifact routes clicks + the hand cursor; the rest feed the 4.12 tooltips.
    private int HitTestArtifactIcon(Point p) => HitRect(_artifactRects, p);
    private int HitTestThermoIcon(Point p)   => HitRect(_thermoRects, p);
    private int HitTestWarnIcon(Point p)     => HitRect(_warnRects, p);
    private int HitTestTaskCount(Point p)    => HitRect(_taskRects, p);
    private int HitTestMetrics(Point p)      => HitRect(_metricsRects, p);

    private static int HitRect(Dictionary<int, Rect> rects, Point p)
    {
        foreach (var (row, rect) in rects)
            if (rect.Contains(p)) return row;
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

    // A stable, reasonably saturated colour derived from the name, so two icon-less links are visually
    // distinguishable without any per-link configuration.
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
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ── Session row (core) ────────────────────────────────────────────────────
    private void DrawSessionRow(DrawingContext ctx, int rowIndex, ClaudeSession session, double top, double width)
    {
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        if (rowIndex == _hoveredRow)
            ctx.FillRectangle(RowHoverBrush, new Rect(1, top + 1, width - 2, RowHeight - 1));

        double midY = top + RowHeight / 2;
        bool running  = session.Status == SessionStatus.Running;
        bool awaiting = session.Status == SessionStatus.AwaitingInput;

        // While working a checklist, the active task's gerund is more telling than the raw tool phrase.
        string? activity = running
            ? (session.CurrentTask?.ActiveForm is { Length: > 0 } af ? af : session.Activity)
            : awaiting ? "waiting on you" : null;
        string? elapsed  = running ? session.RunningElapsedLabel()
                         : awaiting ? session.AwaitingElapsedLabel() : null;
        bool twoLine = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        double nameMidY = twoLine ? top + RowHeight / 2 - 8 : midY;

        IBrush secondLine = awaiting
            ? new SolidColorBrush(WarmWaitingColor(session.AwaitingElapsed() ?? TimeSpan.Zero))
            : MutedBrush;

        var dotColor = session.Status switch
        {
            SessionStatus.Running        => RunningColor,
            SessionStatus.NeedsAttention => AttentionColor,
            SessionStatus.AwaitingInput  => AwaitingColor,
            _                            => IdleColor,
        };
        ctx.DrawEllipse(new SolidColorBrush(dotColor), null, new Point(HorizPad + 4, nameMidY), 4, 4);

        string statusText = session.Status switch
        {
            SessionStatus.Running        => "running",
            SessionStatus.NeedsAttention => "done ↩",
            SessionStatus.AwaitingInput  => "input ↩",
            _                            => "idle",
        };
        IBrush statusBrush = session.Status switch
        {
            SessionStatus.NeedsAttention => AttentionBrush,
            SessionStatus.AwaitingInput  => AwaitingBrush,
            _                            => MutedBrush,
        };

        double statusW = OverlayDraw.MeasureWidth(statusText, StatusSize);

        // Left-of-name glyph cluster. (artifact/party slots stay 0-width until 4.7/confetti; metrics
        // until 4.9.)
        bool stuck = _showStuckWarnings && session.IsStuck;
        double warnW = stuck ? WarnIconWidth : 0;
        bool hasArtifacts = _showArtifacts && session.HasArtifacts;
        double artW = hasArtifacts ? ArtifactIconWidth : 0;
        const double partyW = 0;
        double mailW = session.ExternalNotify ? MailIconWidth : 0;
        double rcW   = session.RemoteControlled ? RcIconWidth : 0;
        double botW  = session.IsBackground ? BotIconWidth : 0;

        // Right-side cluster (right→left from the status text): thermometer, mode badge, task count,
        // metrics bars (4.9), burn rate.
        bool showBadge = session.Mode != PermissionMode.Normal && _showModeBadges;
        double badgeW = showBadge ? ModeBadgeWidth : 0;

        float ctxFill = session.ContextFill ?? 0f;
        bool showThermo = _showContextPressure
            && (ctxFill >= _ctxYellow || (_showContextGreenSegment && session.ContextFill.HasValue));
        double thermoW = showThermo ? ThermoIconWidth + 2 : 0;

        bool hasTasks = _showTaskProgress && session.HasTasks;
        string taskLabel = hasTasks ? $"{session.CompletedTaskCount}/{session.Tasks.Count}" : "";
        double taskW = hasTasks ? OverlayDraw.MeasureWidth(taskLabel, StatusSize) + 8 : 0;

        _sessionMetrics.TryGetValue(session.Pid, out var sessMetrics);
        bool showMetrics = _showSessionMetrics && sessMetrics.ProcessCount > 0;
        double metricsW = showMetrics ? MetricsBarWidth : 0;

        bool showBurn = _showBurnRate && running && session.BurnRate is > 0;
        string burnLabel = showBurn ? StatsFormat.Tokens((long)session.BurnRate!.Value) + "/m" : "";
        double burnW = showBurn ? OverlayDraw.MeasureWidth(burnLabel, StatusSize) + 8 : 0;

        var gitStats = _showGitStats ? session.GitStats : null;
        bool showGit = gitStats is { IsEmpty: false };
        string gitAdd = showGit ? $"+{gitStats!.Value.Added}" : "";
        string gitDel = showGit ? $"-{gitStats!.Value.Deleted}" : "";
        double gitAddW = showGit ? OverlayDraw.MeasureWidth(gitAdd, StatusSize) : 0;
        double gitDelW = showGit ? OverlayDraw.MeasureWidth(gitDel, StatusSize) : 0;
        const double GitGap = 4;
        double gitW = showGit ? gitAddW + GitGap + gitDelW + 8 : 0;

        double nameMax = width - HorizPad * 3 - 8 - statusW - badgeW - rcW - botW - partyW - mailW
                         - artW - warnW - thermoW - taskW - metricsW - burnW - gitW;
        string nameTrunc = OverlayDraw.Truncate(session.DisplayName, NameSize, nameMax);
        double nameW = OverlayDraw.MeasureWidth(nameTrunc, NameSize);

        // Left glyphs, in draw order. Warn + artifact capture generous hit-rects for hover/tooltip.
        if (stuck)
        {
            DrawWarnIcon(ctx, HorizPad + 14, nameMidY);
            _warnRects[rowIndex] = new Rect(HorizPad + 12, nameMidY - 9, WarnIconWidth + 2, 18);
        }
        if (hasArtifacts)
        {
            DrawArtifactIcon(ctx, HorizPad + 14 + warnW, nameMidY, hovered: _hoveredArtifactRow == rowIndex);
            _artifactRects[rowIndex] = new Rect(HorizPad + 12 + warnW, nameMidY - 9, ArtifactIconWidth + 2, 18);
        }
        if (mailW > 0)    DrawMailIcon(ctx, HorizPad + 14 + warnW + artW, nameMidY);
        if (rcW > 0)   DrawRemoteIcon(ctx, HorizPad + 16 + warnW + artW + mailW, nameMidY);
        if (botW > 0)  DrawBotIcon(ctx, HorizPad + 14 + warnW + artW + mailW + rcW + partyW, nameMidY);

        double nameX = HorizPad + 14 + warnW + artW + mailW + rcW + partyW + botW;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(nameTrunc, NameSize, FgBrush), nameX, nameMidY);

        // Git churn immediately right of the name: "+added" green, "-deleted" red.
        if (showGit)
        {
            double gitX = nameX + nameW + 6;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(gitAdd, StatusSize, GitAddBrush), gitX, nameMidY);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(gitDel, StatusSize, GitDelBrush),
                gitX + gitAddW + GitGap, nameMidY);
        }

        double statusX = width - HorizPad - statusW;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(statusText, StatusSize, statusBrush), statusX, nameMidY);

        if (showThermo)
        {
            DrawThermoIcon(ctx, ctxFill, statusX - thermoW, nameMidY);
            _thermoRects[rowIndex] = new Rect(statusX - thermoW, nameMidY - 9, thermoW, 18);
        }
        if (showBadge)
        {
            int alpha = session.Status == SessionStatus.Idle ? 110 : 255;
            DrawModeBadge(ctx, session.Mode, statusX - thermoW - badgeW, nameMidY, alpha);
        }
        if (hasTasks)
        {
            double taskX = statusX - thermoW - badgeW - taskW;
            bool allDone = session.CompletedTaskCount == session.Tasks.Count;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(taskLabel, StatusSize, allDone ? RunningBrush : MutedBrush),
                taskX, nameMidY);
            _taskRects[rowIndex] = new Rect(taskX, nameMidY - 9, taskW, 18);
        }
        if (showMetrics)
        {
            double metricsX = statusX - thermoW - badgeW - taskW - metricsW;
            DrawMetricsBars(ctx, sessMetrics, metricsX, nameMidY);
            _metricsRects[rowIndex] = new Rect(metricsX, nameMidY - 9, metricsW, 18);
        }
        if (showBurn)
        {
            double burnX = statusX - thermoW - badgeW - taskW - metricsW - burnW;
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(burnLabel, StatusSize, BurnBrush), burnX, nameMidY);
        }

        if (twoLine)
        {
            double activityMidY = top + RowHeight / 2 + 9;
            double lineLeft = HorizPad + 14;

            double elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                elapsedW = OverlayDraw.MeasureWidth(elapsed, ActivitySize);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(elapsed, ActivitySize, secondLine),
                    width - HorizPad - elapsedW, activityMidY);
            }
            if (!string.IsNullOrEmpty(activity))
            {
                double activityMax = width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                string actTrunc = OverlayDraw.Truncate(activity, ActivitySize, activityMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, ActivitySize, secondLine),
                    lineLeft, activityMidY);
            }
        }
    }

    private Color WarmWaitingColor(TimeSpan waited)
    {
        double fullMinutes = Math.Max(1, _waitingTimerRedMinutes);
        double t = Math.Clamp(waited.TotalMinutes / fullMinutes, 0.0, 1.0);
        var to = Color.FromRgb(239, 68, 68);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromRgb(Lerp(AwaitingColor.R, to.R), Lerp(AwaitingColor.G, to.G), Lerp(AwaitingColor.B, to.B));
    }

    // ── "Autonomous" section header ───────────────────────────────────────────
    private void DrawSectionHeaderRow(DrawingContext ctx, DisplayRow row, double top, double width)
    {
        double midY = top + SectionRowHeight / 2;

        var chevron = OverlayDraw.Text(_autonomousExpanded ? "▾" : "▸", SectionChev, MutedBrush);
        double x = HorizPad;
        OverlayDraw.TextLeftMid(ctx, chevron, x, midY);
        x += chevron.Width + 4;

        DrawBotIcon(ctx, x, midY);
        x += BotIconWidth;

        var label = OverlayDraw.Text("Autonomous", SectionLabel, MutedBrush);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        x += label.Width + 6;

        // Count badge (dim pill).
        var countFt = OverlayDraw.Text(row.SectionCount.ToString(), SectionLabel, MutedBrush);
        double badgeW = countFt.Width + 10;
        double badgeH = label.Height + 2;
        OverlayDraw.Panel(ctx, new Rect(x, midY - badgeH / 2, badgeW, badgeH), BadgeBrush, null, badgeH / 2);
        OverlayDraw.TextLeftMid(ctx, countFt, x + 5, midY);
        x += badgeW + 8;

        if (x < width - HorizPad)
            ctx.DrawLine(SepPen, new Point(x, midY), new Point(width - HorizPad, midY));
    }

    // ── Sub-agent / teammate rows ─────────────────────────────────────────────
    private void DrawSubAgentRow(DrawingContext ctx, SubAgent sub, double top, double width)
    {
        double midY = top + SubRowHeight / 2;

        // Tree connector: a stub dropping from the parent row down to this child's marker.
        double branchX = HorizPad + 4;
        double markerX = HorizPad + SubIndent;
        ctx.DrawLine(TreeLinePen, new Point(branchX, top - SubRowHeight / 2), new Point(branchX, midY));
        ctx.DrawLine(TreeLinePen, new Point(branchX, midY), new Point(markerX - 2, midY));

        if (sub.IsTeammate) DrawTeammateRow(ctx, sub, markerX, midY, width);
        else DrawPlainSubAgentRow(ctx, sub, markerX, midY, width);
    }

    private void DrawPlainSubAgentRow(DrawingContext ctx, SubAgent sub, double dotX, double midY, double width)
    {
        ctx.DrawEllipse(SubAgentBrush, null, new Point(dotX + 3, midY), 3, 3);

        const string statusText = "running";
        double statusW = OverlayDraw.MeasureWidth(statusText, SubStatusSize);
        double labelX = dotX + 12;
        double labelMaxW = width - labelX - HorizPad - statusW - 6;

        string type = sub.AgentType?.Trim() ?? "";
        string desc = sub.Description?.Trim() ?? "";
        if (string.Equals(desc, type, StringComparison.Ordinal)) desc = "";
        if (type.Length == 0 && desc.Length == 0) desc = "sub-agent";

        double x = labelX;
        if (type.Length > 0)
        {
            string typeTrunc = OverlayDraw.Truncate(type, SubNameSize, labelMaxW / 2);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(typeTrunc, SubNameSize, MutedBrush), x, midY);
            x += OverlayDraw.MeasureWidth(typeTrunc, SubNameSize) + 8;
        }
        if (desc.Length > 0)
        {
            string descTrunc = OverlayDraw.Truncate(desc, SubNameSize, labelMaxW - (x - labelX));
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(descTrunc, SubNameSize, FgBrush), x, midY);
        }

        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(statusText, SubStatusSize, SubAgentBrush),
            width - HorizPad - statusW, midY);
    }

    private void DrawTeammateRow(DrawingContext ctx, SubAgent sub, double glyphX, double midY, double width)
    {
        bool idle = sub.IsIdle;
        Color teamColor = Palette.TeamColor(sub.Color);
        Color nameColor = idle ? Palette.Blend(teamColor, BgColor, 0.55f) : teamColor;
        Color textColor = idle ? Palette.Blend(FgColor, BgColor, 0.55f) : FgColor;
        var nameBrush = new SolidColorBrush(nameColor);
        var textBrush = new SolidColorBrush(textColor);

        DrawTeammateGlyph(ctx, glyphX, midY, nameColor);

        string stateText = idle ? "idle" : "working";
        double stateW = OverlayDraw.MeasureWidth(stateText, SubStatusSize);
        double labelX = glyphX + 16;
        double labelMaxW = width - labelX - HorizPad - stateW - 6;

        string handle = "@" + (string.IsNullOrWhiteSpace(sub.Name) ? "teammate" : sub.Name!.Trim());
        string handleTrunc = OverlayDraw.Truncate(handle, SubNameSize, labelMaxW);
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(handleTrunc, SubNameSize, nameBrush), labelX, midY);
        double x = labelX + OverlayDraw.MeasureWidth(handleTrunc, SubNameSize) + 8;

        string activity = idle ? "" : sub.Activity?.Trim() ?? "";
        if (activity.Length > 0)
        {
            double remaining = labelMaxW - (x - labelX);
            if (remaining > 24)
            {
                string actTrunc = OverlayDraw.Truncate(activity, SubNameSize, remaining);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, SubNameSize, textBrush), x, midY);
            }
        }

        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(stateText, SubStatusSize, idle ? textBrush : nameBrush),
            width - HorizPad - stateW, midY);
    }

    // A small "person" mark — a head circle above a shoulders dome — in the given colour, centred on (x, midY).
    private static void DrawTeammateGlyph(DrawingContext ctx, double x, double midY, Color color)
    {
        var brush = new SolidColorBrush(color);
        const double headD = 5;
        ctx.DrawEllipse(brush, null, new Point(x + headD / 2, midY - 5 + headD / 2), headD / 2, headD / 2);

        // Shoulders: the top half of a small ellipse below the head.
        var r = new Rect(x - 1, midY + 1, headD + 3, headD + 2);
        var dome = new StreamGeometry();
        using (var gc = dome.Open())
        {
            var leftPt = new Point(r.Left, r.Center.Y);
            var rightPt = new Point(r.Right, r.Center.Y);
            gc.BeginFigure(leftPt, isFilled: true);
            gc.ArcTo(rightPt, new Size(r.Width / 2, r.Height / 2), 0, false, SweepDirection.CounterClockwise);
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, dome);
    }

    // The background-session robot glyph: antenna + rounded-square face + two dot eyes.
    private static void DrawBotIcon(DrawingContext ctx, double x, double midY)
    {
        var pen = new Pen(BotBrush, 1.3, lineCap: PenLineCap.Round);
        const double w = 11, h = 9;
        double left = x, top = midY - h / 2 + 1;
        double cx = left + w / 2;

        ctx.DrawLine(pen, new Point(cx, top - 3), new Point(cx, top));          // antenna
        ctx.DrawEllipse(BotBrush, null, new Point(cx, top - 3.5), 1.5, 1.5);    // antenna cap
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(left, top, w, h), 2)); // face
        ctx.DrawEllipse(BotBrush, null, new Point(left + 3.5, top + 4), 1, 1);  // eyes
        ctx.DrawEllipse(BotBrush, null, new Point(left + w - 3.5, top + 4), 1, 1);
    }

    // The remote-control "broadcast" glyph: a source dot with two quarter-arc waves rising up-right.
    private static void DrawRemoteIcon(DrawingContext ctx, double originX, double midY)
    {
        var pen = new Pen(RemoteBrush, 2.25, lineCap: PenLineCap.Round);
        double oy = midY + 4;
        ctx.DrawEllipse(RemoteBrush, null, new Point(originX, oy), 2, 2);
        OverlayDraw.Arc(ctx, pen, originX, oy, 5, 270, 90);
        OverlayDraw.Arc(ctx, pen, originX, oy, 9, 270, 90);
    }

    // The external-notify glyph: an envelope outline with a "V" flap.
    private static void DrawMailIcon(DrawingContext ctx, double x, double midY)
    {
        var pen = new Pen(MailBrush, 1.3, null, PenLineCap.Round, PenLineJoin.Round);
        const double w = 11, h = 8;
        double top = midY - h / 2;
        ctx.DrawRectangle(null, pen, new Rect(x, top, w, h));
        var flap = new StreamGeometry();
        using (var gc = flap.Open())
        {
            gc.BeginFigure(new Point(x, top), isFilled: false);
            gc.LineTo(new Point(x + w / 2, top + h / 2));
            gc.LineTo(new Point(x + w, top));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, flap);
    }

    // The artifact glyph: two staggered rounded-square outlines in amber (brighter when hovered).
    // Clickable — hit-testing + hover wiring land in 4.11.
    private static void DrawArtifactIcon(DrawingContext ctx, double x, double midY, bool hovered)
    {
        var pen = new Pen(hovered ? ArtifactHover : ArtifactBrush, 1.4, null, PenLineCap.Flat, PenLineJoin.Round);
        const double side = 8, offset = 3, radius = 2;
        double top = midY - (side + offset) / 2;
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(x, top, side, side), radius));
        ctx.DrawRectangle(null, pen, new RoundedRect(new Rect(x + offset, top + offset, side, side), radius));
    }

    // Stuck-detection warning: an amber triangle with an exclamation mark punched out of the panel bg.
    private static void DrawWarnIcon(DrawingContext ctx, double x, double midY)
    {
        const double w = 12, h = 11;
        double top = midY - h / 2;
        double cx = x + w / 2;

        var tri = new StreamGeometry();
        using (var gc = tri.Open())
        {
            gc.BeginFigure(new Point(cx, top), isFilled: true);
            gc.LineTo(new Point(x, top + h));
            gc.LineTo(new Point(x + w, top + h));
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(WarnBrush, null, tri);

        // Exclamation: a short stem above a square dot, in the panel background colour.
        ctx.DrawRectangle(BgFillBrush, null, new Rect(cx - 1, top + 3, 2, 4));
        ctx.DrawRectangle(BgFillBrush, null, new Rect(cx - 1, top + 8, 2, 2));
    }

    // Context-pressure thermometer: glass tube + bulb with mercury rising; colour steps green→yellow→
    // orange→red at the configured thresholds.
    private void DrawThermoIcon(DrawingContext ctx, float fill, double x, double midY)
    {
        Color col = fill >= _ctxRed    ? Color.FromRgb(239, 68, 68)
                  : fill >= _ctxOrange ? Color.FromRgb(249, 115, 22)
                  : fill >= _ctxYellow ? Color.FromRgb(234, 179, 8)
                                       : Color.FromRgb(34, 197, 94);
        var colBrush = new SolidColorBrush(col);

        double cx = x + 5;
        var tube = new Rect(cx - 2, midY - 7, 4, 9);
        var bulb = new Rect(cx - 4, midY, 8, 8);

        // Glass background.
        ctx.DrawRectangle(ThermoGlassFill, null, new RoundedRect(tube, 2));
        ctx.DrawEllipse(ThermoGlassFill, null, bulb.Center, bulb.Width / 2, bulb.Height / 2);

        // Mercury rising in the tube.
        double innerH = tube.Height - 2;
        double fillPx = Math.Clamp(fill * innerH, 0, innerH);
        if (fillPx > 0)
            ctx.DrawRectangle(colBrush, null,
                new Rect(tube.X + 1, tube.Bottom - 1 - fillPx, tube.Width - 2, fillPx));

        // Bulb is always full when visible.
        var innerBulb = new Rect(bulb.X + 1, bulb.Y + 1, bulb.Width - 2, bulb.Height - 2);
        ctx.DrawEllipse(colBrush, null, innerBulb.Center, innerBulb.Width / 2, innerBulb.Height / 2);

        // Glass outline.
        ctx.DrawRectangle(null, ThermoOutline, new RoundedRect(tube, 2));
        ctx.DrawEllipse(null, ThermoOutline, bulb.Center, bulb.Width / 2, bulb.Height / 2);
    }

    // The permission-mode badge: two fast-forward chevrons in the mode's colour, faded when idle.
    private static void DrawModeBadge(DrawingContext ctx, PermissionMode mode, double x, double midY, int alpha)
    {
        Color c = Palette.ModeColor(mode);
        if (alpha < 255) c = Color.FromArgb((byte)alpha, c.R, c.G, c.B);
        var brush = new SolidColorBrush(c);
        const double hh = 4, w = 5;
        Chevron(ctx, brush, x, midY, hh, w);
        Chevron(ctx, brush, x + w + 1, midY, hh, w);

        static void Chevron(DrawingContext ctx, IBrush brush, double x, double midY, double hh, double w)
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(x, midY - hh), isFilled: true);
                gc.LineTo(new Point(x + w, midY));
                gc.LineTo(new Point(x, midY + hh));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }
    }

    // A session's rolled-up resource use as two stacked micro-bars — CPU on top, RAM below — each filled
    // proportionally and coloured by load (green → red via Palette.UsageColor). CPU is a percentage of
    // the whole machine; RAM is drawn against total physical RAM (the same denominator as the strip's
    // RAM bar), so a row's bars are directly comparable to the machine total. x is the left edge of the
    // reserved MetricsBarWidth; midY is the row's name-line centre.
    private void DrawMetricsBars(DrawingContext ctx, SessionMetrics m, double x, double midY)
    {
        const double barH = 3;
        double barW = MetricsBarWidth - 4; // small inset within the reserved width
        double cpuY = midY - 4;            // top bar just above centre  (WinForms: midY − gap/2 − barH)
        double ramY = midY + 1;            // bottom bar just below       (WinForms: midY + (gap+1)/2)

        double cpuPct = Math.Clamp(m.CpuPercent, 0, 100);
        double ramPct = _sysMetrics.TotalRamBytes > 0
            ? Math.Clamp(100.0 * m.RamBytes / _sysMetrics.TotalRamBytes, 0, 100)
            : 0;

        DrawMiniBar(ctx, x, cpuY, barW, barH, cpuPct);
        DrawMiniBar(ctx, x, ramY, barW, barH, ramPct);
    }

    private static void DrawMiniBar(DrawingContext ctx, double x, double y, double w, double h, double pct)
    {
        OverlayDraw.Pill(ctx, new SolidColorBrush(UsageTrackColor), new Rect(x, y, w, h));
        double fillW = Math.Round(w * pct / 100.0);
        if (fillW > 0)
            OverlayDraw.Pill(ctx, new SolidColorBrush(Palette.UsageColor(pct)), new Rect(x, y, fillW, h));
    }

    // ── Pointer interaction ──────────────────────────────────────────────────
    // Row highlight + hand cursors on hover; click routes to artifact-open, row-focus, quick-link
    // launch, or header expand/collapse. The dwell tooltips (thermo/warn/task/metrics), window drag,
    // and right-click menu land in 4.12/4.16/4.13 respectively.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);

        int row = HitTestRow(p);
        if (row != _hoveredRow) { _hoveredRow = row; InvalidateVisual(); }

        int ql = HitTestQuickLink(p);
        if (ql != _hoveredQuickLink) { _hoveredQuickLink = ql; InvalidateVisual(); }

        int art = HitTestArtifactIcon(p);
        if (art != _hoveredArtifactRow) { _hoveredArtifactRow = art; InvalidateVisual(); }

        // Hand cursor over clickable glyphs (quick links + artifacts); rows show only the highlight.
        Cursor = (ql >= 0 || art >= 0) ? HandCursor : Cursor.Default;

        UpdateDwell(p);
        base.OnPointerMoved(e);
    }

    // Picks whichever info glyph (or the usage strip) the cursor is over and (re)arms the dwell timer.
    private void UpdateDwell(Point p)
    {
        (TipKind kind, int row) =
            HitTestThermoIcon(p) is var th && th >= 0 ? (TipKind.Thermo, th) :
            HitTestWarnIcon(p)   is var wa && wa >= 0 ? (TipKind.Warn, wa) :
            HitTestTaskCount(p)  is var ta && ta >= 0 ? (TipKind.Task, ta) :
            HitTestMetrics(p)    is var me && me >= 0 ? (TipKind.Metrics, me) :
            InUsageStrip(p)                           ? (TipKind.Usage, -1) :
                                                        (TipKind.None, -1);

        if (kind == _tipKind && row == _tipRow) return;
        _tipKind = kind;
        _tipRow = row;
        _dwellTimer?.Stop();
        _tooltip?.HideTip();
        if (kind == TipKind.None) return;

        _dwellTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _dwellTimer.Tick -= OnDwellTick;
        _dwellTimer.Tick += OnDwellTick;
        _dwellTimer.Start();
    }

    private void OnDwellTick(object? sender, EventArgs e)
    {
        _dwellTimer?.Stop();
        switch (_tipKind)
        {
            case TipKind.Usage:   ShowUsageTooltip();          break;
            case TipKind.Thermo:  ShowThermoTooltip(_tipRow);  break;
            case TipKind.Warn:    ShowWarnTooltip(_tipRow);    break;
            case TipKind.Task:    ShowTaskTooltip(_tipRow);    break;
            case TipKind.Metrics: ShowMetricsTooltip(_tipRow); break;
        }
    }

    private bool InUsageStrip(Point p) =>
        _expanded && _rows.Count > 0 && _usageEnabled
        && p.Y >= UsageStripTop && p.Y < UsageStripTop + UsageStripHeight;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        bool changed = _hoveredRow != -1 || _hoveredQuickLink != -1 || _hoveredArtifactRow != -1;
        _hoveredRow = _hoveredQuickLink = _hoveredArtifactRow = -1;
        Cursor = Cursor.Default;
        _tipKind = TipKind.None;
        _tipRow = -1;
        _dwellTimer?.Stop();
        _tooltip?.HideTip();
        if (changed) InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            ShowContextMenuAt(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) { base.OnPointerPressed(e); return; }
        var p = e.GetPosition(this);

        // Artifact glyph sits inside a session row, so it must be checked before the row's focus click.
        int art = HitTestArtifactIcon(p);
        if (art >= 0 && _rows[art].Session is { } artSession)
        {
            ArtifactActivated?.Invoke(artSession);
            e.Handled = true;
        }
        else if (HitTestRow(p) is var row && row >= 0)
        {
            if (_rows[row].IsSectionHeader)
            {
                _autonomousExpanded = !_autonomousExpanded;
                Update(_sessions); // rebuild the render list under the new section state
            }
            else
            {
                // Sub-agent rows resolve to their parent session (they share its process/terminal).
                SessionActivated?.Invoke(_rows[row].Session!);
            }
            e.Handled = true;
        }
        else if (HitTestQuickLink(p) is var ql && ql >= 0 && ql < _quickLinks.Count)
        {
            QuickLinkActivated?.Invoke(_quickLinks[ql]);
            e.Handled = true;
        }
        else if (p.Y < HeaderHeight && _sessions.Count > 0)
        {
            // Header click toggles expand/collapse; SizeToContent resizes the window to match.
            _expanded = !_expanded;
            InvalidateMeasure();
            InvalidateVisual();
            e.Handled = true;
        }

        base.OnPointerPressed(e);
    }

    // ── Context menu ─────────────────────────────────────────────────────────
    // Opens a right-click menu at the clicked point, with each item scoped to what's under the cursor:
    // per-session actions on a session row, strip toggles over a strip, and the header menu (the one
    // place that can bring a hidden strip back, plus Exit) over the header. No applicable item → no menu.
    private void ShowContextMenuAt(Point p)
    {
        var items = new List<Control>();

        int row = HitTestRow(p);
        // The "Autonomous" divider carries no session, so none of the per-session items apply to it.
        bool sessionRow = row >= 0 && !_rows[row].IsSectionHeader;
        bool subRow = sessionRow && _rows[row].IsSubAgent;

        if (sessionRow && _rows[row].Session is { } s)
        {
            // View history / copy id / open transcript work on any session row — sub-agent rows resolve
            // to their parent session, which owns the transcript.
            items.Add(MenuItem("View history", () => HistoryRequested?.Invoke(s.SessionId)));
            items.Add(MenuItem("Copy session ID", () => CopyToClipboard(s.SessionId)));
            items.Add(MenuItem("Open transcript in VS Code", () => OpenTranscriptInVsCode(s)));

            if (s.RemoteControlled)
                items.Add(MenuItem("Show QR code", () => QrRequested?.Invoke(s)));

            // External-notify + confetti apply only to a real session row (not its sub-agents), and only
            // while switched on globally.
            if (!subRow && _externalNotifyAvailable)
            {
                string label = s.ExternalNotify ? "Disable external notifications" : "Enable external notifications";
                items.Add(MenuItem(label, () => ExternalNotifyToggleRequested?.Invoke(s.SessionId)));
            }
            if (!subRow && _confettiAvailable)
            {
                string label = ConfettiArmed(s) ? "Cancel confetti finish" : "Confetti finish";
                items.Add(MenuItem(label, () => ToggleConfetti(s.SessionId)));
            }
        }

        // Right-clicking a strip toggles just that strip off (only when it's actually showing); the
        // header menu below can turn either back on.
        bool showRows = _expanded && _rows.Count > 0;
        bool overSystemStrip = showRows && _showSystemMetrics && p.Y >= HeaderHeight && p.Y < UsageStripTop;
        if (overSystemStrip)
            items.Add(MenuItem("Hide system metrics", () => SystemMetricsToggleRequested?.Invoke(false)));
        if (InUsageStrip(p))
            items.Add(MenuItem("Hide usage", () => UsageToggleRequested?.Invoke(false)));

        if (p.Y >= 0 && p.Y < HeaderHeight)
        {
            // Both strip toggles regardless of state, so the header is the one place that can bring a
            // hidden strip back. Wording flips with the current state.
            items.Add(MenuItem(_showSystemMetrics ? "Hide system metrics" : "Show system metrics",
                () => SystemMetricsToggleRequested?.Invoke(!_showSystemMetrics)));
            items.Add(MenuItem(_usageEnabled ? "Hide usage" : "Show usage",
                () => UsageToggleRequested?.Invoke(!_usageEnabled)));
            items.Add(MenuItem("Exit Perch", () => ExitRequested?.Invoke()));
        }

        if (items.Count == 0) return;

        var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.Pointer };
        flyout.ShowAt(this, showAtPointer: true);
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void CopyToClipboard(string text) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    private static void OpenTranscriptInVsCode(ClaudeSession s)
    {
        var path = TranscriptLocator.Resolve(s.SessionId, s.Cwd);
        if (path == null) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best-effort — VS Code may not be on PATH */ }
    }

    // ── Tooltip content ────────────────────────────────────────────────────────
    private OverlayTooltip Tooltip() => _tooltip ??= new OverlayTooltip();
    private PixelPoint ToScreen(double x, double y) => this.PointToScreen(new Point(x, y));

    private void ShowThermoTooltip(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Session is not { } s) return;
        if (!_thermoRects.TryGetValue(row, out var r)) return;
        float fill = s.ContextFill ?? 0f;
        int pct = (int)Math.Round(fill * 100f);
        long window = s.ContextWindow;
        long used = (long)Math.Round(fill * window);
        Tooltip().ShowText($"{FormatTokens(used)}/{FormatTokens(window)} ({pct}%)", ToScreen(r.Left, r.Bottom + 4));
    }

    private void ShowWarnTooltip(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Session?.Stuck is not { } stuck) return;
        if (!_warnRects.TryGetValue(row, out var r)) return;
        Tooltip().ShowText(stuck.Reason, ToScreen(r.Left, r.Bottom + 4));
    }

    private void ShowTaskTooltip(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Session is not { } s) return;
        if (!_taskRects.TryGetValue(row, out var r) || s.Tasks.Count == 0) return;

        // One line per task, prefixed by a status glyph: ✓ done, ▸ in progress, ○ pending. The active
        // task reads better as its gerund; the rest by subject. Long labels are clipped.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Tasks.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var t = s.Tasks[i];
            char glyph = t.State switch
            {
                TaskState.Completed  => '✓',
                TaskState.InProgress => '▸',
                _                    => '○',
            };
            string label = t.State == TaskState.InProgress && t.ActiveForm.Length > 0 ? t.ActiveForm : t.Subject;
            if (label.Length > 64) label = label[..63].TrimEnd() + "…";
            sb.Append(glyph).Append(' ').Append(label);
        }
        Tooltip().ShowText(sb.ToString(), ToScreen(r.Left, r.Bottom + 4));
    }

    private void ShowMetricsTooltip(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Session is not { } s) return;
        if (!_metricsRects.TryGetValue(row, out var r)) return;
        if (!_sessionMetrics.TryGetValue(s.Pid, out var m)) return;

        string text = $"CPU {m.CpuPercent:0}%   ·   RAM {FormatBytes(m.RamBytes)}";
        if (m.ProcessCount > 1) text += $"   ·   {m.ProcessCount} procs";
        Tooltip().ShowText(text, ToScreen(r.Left, r.Bottom + 4));
    }

    private void ShowUsageTooltip()
    {
        var now = DateTime.Now;
        var lines = new List<OverlayTooltip.Line>
        {
            new("Plan usage", OverlayTooltip.FgColor, true),
            new(UsageLine("Session", _usage.FiveHourPercent, _usage.FiveHourResetsAt, now), OverlayTooltip.FgColor, false),
            new(UsageLine("Weekly",  _usage.SevenDayPercent, _usage.SevenDayResetsAt, now), OverlayTooltip.FgColor, false),
        };
        if (_usage.IsStale(now))
        {
            var reason = _usage.Error;
            if (string.IsNullOrEmpty(reason))
                reason = _usage.LastUpdated == DateTime.MinValue
                    ? "No usage data yet"
                    : $"Updated {Ago(now - _usage.LastUpdated)} ago — couldn't refresh";
            lines.Add(new(reason, OverlayTooltip.MutedColor, false));
        }
        // Open to the left of the overlay's left edge so it never covers the strip.
        Tooltip().ShowLines(lines, ToScreen(0, UsageStripTop), placeLeft: true);
    }

    private static string UsageLine(string label, double? percent, DateTime? resetsAt, DateTime now)
    {
        string pct = percent is { } p ? $"{(int)Math.Round(p)}%" : "—";
        string s = $"{label}  {pct}";
        if (resetsAt is { } r && r > now) s += $"  · resets in {Until(r - now)}";
        return s;
    }

    private static string Until(TimeSpan t) =>
        t.TotalDays >= 1  ? $"{(int)t.TotalDays}d {t.Hours}h"
      : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
                          : $"{Math.Max(1, (int)t.TotalMinutes)}m";

    private static string Ago(TimeSpan t) =>
        t.TotalHours >= 1   ? $"{(int)t.TotalHours}h"
      : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m"
                            : $"{(int)t.TotalSeconds}s";

    // Compact token count for the thermometer tooltip: 34631 → "34.6k", 1000000 → "1M".
    private static string FormatTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.##}M"
      : n >= 1_000     ? $"{n / 1_000.0:0.#}k"
                       : n.ToString();

    // Compact byte count for the metrics tooltip: 536870912 → "512 MB", 1610612736 → "1.5 GB".
    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
      : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
      : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0} KB"
                          : $"{bytes} B";

    private static Bitmap? LoadBrand()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://perch-avalonia/Assets/icon.png"))); }
        catch { return null; }
    }
}
