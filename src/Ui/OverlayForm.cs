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
    private const int RowHeight        = 46;
    private const int SubRowHeight      = 24;
    private const int SubIndent         = 22;
    private const int HorizPad          = 12;
    private const int Corner            = 10;
    private const int RcIconWidth       = 14;  // width reserved for the remote-control glyph in a row
    private const int MailIconWidth     = 16;  // width reserved for the external-notify (mail) glyph
    private const int ArtifactIconWidth = 16;  // width reserved for the clickable artifact glyph in a row
    private const int ThermoIconWidth   = 12;  // width reserved for the context-pressure thermometer
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
    private static readonly Color BorderAttention= Color.FromArgb(251, 146, 60);
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
    private static readonly Color MailColor      = Color.FromArgb(94,  234, 212);
    private static readonly Color ArtifactColor  = Color.FromArgb(251, 191, 36);   // amber — the clickable artifact glyph
    private static readonly Color TreeLineColor  = Color.FromArgb(55,  55,  72);
    private static readonly Color UsageRedColor  = Color.FromArgb(239, 68,  68);
    private static readonly Color UsageTrackColor= Color.FromArgb(38,  38,  52);

    // ── State ─────────────────────────────────────────────────────────────────
    // A flat render list of parent-session rows interleaved with their running sub-agent
    // child rows, in draw order. Built from the sessions on each update.
    private readonly record struct DisplayRow(ClaudeSession Session, SubAgent? Sub)
    {
        public bool IsSubAgent => Sub != null;
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool  _expanded = true;
    private bool  _dragging;
    private Point _dragStartScreen;
    private Point _formStartLoc;
    private bool  _wasDrag;
    private int   _hoveredRow = -1;
    // The row index whose artifact glyph the cursor is currently over, or -1. Drives the hand cursor
    // and a brighter glyph; the glyph is clickable independently of the row's focus-terminal click.
    private int   _hoveredArtifactRow = -1;
    private bool  _attentionFlash;

    // Dense mode: an alternate, out-of-the-way presentation (a slim strip hugging a screen edge that
    // expands on hover). The whole state machine — on/off, the hover popup, the docked edge/monitor,
    // the remembered positions, drag-to-redock, and the strip painting — lives in this controller; the
    // form just forwards paint/mouse/relayout to it. Created in the constructor (after _icon exists).
    private readonly DenseModeController _denseMode;

    private UsageInfo _usage = UsageInfo.Empty;
    private bool _usageEnabled = true;
    private bool _showExpectedRate = true;
    private bool _showContextPressure = true;
    // Context-pressure thresholds as fractions of the window: hidden below yellow, then yellow ->
    // orange -> red. Defaults match the original hard-coded bands; overridden from settings.
    private float _ctxYellow = 0.50f, _ctxOrange = 0.65f, _ctxRed = 0.80f;
    private bool _inUsageStrip;
    private readonly UsageTooltipForm _usageTooltip = new();

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

    // Top of the session rows when expanded: header, plus the usage strip and optional quick-links row.
    private int RowsTop => HeaderHeight + (_usageEnabled ? UsageStripHeight : 0) + (HasQuickLinksRow ? QuickLinksRowHeight : 0);

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

    // Is the full session body (usage bars + rows) currently on screen? In floating mode that's
    // the expanded state; in dense mode it's the hover-opened popup.
    private bool ShowFullPanel => _denseMode.IsDense ? _denseMode.IsOpen : _expanded;

    // External (ntfy) notifications. _externalNotifyAvailable mirrors the global setting and gates
    // both the per-session glyph and the right-click toggle; _externalNotifySessions holds the
    // session ids opted in. Both are pushed in from the owning context, which is the source of truth.
    private bool _externalNotifyAvailable;
    private HashSet<string> _externalNotifySessions = new();

    public event EventHandler? ExitRequested;
    public event Action<string>? SessionFocused;

    /// <summary>Raised when the user picks "View history" for a session; carries that session's id so
    /// the owning context can open the history viewer on it.</summary>
    public event Action<string>? HistoryRequested;

    /// <summary>Raised when the user picks "Enable/Disable external notifications" for a session;
    /// carries that session's id for the context to flip its opt-in state.</summary>
    public event Action<string>? ExternalNotifyToggleRequested;

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

        _flashTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _flashTimer.Tick += (_, _) => { _attentionFlash = !_attentionFlash; Invalidate(); };

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
    Bitmap? IDenseHost.Icon => _icon;
    void IDenseHost.RelayoutWindow() => RelayoutWindow();
    void IDenseHost.UpdateTickTimer() => UpdateTickTimer();
    void IDenseHost.ClearRowHover() => _hoveredRow = -1;
    void IDenseHost.HideUsageTooltip() => HideUsageTooltip();

    // ── Public API ────────────────────────────────────────────────────────────
    public void UpdateSessions(IReadOnlyList<ClaudeSession> sessions)
    {
        _sessions = sessions;

        var rows = new List<DisplayRow>();
        foreach (var session in sessions.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new DisplayRow(session, null));
            foreach (var sub in session.SubAgents)
                rows.Add(new DisplayRow(session, sub));
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

    public void SetShowContextPressure(bool show)
    {
        if (_showContextPressure == show) return;
        _showContextPressure = show;
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

    // The run-time labels only need a per-second repaint when they're actually on screen.
    private void UpdateTickTimer()
    {
        bool need = ShowFullPanel && _sessions.Any(s => s.Status == SessionStatus.Running);
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
    private static int HeightOf(DisplayRow row) => row.IsSubAgent ? SubRowHeight : RowHeight;

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

        var borderColor = _attentionFlash ? BorderAttention : BorderNormal;
        using (var pen = new Pen(borderColor, 1.5f))
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
            if (_usageEnabled)
                DrawUsageBars(g);
            if (HasQuickLinksRow)
                DrawQuickLinksRow(g);
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(g, i);
        }
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
        if (_icon is { } icon)
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

        // Dense toggle icon (always present). The glyph points along the docked edge: floating mode
        // shows the arrow collapsing toward that edge, dense mode shows it expanding inward. Clicking
        // it enters dense from floating, or leaves dense from the open popup.
        DrawSideCollapseIcon(g, SideIconRect(), reversed: _denseMode.IsDense ^ (_denseMode.Side == DenseSide.Left));

        // Expand chevron — floating mode only (hidden in dense), and only when there's something to
        // expand. Sits just to the left of the dense toggle icon.
        if (!_denseMode.IsDense && _sessions.Count > 0)
        {
            var chevron = _expanded ? "▲" : "▼";
            var chSz    = g.MeasureString(chevron, chevFont);
            g.DrawString(chevron, chevFont, muted,
                ClientSize.Width - HorizPad - IconBoxW - IconGap - chSz.Width,
                midY - chSz.Height / 2);
        }
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

    // ── Usage bars ─────────────────────────────────────────────────────────────
    // Two always-visible bars below the banner: the 5-hour ("Session") and 7-day ("Weekly")
    // rate-limit windows. Dimmed when the data is stale/unavailable.
    private void DrawUsageBars(Graphics g)
    {
        bool stale = _usage.IsStale(DateTime.Now);

        using var capFont = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var pctFont = new Font("Segoe UI", 7.5f, FontStyle.Bold,    GraphicsUnit.Point);

        int top = HeaderHeight + 2;
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

        int rowTop  = HeaderHeight + (_usageEnabled ? UsageStripHeight : 0);
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
        int rowTop = HeaderHeight + (_usageEnabled ? UsageStripHeight : 0);
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
        if (_rows[rowIdx].IsSubAgent)
            DrawSubAgentRow(g, rowIdx);
        else
            DrawSessionRow(g, rowIdx);
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

        // Tree connector: a stub dropping from the parent row down to this child's dot.
        int branchX = HorizPad + 4;            // aligns under the parent status dot
        int dotX    = HorizPad + SubIndent;
        using (var treePen = new Pen(TreeLineColor, 1f))
        {
            g.DrawLine(treePen, branchX, top - SubRowHeight / 2, branchX, midY);
            g.DrawLine(treePen, branchX, midY, dotX - 2, midY);
        }

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

    private void DrawSessionRow(Graphics g, int rowIdx)
    {
        var session = _rows[rowIdx].Session;
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
        var activity   = running ? session.Activity : null;
        var elapsed    = running ? session.RunningElapsedLabel() : null;
        bool twoLine   = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        int nameMidY   = twoLine ? top + RowHeight / 2 - 8 : midY;

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

        bool hasArtifacts= session.HasArtifacts;
        int artWidth     = hasArtifacts ? ArtifactIconWidth : 0;
        bool mail        = ExternalNotifyEnabled(session);
        int mailWidth    = mail ? MailIconWidth : 0;
        int badgeWidth   = session.Mode != PermissionMode.Normal ? 16 : 0;
        int rcWidth      = session.RemoteControlled ? RcIconWidth : 0;
        float ctxFill    = session.ContextFill ?? 0f;
        int thermoWidth  = _showContextPressure && ctxFill >= _ctxYellow ? ThermoIconWidth + 2 : 0;  // icon + 2 px gap right
        var statusSz     = g.MeasureString(statusText, statusFont);
        int nameMaxWidth = ClientSize.Width - HorizPad * 3 - 8 - (int)statusSz.Width - badgeWidth - rcWidth - mailWidth - artWidth - thermoWidth;
        var nameTrunc    = TruncateString(g, session.DisplayName, nameFont, nameMaxWidth);
        var nameSz       = g.MeasureString(nameTrunc, nameFont);

        // Glyphs sit just right of the status dot and push the name across: the artifact glyph first
        // (closest to the dot, and clickable to open/pick a published artifact), then the mail glyph
        // (external notifications opted in), then the remote-control broadcast glyph.
        if (hasArtifacts)
            DrawArtifactIcon(g, HorizPad + 14, nameMidY, rowIdx == _hoveredArtifactRow);
        if (mail)
            DrawMailIcon(g, HorizPad + 14 + artWidth, nameMidY);
        if (session.RemoteControlled)
            DrawRemoteIcon(g, HorizPad + 16 + artWidth + mailWidth, nameMidY);

        g.DrawString(nameTrunc, nameFont, fgBrush,
            HorizPad + 14 + artWidth + mailWidth + rcWidth, nameMidY - nameSz.Height / 2);

        int statusX = ClientSize.Width - HorizPad - (int)statusSz.Width;
        g.DrawString(statusText, statusFont, statusBrush,
            statusX, nameMidY - statusSz.Height / 2);

        // Thermometer: to the right of the mode badge (between badge and status text).
        if (thermoWidth > 0)
            DrawThermoIcon(g, ctxFill, statusX - thermoWidth, nameMidY);

        if (session.Mode != PermissionMode.Normal)
            Glyphs.DrawModeBadge(g, session.Mode, statusX - thermoWidth - badgeWidth, nameMidY, 4, 5);

        if (twoLine)
        {
            int activityMidY = top + RowHeight / 2 + 9;
            int lineLeft     = HorizPad + 14;

            // Elapsed run time, right-aligned and dim.
            int elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                var elapsedSz = g.MeasureString(elapsed, activityFont);
                elapsedW = (int)elapsedSz.Width;
                g.DrawString(elapsed, activityFont, mutedBrush,
                    ClientSize.Width - HorizPad - elapsedW, activityMidY - elapsedSz.Height / 2);
            }

            // Activity phrase fills the remaining width to the left of the elapsed time.
            if (!string.IsNullOrEmpty(activity))
            {
                int activityMaxW  = ClientSize.Width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                var activityTrunc = TruncateString(g, activity, activityFont, activityMaxW);
                var activitySz    = g.MeasureString(activityTrunc, activityFont);
                g.DrawString(activityTrunc, activityFont, mutedBrush,
                    lineLeft, activityMidY - activitySz.Height / 2);
            }
        }
    }


    // Context-pressure thermometer: tube (4 px wide, 9 px tall) + bulb (8 px diameter), with mercury
    // rising from the bottom. Only drawn at/above the yellow threshold; colour shifts yellow → orange
    // → red at the configured thresholds. x is the left edge of the reserved ThermoIconWidth area;
    // midY is the vertical row centre.
    private void DrawThermoIcon(Graphics g, float fill, int x, int midY)
    {
        if (fill < _ctxYellow) return;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color col = fill >= _ctxRed    ? Color.FromArgb(239, 68,  68)   // red
                  : fill >= _ctxOrange ? Color.FromArgb(249, 115, 22)   // orange
                                       : Color.FromArgb(234, 179,  8);  // yellow

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

            // Dwell over the usage strip (only the two bar rows, not the quick-links row below them).
            int usageStripEnd = HeaderHeight + (_usageEnabled ? UsageStripHeight : 0);
            bool inStrip = ShowFullPanel && _usageEnabled && e.Y >= HeaderHeight && e.Y < usageStripEnd;
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

        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenuAt(e.Location);
            base.OnMouseUp(e);
            return;
        }

        if (e.Button == MouseButtons.Left && !wasDrag)
        {
            bool headerVisible = !_denseMode.IsClosedStrip;

            if (headerVisible && SideIconRect().Contains(e.Location))
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
                if (row >= 0)
                {
                    // Sub-agent rows resolve to their parent session — the sub-agent runs in the
                    // parent's process, so focusing means focusing the parent terminal.
                    var pid = _rows[row].Session.Pid;
                    SessionFocused?.Invoke(pid);
                    if (int.TryParse(pid, out int pidInt))
                        NativeMethods.FocusTerminalForProcess(pidInt, _rows[row].Session.ProjectName);
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
        if (_hoveredQuickLink >= 0) { _hoveredQuickLink = -1; Cursor = Cursors.Default; }
        if (_hoveredArtifactRow >= 0) { _hoveredArtifactRow = -1; Cursor = Cursors.Default; }

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
        var stripScreen = RectangleToScreen(new Rectangle(0, HeaderHeight, ClientSize.Width, UsageStripHeight));
        _usageTooltip.ShowFor(_usage, stripScreen);
    }

    private void HideUsageTooltip()
    {
        if (_usageTooltip.Visible)
            _usageTooltip.Hide();
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
    private static int NameMidY(ClaudeSession s, int top)
    {
        bool running = s.Status == SessionStatus.Running;
        bool twoLine = running && (!string.IsNullOrEmpty(s.Activity) || !string.IsNullOrEmpty(s.RunningElapsedLabel()));
        return twoLine ? top + RowHeight / 2 - 8 : top + RowHeight / 2;
    }

    // The clickable rectangle of a row's artifact glyph, or Rectangle.Empty when the row has none
    // (or is a sub-agent row). Kept generous so the small glyph is easy to click.
    private Rectangle ArtifactIconRect(int rowIdx)
    {
        var row = _rows[rowIdx];
        if (row.IsSubAgent || !row.Session.HasArtifacts)
            return Rectangle.Empty;

        int top  = RowTop(rowIdx);
        int midY = NameMidY(row.Session, top);
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
        var artifacts = _rows[rowIdx].Session.Artifacts;
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
            .Select(a => (a.Title, (Action)(() => OpenArtifact(a))))
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
        var items = new List<(string Label, Action OnClick)>();

        int row = HitTestRow(clientPt);

        // View history — on any session row (sub-agent rows resolve to their parent session, which
        // owns the transcript). Listed first so it's the primary per-session action.
        if (row >= 0)
        {
            var historySession = _rows[row].Session;
            items.Add(("View history", () => HistoryRequested?.Invoke(historySession.SessionId)));
        }

        if (row >= 0)
        {
            var idSession = _rows[row].Session;
            items.Add(("Copy session ID", () => Clipboard.SetText(idSession.SessionId)));
        }

        if (row >= 0)
        {
            var txSession = _rows[row].Session;
            items.Add(("Open transcript in VS Code", () =>
            {
                var path = TranscriptLocator.Resolve(txSession.SessionId, txSession.Cwd);
                if (path != null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("code", $"\"{path}\"") { UseShellExecute = true });
            }));
        }

        if (row >= 0 && _rows[row].Session is { RemoteControlled: true } rc)
            items.Add(("Show QR code", () => ShowQrCode(rc)));

        // Per-session external-notify toggle — only on a real session row, and only while the feature
        // is switched on globally. Sub-agent rows share the parent session, so skip them.
        if (row >= 0 && !_rows[row].IsSubAgent && _externalNotifyAvailable)
        {
            var s = _rows[row].Session;
            string label = ExternalNotifyEnabled(s)
                ? "Disable external notifications"
                : "Enable external notifications";
            items.Add((label, () => ExternalNotifyToggleRequested?.Invoke(s.SessionId)));
        }

        bool headerVisible = !_denseMode.IsClosedStrip;
        bool overHeader = headerVisible && clientPt.Y >= 0 && clientPt.Y < HeaderHeight;
        if (overHeader)
            items.Add(("Exit Perch", () => ExitRequested?.Invoke(this, EventArgs.Empty)));

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
            _autoCloseBarTimer.Dispose();
            _usageTooltip.Dispose();
            _popover?.Dispose();
            _qrForm?.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
