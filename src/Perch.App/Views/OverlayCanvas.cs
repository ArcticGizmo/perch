using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
/// Built up over Phase 4 to full parity with <c>OverlayForm</c>: rounded panel (4.1); header/collapsed
/// bar (4.2); expanded session rows (4.3); sub-agents, teammates, and the collapsible "Autonomous"
/// section (4.4); per-row identity/health glyphs (4.5–4.7); usage, system-metrics, and quick-link strips
/// (4.8–4.10); hit-testing, hover, and dwell tooltips (4.11–4.12); the right-click menu (4.13); the
/// attention chase-border (4.14); the auto-close countdown (4.15); window drag/behaviors (4.16); and the
/// display-toggle gates driven from settings (4.17).
/// </summary>
public sealed class OverlayCanvas : Control, IDenseHost
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
    private const double NoteLineHeight   = 16; // extra height a row gains when a note needs its own third line
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
    private const double PartyIconWidth   = 16; // the "confetti finish" party-popper glyph on an armed row
    private const double NoteIconWidth    = 15; // the pinned-note glyph shown on a row that has a note

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
    // Dev-instance marker: a hot-pink brand so an isolated dev build is unmistakable next to a running
    // installed Perch — a 2px border around the panel plus a "Perch - DEV" header label. Only ever used
    // when AppProfile.IsDev, so a normal build never pays for it.
    private static readonly IBrush DevPinkBrush   = new SolidColorBrush(Color.FromRgb(244, 114, 182));
    private static readonly IPen   DevBorderPen   = new Pen(DevPinkBrush, 2);
    // Replay-instance marker: a light-blue brand + "Perch - Replay" header label so a replay is
    // unmistakable and can't be read as live sessions. Mirrors the dev marker and takes precedence over
    // it (a replay is always a dev `dotnet run` too). Only used when ReplayMode is set.
    private static readonly IBrush ReplayBlueBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
    private static readonly IPen   ReplayBorderPen = new Pen(ReplayBlueBrush, 2);

    /// <summary>True when this overlay is driving a replay recording. Swaps the dev/normal branding for
    /// a light-blue "Perch - Replay" header + border so a replay can't be mistaken for live sessions.
    /// Set once by <c>App</c> at startup when <c>perch replay</c> is active.</summary>
    public bool ReplayMode { get; set; }
    // "Jump to next session" landed here: a blue selection wash + left bar that holds then fades, so the
    // hotkey user can see which session they've cycled to. Blue reads as navigation, distinct from the
    // green/orange/yellow status hues.
    private static readonly Color  CycleColor     = Color.FromRgb(96, 165, 250);
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
    // Pinned-note glyph + its own note line: a sticky-note amber for the glyph, a dimmer amber for the
    // note text, so a note reads as a deliberate human annotation distinct from the muted activity line.
    private static readonly IBrush NoteBrush      = new SolidColorBrush(Color.FromRgb(244, 193, 79));
    private static readonly IBrush NoteLineBrush  = new SolidColorBrush(Color.FromRgb(217, 196, 143));

    // Party-popper glyph: a gold cone spraying a fan of festive confetti dots (shared with the confetti
    // window's palette so the armed-row hint and the finish burst read as one feature).
    private static readonly IBrush PartyConeBrush = new SolidColorBrush(Color.FromRgb(255, 190, 70));
    private static readonly (double dx, double dy, double r, IBrush brush)[] PartyBits =
    [
        (13, -6, 1.8, new SolidColorBrush(Color.FromRgb(255, 92, 92))),   // red
        (11, -2, 1.5, new SolidColorBrush(Color.FromRgb(94, 234, 212))),  // teal
        (14, -1, 1.6, new SolidColorBrush(Color.FromRgb(178, 120, 255))), // purple
        (10, -7, 1.4, new SolidColorBrush(Color.FromRgb(255, 236, 92))),  // yellow
        (13, -9, 1.5, new SolidColorBrush(Color.FromRgb(92, 214, 122))),  // green
    ];
    private static readonly IBrush ThermoGlassFill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IPen   ThermoOutline  = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
    private static readonly Color  MutedColor     = Color.FromRgb(110, 110, 130);
    private static readonly IBrush UpdateBrush    = new SolidColorBrush(Color.FromRgb(255, 68, 45));   // perch-logo red-orange — the update badge
    private static readonly IBrush UpdateHover     = new SolidColorBrush(Color.FromRgb(255, 104, 84));  // brightened while hovered
    private static readonly Color  SepColor       = Color.FromRgb(35, 35, 50);
    private static readonly IBrush RowHoverBrush  = new SolidColorBrush(Color.FromRgb(25, 25, 38));
    private static readonly Color  UsageTrackColor = Color.FromRgb(38, 38, 52);
    private static readonly Color  ExpectedMarkColor = Color.FromRgb(180, 180, 195);

    // Brand mark (the app icon), loaded once.
    private static readonly Bitmap? Brand = LoadBrand();

    // Owns dense mode (the slim edge strip that expands on hover) and its geometry/painting.
    private readonly DenseController _denseCtl;

    public OverlayCanvas()
    {
        // The brand mark and quick-link icons are 256px sources drawn at ~16–18px; the default sampler
        // aliases them badly. HighQuality makes Skia mipmap the downscale so they stay crisp at any DPI.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        _denseCtl = new DenseController(this, RunningColor, AwaitingColor, AttentionColor, IdleColor);
    }

    // Whether the full session body is currently on screen: the floating expand state, or — in dense
    // mode — whether the hover popup is open. Everything panel-related keys off this, not raw _expanded.
    private bool ShowFullPanel => _denseCtl.IsDense ? _denseCtl.IsOpen : _expanded;

    // ── Dense mode: public surface for the app (tray / hotkey / screen changes) ──
    public bool IsDense => _denseCtl.IsDense;
    public void ToggleDense() => _denseCtl.Toggle();
    public void OnScreensChanged() => _denseCtl.OnScreensChanged();
    public void DisposeDense() => _denseCtl.Dispose();

    // ── IDenseHost (geometry lives on the window) ──
    // The owning window, set by LiveOverlayWindow. VisualRoot resolves to an internal TopLevelHost (not
    // the Window), so we can't reach Position / Screens / BeginMoveDrag through it — hold a direct ref.
    internal Window? OwnerWindow { get; set; }
    private Window? HostWindow => OwnerWindow;

    Screens? IDenseHost.Screens => HostWindow?.Screens;

    PixelPoint IDenseHost.WindowPosition
    {
        get => HostWindow?.Position ?? default;
        set { if (HostWindow is { } w) w.Position = value; }
    }

    double IDenseHost.WindowScaling => HostWindow?.RenderScaling ?? 1.0;

    void IDenseHost.PlaceWindow(PixelPoint position, double dipWidth, double dipHeight)
    {
        if (HostWindow is not { } w) return;
        w.SizeToContent = SizeToContent.Manual;
        w.Width = dipWidth;
        w.Height = dipHeight;
        w.Position = position;
    }

    void IDenseHost.RestoreFloating(PixelPoint position)
    {
        if (HostWindow is not { } w) return;
        w.Width = FormWidth;
        w.SizeToContent = SizeToContent.Height; // recomputes height from content, keeps the 280 width
        w.Position = position;
    }

    double IDenseHost.FullPanelWidthDip => FormWidth;
    double IDenseHost.FullPanelHeightDip => FullPanelHeight();
    IReadOnlyList<ClaudeSession> IDenseHost.Sessions => _sessions;
    Bitmap? IDenseHost.Icon => Brand;
    void IDenseHost.RelayoutWindow() => RelayoutWindow();
    void IDenseHost.UpdateTickTimer() => UpdateTickTimer();
    void IDenseHost.ClearRowHover() => _hoveredRow = -1;
    void IDenseHost.HideTooltips() { _dwellTimer?.Stop(); _tooltip?.HideTip(); _tipKind = TipKind.None; _tipRow = -1; }
    void IDenseHost.Invalidate() { InvalidateMeasure(); InvalidateVisual(); }

    // Owns the window's size/position. Floating auto-sizes to content at a fixed 280 width; in dense mode
    // the controller docks the strip/popup to its remembered edge and Y. Mirrors OverlayForm.RelayoutWindow.
    private void RelayoutWindow()
    {
        if (HostWindow is not { } w) return;
        if (_denseCtl.IsDense) _denseCtl.ApplyGeometry();
        else { w.Width = FormWidth; w.SizeToContent = SizeToContent.Height; }
        InvalidateMeasure();
        InvalidateVisual();
    }

    // Re-applies the window footprint after a change that may alter the panel's content height. The floating
    // window auto-sizes through SizeToContent, so invalidating the measure is enough. The dense strip/popup
    // is sized manually (its MeasureOverride returns the size the controller placed), so a bare remeasure
    // repaints the now-shorter content but leaves the window at its old, taller size — an invisible region
    // that keeps eating clicks where the removed rows used to be. In dense mode we must re-run the geometry
    // so the window actually shrinks to match. Use this anywhere a toggle/update can change the panel height.
    private void RemeasurePanel()
    {
        if (_denseCtl.IsDense) RelayoutWindow();
        else { InvalidateMeasure(); InvalidateVisual(); }
    }

    // Height of the full panel (header + optional strips + all session rows), computed as if expanded —
    // the size the dense popup and the floating expanded panel both use. Kept in lockstep with Draw's
    // showRows branch so the measured window height and the painted layout can't drift.
    private double FullPanelHeight()
    {
        double h = HeaderHeight;
        if (_rows.Count > 0)
        {
            if (_showSystemMetrics) h += SysMetricsStripHeight;
            if (_usageEnabled) h += UsageStripHeight;
            if (HasQuickLinksRow) h += QuickLinksRowHeight;
            foreach (var r in _rows) h += HeightOf(r);
            h += 2;
        }
        if (ShowFooter) h += FooterHeight;
        return h;
    }

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
    private bool _showNoteLine = true;
    private bool _showStuckWarnings = true;
    private bool _showArtifacts = true;
    private bool _showWaitingTimer = true;
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
        RemeasurePanel();
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
        RemeasurePanel();
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

    // The global scratch-pad note button that leads the quick-links row: its hit-rect (captured at paint
    // time) and hover state. Clicking it opens the scratch pad (see RouteClick / ScratchPadRequested).
    private Rect _noteButtonRect;
    private bool _hoveredNoteButton;

    // The quick-links row is always shown with the panel now — it hosts the note button even when the user
    // has no quick links of their own.
    private bool HasQuickLinksRow => true;
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
        RemeasurePanel();
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

    /// <summary>Show/hide the context-pressure thermometer. Display only; the fill is still tracked.</summary>
    public void SetShowContextPressure(bool show)
    {
        if (_showContextPressure == show) return;
        _showContextPressure = show;
        if (!show) HideActiveTip();
        InvalidateVisual();
    }

    /// <summary>Show/hide the stuck-detection warning glyph. Display only — when off (or when nothing is
    /// flagged) no glyph is drawn; hides any open tooltip on the way out.</summary>
    public void SetStuckDetectionEnabled(bool enabled)
    {
        if (_showStuckWarnings == enabled) return;
        _showStuckWarnings = enabled;
        if (!enabled) HideActiveTip();
        InvalidateVisual();
    }

    /// <summary>Show/hide the task-list "n/m" progress count. Display only — the checklist is still
    /// tracked; the name reclaims the freed width. Hides any open tooltip on the way out.</summary>
    public void SetShowTaskProgress(bool show)
    {
        if (_showTaskProgress == show) return;
        _showTaskProgress = show;
        if (!show) HideActiveTip();
        InvalidateVisual();
    }

    /// <summary>Show/hide the live token burn-rate label. Display only; the rate is still computed.</summary>
    public void SetShowBurnRate(bool show)
    {
        if (_showBurnRate == show) return;
        _showBurnRate = show;
        InvalidateVisual();
    }

    /// <summary>Show/hide the unstaged git line-churn chip ("+142 -37"). Display only.</summary>
    public void SetShowGitStats(bool show)
    {
        if (_showGitStats == show) return;
        _showGitStats = show;
        InvalidateVisual();
    }

    /// <summary>Show/hide a pinned note's own line under a session. Off keeps the note glyph + hover (the
    /// note is still there, just compact); on gives it a dedicated line. Changes row height, so relayout.</summary>
    public void SetShowNoteLine(bool show)
    {
        if (_showNoteLine == show) return;
        _showNoteLine = show;
        RemeasurePanel();
    }

    /// <summary>Show/hide the "waiting on you" timer on awaiting-input rows. Display only — the row still
    /// shows its "input ↩" status, just without the elapsed-wait line.</summary>
    public void SetShowWaitingTimer(bool show)
    {
        if (_showWaitingTimer == show) return;
        _showWaitingTimer = show;
        UpdateTickTimer(); // an awaiting row now needs (or no longer needs) the per-second repaint
        InvalidateVisual();
    }

    /// <summary>How many minutes a blocked session takes to warm the waiting timer fully to red (min 1).</summary>
    public void SetWaitingTimerRedMinutes(int minutes)
    {
        minutes = Math.Max(1, minutes);
        if (_waitingTimerRedMinutes == minutes) return;
        _waitingTimerRedMinutes = minutes;
        InvalidateVisual();
    }

    /// <summary>Sets the context-pressure colour thresholds (whole percentages). The thermometer is
    /// hidden below <paramref name="yellow"/>, then warms yellow → orange → red.</summary>
    public void SetContextThresholds(int yellow, int orange, int red)
    {
        _ctxYellow = yellow / 100f;
        _ctxOrange = orange / 100f;
        _ctxRed    = red    / 100f;
        InvalidateVisual();
    }

    // Hides whatever dwell tooltip is showing (used when a gate that owns a tooltip target turns off).
    private void HideActiveTip()
    {
        _tipKind = TipKind.None;
        _tipRow = -1;
        _dwellTimer?.Stop();
        _tooltip?.HideTip();
    }

    private IReadOnlyList<ClaudeSession> _sessions = [];
    private List<DisplayRow> _rows = [];
    private bool _expanded = true;
    private bool _autonomousExpanded;

    // Sub-agent tree nodes the user has collapsed, by agent id. A node is expanded by default (absent
    // here); collapsing hides its subtree and rolls a "+N" hidden-count onto the row. In-memory only —
    // a collapse doesn't survive a restart, and ids for agents that have since finished simply go unused.
    private readonly HashSet<string> _collapsedAgents = new();

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
    private readonly Dictionary<int, Rect> _noteRects = new();
    // The expand/collapse chevron on a sub-agent row that has children — captured at paint time so a
    // click can toggle that node (see RouteClick). Only rows with children get an entry.
    private readonly Dictionary<int, Rect> _subChevronRects = new();

    // The header's update badge (a perch-orange download disc), shown only while an update is pending. Its
    // hit-rect is captured at paint time; clicking it raises UpdateRequested for the app to apply.
    private bool _updateAvailable;
    private bool _hoveredUpdateIcon;
    private Rect _updateIconRect;

    /// <summary>Raised when the user clicks the header's update badge, asking the app to download and
    /// apply the pending update.</summary>
    public event Action? UpdateRequested;

    /// <summary>Shows or hides the header's update badge (repaint only — the badge lives in the header
    /// glyph cluster and is drawn each frame from this flag).</summary>
    public void SetUpdateAvailable(bool available)
    {
        if (_updateAvailable == available) return;
        _updateAvailable = available;
        InvalidateVisual();
    }

    // ── Service status footer ─────────────────────────────────────────────────
    // A slim banner pinned to the panel bottom, shown only when status.claude.com reports an issue.
    // Clicking it opens a menu of the unresolved incidents plus a link to the status page.
    private const double FooterHeight = 24;
    private StatusInfo _status = StatusInfo.Healthy;
    private bool _serviceStatusEnabled = true;
    private Rect _footerRect;
    private bool _hoveredFooter;

    private static readonly Color FooterMinorColor = Color.FromRgb(245, 158, 11); // amber (minor)
    private static readonly Color FooterMajorColor = Color.FromRgb(239, 68, 68);  // red (major / critical)
    private static readonly Color FooterMaintColor = Color.FromRgb(96, 165, 250); // blue (maintenance)

    // The footer is on screen only when the feature is enabled and there's actually an issue.
    private bool ShowFooter => _serviceStatusEnabled && _status.HasIssue;

    /// <summary>Feeds the latest service-status reading (on the UI thread). When the footer's visibility
    /// flips the panel height changes, so remeasure (the floating window auto-sizes to content); otherwise
    /// just repaint. Internal because <see cref="StatusInfo"/> is a Core-internal type shared via
    /// InternalsVisibleTo.</summary>
    internal void UpdateStatus(StatusInfo status)
    {
        bool before = ShowFooter;
        _status = status;
        if (ShowFooter != before) RemeasurePanel();
        else InvalidateVisual();
    }

    /// <summary>Show/hide the whole service-status footer feature. Changes the panel height when there's a
    /// live issue, so remeasure; hides any footer immediately when turned off.</summary>
    public void SetServiceStatusEnabled(bool enabled)
    {
        if (_serviceStatusEnabled == enabled) return;
        bool before = ShowFooter;
        _serviceStatusEnabled = enabled;
        if (ShowFooter != before) RemeasurePanel();
        else InvalidateVisual();
    }

    /// <summary>Raised when a session row is clicked (sub-agent rows resolve to their parent). The app
    /// focuses the session's terminal via the platform seam. Internal — <see cref="ClaudeSession"/> is
    /// a Core-internal type.</summary>
    internal event Action<ClaudeSession>? SessionActivated;

    /// <summary>Raised when the user picks an artifact from the artifact glyph's popover list; the app
    /// opens it. The list is always shown (even for a single artifact), so this is the only artifact path.</summary>
    internal event Action<Artifact>? ArtifactChosen;

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

    /// <summary>Raised when the user picks "Add note…"/"Edit note…" for a session. The app opens the note
    /// editor (prefilled from <see cref="ClaudeSession.Note"/>) and writes the result via the monitor.
    /// Internal — <see cref="ClaudeSession"/> is a Core-internal type.</summary>
    internal event Action<ClaudeSession>? NoteEditRequested;

    /// <summary>Raised when the user picks "Clear note" for a session; carries the session id for the app
    /// to delete its <c>.note</c> sidecar.</summary>
    public event Action<string>? NoteClearRequested;

    /// <summary>Raised when the user clicks the note button at the start of the quick-links row. The app
    /// opens the global scratch pad (prefilled from <c>AppSettings.ScratchText</c>) and persists it.</summary>
    public event Action? ScratchPadRequested;

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

    // Flips a session's confetti arming from the right-click menu (in-memory only). Internal (not
    // private) so the headless render harness can arm a row the same way the menu does.
    internal void ToggleConfetti(string sessionId)
    {
        if (!_confettiSessions.Remove(sessionId)) _confettiSessions.Add(sessionId);
        InvalidateVisual();
    }

    // Dwell tooltips: hovering an info glyph (thermometer / stuck-warning / task-count / metrics bars)
    // or the usage strip for ~150ms pops a hint. A single timer serves whichever the cursor last
    // settled on; moving to a different (or no) target restarts it and hides the current tip.
    private enum TipKind { None, Usage, Thermo, Warn, Task, Metrics, Note }
    private TipKind _tipKind = TipKind.None;
    private int _tipRow = -1;
    private DispatcherTimer? _dwellTimer;
    private OverlayTooltip? _tooltip;

    // A flat render row: a parent session, one of its sub-agents, or the "Autonomous" section header.
    // Depth is the sub-agent's nesting level (1 = a session's direct child, 2 = a sub-agent's own child,
    // …), driving its indent in the tree; 0 for session and section rows.
    private readonly record struct DisplayRow(ClaudeSession? Session, SubAgent? Sub, int SectionCount = -1, int Depth = 0)
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

        // Stop the attention flash once nothing needs attention or is awaiting input anymore.
        if (_attentionFlash && sessions.All(s =>
                s.Status != SessionStatus.NeedsAttention && s.Status != SessionStatus.AwaitingInput))
            StopAttention();

        UpdateTickTimer();
        RemeasurePanel();
    }

    // A session's row plus its running sub-agent / teammate subtree, walked depth-first in draw order so
    // each parent is immediately followed by its (indented) children.
    private void AddSessionRows(List<DisplayRow> rows, ClaudeSession session)
    {
        rows.Add(new DisplayRow(session, null));
        AddSubTree(rows, session, session.SubAgents, 1);
    }

    // Appends one level of the sub-agent tree, then recurses into each node's children unless the node is
    // collapsed. Ordering per level matches the flat layout it replaces: teammates first, working before
    // idle, then by name.
    private void AddSubTree(List<DisplayRow> rows, ClaudeSession session, IReadOnlyList<SubAgent> subs, int depth)
    {
        var ordered = subs
            .Where(s => !_hideInactiveTeamMembers || !(s.IsTeammate && s.IsIdle))
            .OrderByDescending(s => s.IsTeammate)
            .ThenBy(s => s.IsTeammate && s.IsIdle)
            .ThenBy(s => s.Name ?? s.Description, StringComparer.OrdinalIgnoreCase);
        foreach (var sub in ordered)
        {
            rows.Add(new DisplayRow(session, sub, Depth: depth));
            if (sub.Children.Count > 0 && !_collapsedAgents.Contains(sub.AgentId))
                AddSubTree(rows, session, sub.Children, depth + 1);
        }
    }

    // How many descendants a node hides when collapsed — the "+N" badge on its row. Counts the whole
    // subtree, honouring the same idle-teammate gate as the visible rows so the number matches.
    private int HiddenDescendantCount(SubAgent sub)
    {
        int n = 0;
        foreach (var c in sub.Children)
        {
            if (_hideInactiveTeamMembers && c.IsTeammate && c.IsIdle) continue;
            n += 1 + HiddenDescendantCount(c);
        }
        return n;
    }

    private double HeightOf(DisplayRow row) =>
        row.IsSectionHeader ? SectionRowHeight :
        row.IsSubAgent ? SubRowHeight :
        SessionRowHeight(row.Session!);

    // A session row is RowHeight for the name + one optional sub-line (activity/elapsed OR a note); it
    // grows by NoteLineHeight only when it needs *both* — so a note gets its own third line rather than
    // overriding the activity/status line.
    private double SessionRowHeight(ClaudeSession s)
    {
        var (activity, elapsed) = SecondLineContent(s);
        bool activityLine = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        return activityLine && s.HasNote && _showNoteLine ? RowHeight + NoteLineHeight : RowHeight;
    }

    // The live second-line content (activity phrase + elapsed timer) for a session, or (null, null) when
    // it has none. Single source of truth so the measured row height and the painted layout can't drift.
    private (string? activity, string? elapsed) SecondLineContent(ClaudeSession session)
    {
        bool running  = session.Status == SessionStatus.Running;
        bool awaiting = session.Status == SessionStatus.AwaitingInput && _showWaitingTimer;
        string? activity = running
            ? (session.CurrentTask?.ActiveForm is { Length: > 0 } af ? af : session.Activity)
            : awaiting ? "waiting on you" : null;
        string? elapsed  = running ? session.RunningElapsedLabel()
                         : awaiting ? session.AwaitingElapsedLabel() : null;
        return (activity, elapsed);
    }

    // The closed dense strip: the rounded panel + border (or attention chase), then the controller's
    // icon-and-status-dots strip. Mirrors the OverlayForm.OnPaint closed-strip branch.
    private double DrawStrip(DrawingContext? ctx, double width)
    {
        double h = ctx != null ? Bounds.Height : _denseCtl.StripHeightDip();
        if (ctx != null)
        {
            var pr = new Rect(0.5, 0.5, width - 1, h - 1);
            if (_attentionFlash) { OverlayDraw.Panel(ctx, pr, BgBrush, null, Corner); DrawChaseBorder(ctx, pr, AttentionColor); }
            else OverlayDraw.Panel(ctx, pr, BgBrush, BorderPen, Corner);
            _denseCtl.PaintStrip(ctx, width);
            DrawInstanceBorder(ctx, width, h);
        }
        return h;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // In dense mode the window's size is set explicitly (SizeToContent off), so the content is
        // measured to the window client size the controller placed — return that. Floating auto-sizes.
        if (_denseCtl.IsDense && double.IsFinite(availableSize.Width) && double.IsFinite(availableSize.Height)
            && availableSize is { Width: > 0, Height: > 0 })
            return availableSize;
        return new(FormWidth, Draw(null, FormWidth));
    }

    public override void Render(DrawingContext ctx) => Draw(ctx, Bounds.Width);

    // Measure-or-paint: returns the content height; paints only when ctx is non-null.
    private double Draw(DrawingContext? ctx, double width)
    {
        if (_denseCtl.IsClosedStrip) return DrawStrip(ctx, width);

        bool showRows = ShowFullPanel && _rows.Count > 0;
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
        // The outage footer sits below everything; shown only when enabled and there's an issue. Reached
        // only past the closed-strip early-return, so the header is always present to anchor it to.
        bool showFooter = ShowFooter;
        if (showFooter) height += FooterHeight;

        if (ctx != null)
        {
            // While attention is flashing, the 1px border is replaced by the animated chase (drawn under
            // the content, so the header/rows paint over its inward bloom).
            var panelRect = new Rect(0.5, 0.5, width - 1, height - 1);
            if (_attentionFlash)
            {
                OverlayDraw.Panel(ctx, panelRect, BgBrush, null, Corner);
                DrawChaseBorder(ctx, panelRect, AttentionColor);
            }
            else
            {
                OverlayDraw.Panel(ctx, panelRect, BgBrush, BorderPen, Corner);
            }
            DrawHeader(ctx, width);
            if (_autoCloseActive) DrawAutoCloseBar(ctx, width);

            if (showSys) DrawSystemMetricsStrip(ctx, width);
            if (showUsage) DrawUsageBars(ctx, width);
            if (showQuickLinks) DrawQuickLinksRow(ctx, width);
            else _noteButtonRect = default; // no row painted → drop the stale note-button hit-rect

            if (showRows)
            {
                // Glyph hit-rects are rebuilt from scratch each paint; DrawSessionRow repopulates them
                // for any row that actually shows the glyph.
                _artifactRects.Clear();
                _thermoRects.Clear();
                _warnRects.Clear();
                _taskRects.Clear();
                _metricsRects.Clear();
                _noteRects.Clear();
                _subChevronRects.Clear();

                double top = RowsTop;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    if (r.IsSectionHeader) DrawSectionHeaderRow(ctx, r, top, width);
                    else if (r.IsSubAgent) DrawSubAgentRow(ctx, i, r, top, width);
                    else DrawSessionRow(ctx, i, r.Session!, top, width);
                    top += HeightOf(r);
                }
            }

            if (showFooter) DrawStatusFooter(ctx, width, height);
            else _footerRect = default;

            DrawInstanceBorder(ctx, width, height);
        }

        return height;
    }

    // A 2px marker border hugging the panel so an isolated instance can't be confused with a running
    // installed Perch: light blue under replay (takes precedence), else hot pink for a dev build, else
    // none. Inset a touch so the stroke sits fully inside the window bounds, and painted last so it rides
    // over the content edges.
    private void DrawInstanceBorder(DrawingContext ctx, double width, double height)
    {
        var pen = ReplayMode ? ReplayBorderPen : AppProfile.IsDev ? DevBorderPen : null;
        if (pen == null) return;
        var r = new Rect(1, 1, width - 2, height - 2);
        if (r.Width <= 0 || r.Height <= 0) return;
        OverlayDraw.Panel(ctx, r, null, pen, Corner - 1);
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

        var label = ReplayMode
            ? OverlayDraw.Text("Perch - Replay", 11, ReplayBlueBrush)
            : AppProfile.IsDev
                ? OverlayDraw.Text("Perch - DEV", 11, DevPinkBrush)
                : OverlayDraw.Text("Perch", 11, MutedBrush);
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

        // Right cluster: the dense toggle glyph + (floating only) the expand chevron. The glyph points
        // along the docked edge: floating shows the arrow collapsing toward the edge, dense shows it
        // expanding inward. Clicking it enters dense from floating, or leaves it from the open popup.
        DrawSideCollapseIcon(ctx, SideIconRect(width), reversed: _denseCtl.IsDense ^ (_denseCtl.Side == DenseSide.Left));
        double clusterLeft = width - HorizPad - IconBoxW;

        if (!_denseCtl.IsDense && _sessions.Count > 0)
        {
            var chevron = OverlayDraw.Text(_expanded ? "▲" : "▼", 9, MutedBrush);
            double chevX = clusterLeft - IconGap - chevron.Width;
            OverlayDraw.TextLeftMid(ctx, chevron, chevX, midY);
            clusterLeft = chevX;
        }

        // Update badge — only while an update is pending. Drawn in the perch-logo colour so it stands out
        // from the muted glyphs; clicking it (see RouteClick) applies the update.
        if (_updateAvailable)
        {
            double ux = clusterLeft - IconGap - IconBoxW;
            double uy = (HeaderHeight - IconBoxH) / 2;
            _updateIconRect = new Rect(ux, uy, IconBoxW, IconBoxH);
            DrawUpdateIcon(ctx, _updateIconRect, _hoveredUpdateIcon);
        }
        else
        {
            _updateIconRect = default;
        }
    }

    // The update badge: a filled perch-orange disc with a white "download" arrow (a down arrow over a
    // short tray line), brightened a touch while hovered. Mirrors the WinForms OverlayForm badge.
    private static void DrawUpdateIcon(DrawingContext ctx, Rect r, bool hovered)
    {
        ctx.DrawEllipse(hovered ? UpdateHover : UpdateBrush, null, r.Center, r.Width / 2, r.Height / 2);

        var pen = new Pen(Brushes.White, 1.5, lineCap: PenLineCap.Round);
        double cx = r.Left + r.Width / 2;
        double midY = r.Top + r.Height / 2;
        double top = midY - 4, bot = midY + 2;
        ctx.DrawLine(pen, new Point(cx, top), new Point(cx, bot));                     // arrow shaft
        ctx.DrawLine(pen, new Point(cx - 3, bot - 3), new Point(cx, bot));             // arrowhead
        ctx.DrawLine(pen, new Point(cx + 3, bot - 3), new Point(cx, bot));
        ctx.DrawLine(pen, new Point(cx - 3, midY + 4), new Point(cx + 3, midY + 4));   // tray line
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

        // The note button leads the row (slot 0); the user's quick links follow (slots 1..count).
        int count = _quickLinks.Count;
        int slots = count + 1;
        double totalW = slots * IconSize + (slots - 1) * IconGap;
        double startX = (width - totalW) / 2;

        double noteX = startX;
        double iconY0 = centerY - IconSize / 2;
        if (_hoveredNoteButton)
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                new Rect(noteX - HitPad, iconY0 - HitPad, IconSize + HitPad * 2, IconSize + HitPad * 2));
        DrawNoteIcon(ctx, noteX + (IconSize - 10) / 2, centerY);
        _noteButtonRect = new Rect(noteX - HitPad, iconY0 - HitPad, IconSize + HitPad * 2, IconSize + HitPad * 2);

        for (int i = 0; i < count; i++)
        {
            double iconX = startX + (i + 1) * (IconSize + IconGap);
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
        if (!(ShowFullPanel && _rows.Count > 0 && HasQuickLinksRow)) return -1;
        double rowTop = QuickLinksTop;
        if (p.Y < rowTop || p.Y >= rowTop + QuickLinksRowHeight) return -1;

        const double IconSize = 16, IconGap = 14, HitPad = 4;
        int count = _quickLinks.Count;
        int slots = count + 1;                          // slot 0 is the note button (not a quick link)
        double totalW = slots * IconSize + (slots - 1) * IconGap;
        double startX = (Bounds.Width - totalW) / 2;

        for (int i = 0; i < count; i++)
        {
            double iconX = startX + (i + 1) * (IconSize + IconGap);
            if (p.X >= iconX - HitPad && p.X < iconX + IconSize + HitPad) return i;
        }
        return -1;
    }

    // Returns the display-row index under p, or -1 (only while the panel is expanded with rows).
    private int HitTestRow(Point p)
    {
        if (!(ShowFullPanel && _rows.Count > 0) || p.Y < RowsTop) return -1;
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
    private int HitTestNoteIcon(Point p)     => HitRect(_noteRects, p);

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
        double rowH = SessionRowHeight(session);
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        if (rowIndex == _hoveredRow)
            ctx.FillRectangle(RowHoverBrush, new Rect(1, top + 1, width - 2, rowH - 1));

        // "Jump to next session" landing highlight: a blue wash + bright left bar, fading over time.
        if (session.SessionId == _cycleHighlightId && CycleHighlightOpacity() is > 0 and var op)
        {
            var rowRect = new Rect(1, top + 1, width - 2, rowH - 1);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(46 * op), CycleColor.R, CycleColor.G, CycleColor.B)), rowRect);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb((byte)(255 * op), CycleColor.R, CycleColor.G, CycleColor.B)),
                new Rect(1, top + 1, 3, rowH - 1));
        }

        bool running = session.Status == SessionStatus.Running;
        // When the waiting timer is off, an awaiting row keeps its "input ↩" status but drops the "waiting
        // on you" activity + elapsed line (see SecondLineContent), so it renders single-line.
        var (activity, elapsed) = SecondLineContent(session);
        bool activityLine = !string.IsNullOrEmpty(activity) || !string.IsNullOrEmpty(elapsed);
        bool awaiting = session.Status == SessionStatus.AwaitingInput && _showWaitingTimer;
        // The "Session notes" indicator toggle (off by default) gates the whole inline note — the clickable
        // glyph and its text line. Off ⇒ the note is still stored and editable from the right-click menu,
        // just not shown on the row.
        bool showNote = session.HasNote && _showNoteLine;

        // Lines stack under the name: the activity/elapsed line (when present), then the note on its own
        // line. The name centres in a single-line row and rides high once anything sits beneath it.
        double firstLineY = top + 32;                                  // the activity/elapsed slot
        double noteLineY  = activityLine ? firstLineY + NoteLineHeight // note takes the 3rd line…
                                         : firstLineY;                 // …or the 2nd when there's no activity
        double nameMidY   = activityLine || showNote ? top + 15 : top + rowH / 2;

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

        // Left-of-name glyph cluster: warn, artifact, mail, remote-control, party (confetti armed), bot.
        bool stuck = _showStuckWarnings && session.IsStuck;
        double warnW = stuck ? WarnIconWidth : 0;
        bool hasArtifacts = _showArtifacts && session.HasArtifacts;
        double artW = hasArtifacts ? ArtifactIconWidth : 0;
        double partyW = ConfettiArmed(session) ? PartyIconWidth : 0;
        double mailW = session.ExternalNotify ? MailIconWidth : 0;
        double rcW   = session.RemoteControlled ? RcIconWidth : 0;
        double botW  = session.IsBackground ? BotIconWidth : 0;
        double noteW = showNote ? NoteIconWidth : 0;

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
                         - artW - warnW - thermoW - taskW - metricsW - burnW - gitW - noteW;
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
        if (partyW > 0) DrawPartyIcon(ctx, HorizPad + 14 + warnW + artW + mailW + rcW, nameMidY);
        if (botW > 0)  DrawBotIcon(ctx, HorizPad + 14 + warnW + artW + mailW + rcW + partyW, nameMidY);
        if (noteW > 0)
        {
            double noteGlyphX = HorizPad + 14 + warnW + artW + mailW + rcW + partyW + botW;
            DrawNoteIcon(ctx, noteGlyphX, nameMidY);
            _noteRects[rowIndex] = new Rect(noteGlyphX - 2, nameMidY - 9, NoteIconWidth + 2, 18);
        }

        double nameX = HorizPad + 14 + warnW + artW + mailW + rcW + partyW + botW + noteW;
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

        double lineLeft = HorizPad + 14;
        if (activityLine)
        {
            double elapsedW = 0;
            if (!string.IsNullOrEmpty(elapsed))
            {
                elapsedW = OverlayDraw.MeasureWidth(elapsed, ActivitySize);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(elapsed, ActivitySize, secondLine),
                    width - HorizPad - elapsedW, firstLineY);
            }
            if (!string.IsNullOrEmpty(activity))
            {
                double activityMax = width - lineLeft - HorizPad - (elapsedW > 0 ? elapsedW + 6 : 0);
                string actTrunc = OverlayDraw.Truncate(activity, ActivitySize, activityMax);
                OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(actTrunc, ActivitySize, secondLine),
                    lineLeft, firstLineY);
            }
        }

        // The pinned note on its own line, in the note amber — never overriding the activity/status line.
        // Shown with the glyph when the "Session notes" indicator is on.
        if (showNote)
        {
            double noteMax = width - lineLeft - HorizPad;
            string noteTrunc = OverlayDraw.Truncate(session.Note!, ActivitySize, noteMax);
            OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(noteTrunc, ActivitySize, NoteLineBrush),
                lineLeft, noteLineY);
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
    private void DrawSubAgentRow(DrawingContext ctx, int rowIndex, DisplayRow row, double top, double width)
    {
        var sub = row.Sub!;
        int depth = Math.Max(1, row.Depth);
        double midY = top + SubRowHeight / 2;

        // Tree connector: one SubIndent step of indent per nesting level, with the branch dropping from
        // the row above at the parent's marker column and turning in to this node's marker.
        double branchX = HorizPad + SubIndent * (depth - 1) + 4;
        double markerX = HorizPad + SubIndent * depth;
        ctx.DrawLine(TreeLinePen, new Point(branchX, top - SubRowHeight / 2), new Point(branchX, midY));

        bool hasChildren = sub.Children.Count > 0;
        bool collapsed = hasChildren && _collapsedAgents.Contains(sub.AgentId);
        if (hasChildren)
        {
            // A node with children carries an expand/collapse chevron on the branch, in place of the
            // horizontal stub; clicking it toggles the subtree (see RouteClick).
            var chevron = OverlayDraw.Text(collapsed ? "▸" : "▾", SectionChev, MutedBrush);
            OverlayDraw.TextLeftMid(ctx, chevron, markerX - 11, midY);
            _subChevronRects[rowIndex] = new Rect(branchX - 2, top, markerX - branchX + 8, SubRowHeight);
        }
        else
        {
            ctx.DrawLine(TreeLinePen, new Point(branchX, midY), new Point(markerX - 2, midY));
        }

        int hidden = collapsed ? HiddenDescendantCount(sub) : 0;
        if (sub.IsTeammate) DrawTeammateRow(ctx, sub, markerX, midY, width, hidden);
        else DrawPlainSubAgentRow(ctx, sub, markerX, midY, width, hidden);
    }

    // The "+N" muted hidden-count badge on a collapsed node, drawn just left of the row's status text.
    // Returns the width it reserves (badge + gap, or 0) so the label can shrink to avoid overrunning it.
    private double DrawHiddenBadge(DrawingContext ctx, int hidden, double statusW, double midY, double width)
    {
        if (hidden <= 0) return 0;
        string badge = "+" + hidden;
        double badgeW = OverlayDraw.MeasureWidth(badge, SubStatusSize) + 8;
        OverlayDraw.TextLeftMid(ctx, OverlayDraw.Text(badge, SubStatusSize, MutedBrush),
            width - HorizPad - statusW - badgeW + 2, midY);
        return badgeW;
    }

    private void DrawPlainSubAgentRow(DrawingContext ctx, SubAgent sub, double dotX, double midY, double width, int hidden)
    {
        ctx.DrawEllipse(SubAgentBrush, null, new Point(dotX + 3, midY), 3, 3);

        const string statusText = "running";
        double statusW = OverlayDraw.MeasureWidth(statusText, SubStatusSize);
        double badgeW = DrawHiddenBadge(ctx, hidden, statusW, midY, width);
        double labelX = dotX + 12;
        double labelMaxW = width - labelX - HorizPad - statusW - badgeW - 6;

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

    private void DrawTeammateRow(DrawingContext ctx, SubAgent sub, double glyphX, double midY, double width, int hidden)
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
        double badgeW = DrawHiddenBadge(ctx, hidden, stateW, midY, width);
        double labelX = glyphX + 16;
        double labelMaxW = width - labelX - HorizPad - stateW - badgeW - 6;

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
    // The "confetti finish" indicator: a little party popper — a gold cone spraying a fan of coloured
    // confetti up and to the right — drawn on a session that's armed to celebrate when it next finishes.
    private static void DrawPartyIcon(DrawingContext ctx, double x, double midY)
    {
        // Cone: a narrow gold triangle, apex at the bottom-left, mouth opening toward the upper-right.
        var cone = new StreamGeometry();
        using (var gc = cone.Open())
        {
            gc.BeginFigure(new Point(x + 2, midY + 6), isFilled: true);   // apex
            gc.LineTo(new Point(x + 11, midY - 4));                       // mouth top
            gc.LineTo(new Point(x + 6, midY + 3));                        // mouth bottom
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(PartyConeBrush, null, cone);

        // Confetti: a few small dots sprayed out from the cone's mouth, each a different festive colour.
        foreach (var (dx, dy, r, brush) in PartyBits)
            ctx.DrawEllipse(brush, null, new Point(x + dx, midY + dy), r, r);
    }

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

    // The pinned-note glyph: a small dog-eared page (folded top-right corner) with two short "text"
    // lines, in the sticky-note amber. Marks a row that carries a note; hovering it pops the full text.
    private static void DrawNoteIcon(DrawingContext ctx, double x, double midY)
    {
        var pen = new Pen(NoteBrush, 1.3, null, PenLineCap.Round, PenLineJoin.Round);
        const double w = 10, h = 12, fold = 3.5;
        double left = x, top = midY - h / 2, right = left + w, bottom = top + h;

        var page = new StreamGeometry();
        using (var gc = page.Open())
        {
            gc.BeginFigure(new Point(left, top), isFilled: false);
            gc.LineTo(new Point(right - fold, top));
            gc.LineTo(new Point(right, top + fold));
            gc.LineTo(new Point(right, bottom));
            gc.LineTo(new Point(left, bottom));
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(null, pen, page);

        // The folded corner + two text lines.
        var fold2 = new StreamGeometry();
        using (var gc = fold2.Open())
        {
            gc.BeginFigure(new Point(right - fold, top), isFilled: false);
            gc.LineTo(new Point(right - fold, top + fold));
            gc.LineTo(new Point(right, top + fold));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, fold2);
        ctx.DrawLine(pen, new Point(left + 2.5, top + 6), new Point(right - 2.5, top + 6));
        ctx.DrawLine(pen, new Point(left + 2.5, top + 9), new Point(right - 3.5, top + 9));
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
    // Hover highlight + hand cursors; the header doubles as a drag handle (manual reposition, so a click
    // still toggles expand/collapse — drag vs click is told apart by a small movement threshold, as in
    // the WinForms form). Click routing runs on release (when it wasn't a drag): artifact-open, row-focus,
    // quick-link launch, section toggle, header expand. Right-click opens the context menu.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    // Drag state. A left press in the header arms a potential drag; once the pointer moves past a small
    // threshold the gesture is handed to the OS move loop (BeginMoveDrag). A press that never moves is a
    // click, routed on release (header toggle, row focus, artifact / quick-link open).
    private bool _leftPressed;
    private bool _headerArmed;
    private bool _headerDragged;
    private Point _headerPressPoint;
    private PointerPressedEventArgs? _headerPressArgs;

    // Dense-mode drag is manual (constrained to the docked edge, vertical, with drop lanes) — the OS move
    // loop can't do that, so it can't use BeginMoveDrag. The closed strip drags from anywhere; the open
    // popup drags from its header.
    private bool _denseArmed;
    private bool _denseWasDrag;
    private PixelPoint _denseDragStartScreen;
    private int _denseStartY;

    /// <summary>Raised once when a drag finishes, so the app can follow with the screen-edge glow.</summary>
    public event Action? DragCompleted;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);

        // Dense drag: manual, constrained to the docked edge (vertical), with drop lanes for re-docking.
        if (_denseArmed)
        {
            var cur = this.PointToScreen(p);
            int dx = cur.X - _denseDragStartScreen.X, dy = cur.Y - _denseDragStartScreen.Y;
            if (!_denseWasDrag && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            {
                _denseWasDrag = true;
                _denseCtl.ShowDropZones();
            }
            if (_denseWasDrag) _denseCtl.DragVertical(_denseStartY + dy, cur);
            base.OnPointerMoved(e);
            return;
        }

        // Header press waiting to become a drag: once past the threshold, hand off to the OS move loop.
        // A borderless window can't use the title bar, so BeginMoveDrag is the cross-platform way to move
        // it — far more reliable than repositioning by hand on every pointer move.
        if (_headerArmed && !_headerDragged)
        {
            if (Math.Abs(p.X - _headerPressPoint.X) > 4 || Math.Abs(p.Y - _headerPressPoint.Y) > 4)
            {
                _headerDragged = true;
                _headerArmed = false;
                e.Pointer.Capture(null); // release our capture so the OS can drive the move
                if (OwnerWindow is { } w && _headerPressArgs is { } pa)
                {
                    w.BeginMoveDrag(pa);     // OS-native move (blocks on Windows until the button is released)
                    DragCompleted?.Invoke(); // re-home the screen-edge glow onto the overlay's new monitor
                }
            }
            base.OnPointerMoved(e);
            return;
        }
        if (_headerDragged) { base.OnPointerMoved(e); return; } // gesture owned by the OS move loop

        int row = HitTestRow(p);
        if (row != _hoveredRow) { _hoveredRow = row; InvalidateVisual(); }

        int ql = HitTestQuickLink(p);
        if (ql != _hoveredQuickLink) { _hoveredQuickLink = ql; InvalidateVisual(); }

        int art = HitTestArtifactIcon(p);
        if (art != _hoveredArtifactRow) { _hoveredArtifactRow = art; InvalidateVisual(); }

        bool overUpdate = _updateAvailable && _updateIconRect.Width > 0 && _updateIconRect.Contains(p);
        if (overUpdate != _hoveredUpdateIcon) { _hoveredUpdateIcon = overUpdate; InvalidateVisual(); }

        bool overFooter = ShowFooter && _footerRect.Width > 0 && _footerRect.Contains(p);
        if (overFooter != _hoveredFooter) { _hoveredFooter = overFooter; InvalidateVisual(); }

        bool overNote = _noteButtonRect.Width > 0 && _noteButtonRect.Contains(p);
        if (overNote != _hoveredNoteButton) { _hoveredNoteButton = overNote; InvalidateVisual(); }

        // A row's note glyph is clickable (opens its scratch pad), so it earns the hand cursor too.
        bool overRowNote = HitTestNoteIcon(p) >= 0;

        // Hand cursor over clickable glyphs (quick links + artifacts + the update badge + outage footer +
        // the scratch-pad note button + a row's note glyph); rows show only the highlight.
        Cursor = (ql >= 0 || art >= 0 || overUpdate || overFooter || overNote || overRowNote)
            ? HandCursor : Cursor.Default;

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
            HitTestNoteIcon(p)   is var no && no >= 0 ? (TipKind.Note, no) :
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
            case TipKind.Note:    ShowNoteTooltip(_tipRow);    break;
        }
    }

    private bool InUsageStrip(Point p) =>
        ShowFullPanel && _rows.Count > 0 && _usageEnabled
        && p.Y >= UsageStripTop && p.Y < UsageStripTop + UsageStripHeight;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        bool changed = _hoveredRow != -1 || _hoveredQuickLink != -1 || _hoveredArtifactRow != -1 || _hoveredUpdateIcon || _hoveredFooter || _hoveredNoteButton;
        _hoveredRow = _hoveredQuickLink = _hoveredArtifactRow = -1;
        _hoveredUpdateIcon = false;
        _hoveredFooter = false;
        _hoveredNoteButton = false;
        Cursor = Cursor.Default;
        _tipKind = TipKind.None;
        _tipRow = -1;
        _dwellTimer?.Stop();
        _tooltip?.HideTip();
        if (changed) InvalidateVisual();

        // Leaving the open dense popup starts the countdown to collapse it back to the strip — but not
        // mid-drag, where the pointer legitimately roams to another monitor's drop lane.
        if (_denseCtl.IsDense && _denseCtl.IsOpen && !_denseWasDrag)
            _denseCtl.SchedulePopupClose();

        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        // Any press that reaches the canvas while a menu is open is a click *outside* that menu — presses
        // on the menu itself land on its own popup window and never arrive here. Dismiss it. (The overlay
        // is a no-activate tool window so it never deactivates, which is what Avalonia's built-in light
        // dismiss waits for; hence we close menus ourselves.) A left press only dismisses — swallowed so it
        // doesn't also act on whatever's underneath; a right press falls through to open a fresh menu at
        // the new spot.
        if (_openFlyout is { } open)
        {
            _openFlyout = null;
            open.Hide();
            if (!props.IsRightButtonPressed) { e.Handled = true; return; }
        }

        if (props.IsRightButtonPressed)
        {
            ShowContextMenuAt(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) { base.OnPointerPressed(e); return; }

        var p = e.GetPosition(this);
        _leftPressed = true;
        _headerArmed = false;
        _headerDragged = false;
        _headerPressArgs = null;
        _denseArmed = false;
        _denseWasDrag = false;

        if (_denseCtl.IsDense)
        {
            // The closed strip is a drag handle anywhere; the open popup drags from its header. Dense drag
            // is manual (see OnPointerMoved) — arm it here, recording the window's start Y in physical px.
            if ((_denseCtl.IsClosedStrip || p.Y < HeaderHeight) && HostWindow is { } dw)
            {
                _denseArmed = true;
                _denseDragStartScreen = this.PointToScreen(p);
                _denseStartY = dw.Position.Y;
            }
            e.Pointer.Capture(this);
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        // Floating: the header is the drag handle — arm a potential OS move (started in OnPointerMoved
        // once the pointer actually moves). Keep the press args — BeginMoveDrag needs them. A press that
        // never moves falls through to RouteClick on release (header toggle).
        if (p.Y < HeaderHeight && OwnerWindow is not null)
        {
            _headerArmed = true;
            _headerPressPoint = p;
            _headerPressArgs = e;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        base.OnPointerPressed(e);
    }

    // Entering the overlay while dense pops the full panel open (any re-entry also cancels a pending
    // auto-close inside the controller).
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        if (_denseCtl.IsDense) _denseCtl.OnPointerEntered();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_leftPressed) { base.OnPointerReleased(e); return; }
        _leftPressed = false;
        e.Pointer.Capture(null);

        // Dense drag: released over another monitor's drop lane re-pins the strip there; a plain click
        // (no move) falls through to RouteClick (the dense toggle glyph, or rows in the open popup).
        if (_denseArmed)
        {
            bool wasDrag = _denseWasDrag;
            _denseArmed = false;
            _denseWasDrag = false;
            if (wasDrag) _denseCtl.PinToActiveDropZone(this.PointToScreen(e.GetPosition(this)));
            _denseCtl.HideDropZones();
            if (wasDrag) DragCompleted?.Invoke();
            else RouteClick(e.GetPosition(this));
            base.OnPointerReleased(e);
            return;
        }

        bool dragged = _headerDragged;
        _headerArmed = false;
        _headerDragged = false;
        _headerPressArgs = null;

        if (!dragged) RouteClick(e.GetPosition(this)); // a real drag already handed off to the OS

        base.OnPointerReleased(e);
    }

    // Routes a (non-drag) left click to whatever's under the point, in priority order: the artifact glyph
    // (inside a row, so checked first), a session/section row, a quick-link icon, then the header (toggle
    // expand/collapse). Mirrors the WinForms MouseUp click chain.
    private void RouteClick(Point p)
    {
        // The dense toggle glyph in the header (visible whenever the header shows, i.e. not the closed
        // strip): enter dense from floating, or leave it from the open popup.
        if (!_denseCtl.IsClosedStrip && p.Y < HeaderHeight && SideIconRect(Bounds.Width).Contains(p))
        {
            _denseCtl.Toggle();
            return;
        }

        // Update badge (header, floating): apply the pending update. Checked before the header-toggle
        // fallback below, since the badge sits inside the header band.
        if (_updateAvailable && _updateIconRect.Width > 0 && _updateIconRect.Contains(p))
        {
            UpdateRequested?.Invoke();
            return;
        }

        // Outage footer: open the incident menu (with the "open status.claude.com" link).
        if (ShowFooter && _footerRect.Width > 0 && _footerRect.Contains(p))
        {
            ShowStatusMenu();
            return;
        }

        int art = HitTestArtifactIcon(p);
        if (art >= 0 && _rows[art].Session is { } artSession)
        {
            // Always pop the picker list — even for a single artifact — so the interaction is consistent
            // (a click never silently opens a link; you always see and choose from the list).
            var arts = artSession.Artifacts;
            if (arts.Count > 0) ShowArtifactPicker(arts);
            return;
        }

        // The note glyph opens that session's scratch pad for editing.
        int note = HitTestNoteIcon(p);
        if (note >= 0 && _rows[note].Session is { } noteSession)
        {
            NoteEditRequested?.Invoke(noteSession);
            return;
        }

        // A sub-agent's expand/collapse chevron (only present on a node that has children).
        int chev = HitRect(_subChevronRects, p);
        if (chev >= 0 && _rows[chev].Sub is { } chevSub)
        {
            if (!_collapsedAgents.Remove(chevSub.AgentId))
                _collapsedAgents.Add(chevSub.AgentId);
            Update(_sessions); // rebuild the render list under the new collapse state
            return;
        }

        int row = HitTestRow(p);
        if (row >= 0)
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
            return;
        }

        // The note button leading the quick-links row opens the global scratch pad.
        if (_noteButtonRect.Width > 0 && _noteButtonRect.Contains(p))
        {
            ScratchPadRequested?.Invoke();
            return;
        }

        int ql = HitTestQuickLink(p);
        if (ql >= 0 && ql < _quickLinks.Count)
        {
            QuickLinkActivated?.Invoke(_quickLinks[ql]);
            return;
        }

        if (!_denseCtl.IsDense && p.Y < HeaderHeight && _sessions.Count > 0)
        {
            // Header click toggles expand/collapse (floating only); SizeToContent resizes to match.
            _expanded = !_expanded;
            UpdateTickTimer();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    // ── Context menu ─────────────────────────────────────────────────────────
    // Opens a right-click menu at the clicked point, with each item scoped to what's under the cursor:
    // per-session actions on a session row, strip toggles over a strip, and the header menu (the one
    // place that can bring a hidden strip back, plus Exit) over the header. No applicable item → no menu.
    private void ShowContextMenuAt(Point p)
    {
        // The outage footer has one menu regardless of button — a right-click there opens the same incident
        // menu a left-click does, rather than the per-row context menu.
        if (ShowFooter && _footerRect.Width > 0 && _footerRect.Contains(p))
        {
            ShowStatusMenu();
            return;
        }

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

            // Pinned notes apply to a real session row (not its sub-agents). Always available — notes are
            // a core, ungated feature like history/copy-id.
            if (!subRow)
            {
                items.Add(MenuItem(s.HasNote ? "Edit note…" : "Add note…", () => NoteEditRequested?.Invoke(s)));
                if (s.HasNote)
                    items.Add(MenuItem("Clear note", () => NoteClearRequested?.Invoke(s.SessionId)));
            }

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
        bool showRows = ShowFullPanel && _rows.Count > 0;
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
        ShowFlyout(items);
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // The one place every context/flyout menu is shown. Tracks the open menu in _openFlyout so a press
    // elsewhere on the overlay can close it (see OnPointerPressed) — the overlay's no-activate tool window
    // defeats Avalonia's built-in light dismiss. Opening a menu first closes any still-open one.
    private MenuFlyout? _openFlyout;

    private void ShowFlyout(IEnumerable<Control> items)
    {
        _openFlyout?.Hide();
        var flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.Pointer };
        flyout.Closed += (_, _) => { if (ReferenceEquals(_openFlyout, flyout)) _openFlyout = null; };
        _openFlyout = flyout;
        flyout.ShowAt(this, showAtPointer: true);
    }

    // Pops a small menu of a session's artifacts at the cursor; picking one opens it. Always used for the
    // artifact glyph (a single artifact shows as a one-item list), so the click behaviour is consistent.
    private void ShowArtifactPicker(IReadOnlyList<Artifact> artifacts)
    {
        var items = new List<Control>(artifacts.Count);
        foreach (var a in artifacts)
            items.Add(MenuItem(string.IsNullOrWhiteSpace(a.Title) ? a.Url : a.Title, () => ArtifactChosen?.Invoke(a)));
        ShowFlyout(items);
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

    // ── Attention chase-border animation (4.14) ───────────────────────────────
    // While active, the panel's 1px border is replaced by an animated neon "chase": a bright comet head
    // with a fading tail travels the rounded-rect perimeter over a faint always-lit outline, clipped to
    // the panel so the bloom only spills inward. _chasePhase is the head's position (0..1, wrapping),
    // advanced ~30×/s by _chaseTimer; _chaseStopTimer ends the flash after 10s.
    private bool _attentionFlash;
    private double _chasePhase;
    private const double ChaseStep = 0.02; // head advance per tick → ~1.7s per lap at 33ms
    private DispatcherTimer? _chaseTimer;
    private DispatcherTimer? _chaseStopTimer;

    /// <summary>Flashes the attention chase-border and, if collapsed, expands the panel so the session
    /// that needs attention is visible. Called on the UI thread when a session finishes or blocks. The
    /// flash self-stops after ~10s, or as soon as nothing needs attention (see <see cref="Update"/>).</summary>
    public void TriggerAttention()
    {
        // Surface the session that needs attention: in dense mode pop the hover panel open; floating,
        // expand if collapsed.
        if (_denseCtl.IsDense) _denseCtl.OpenPopup();
        else if (!_expanded && _rows.Count > 0)
        {
            _expanded = true;
            UpdateTickTimer();
            InvalidateMeasure();
        }

        _attentionFlash = true;

        (_chaseTimer ??= CreateChaseTimer()).Start();

        _chaseStopTimer ??= CreateChaseStopTimer();
        _chaseStopTimer.Stop();
        _chaseStopTimer.Start();

        InvalidateVisual();
    }

    private DispatcherTimer CreateChaseTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) => { _chasePhase += ChaseStep; InvalidateVisual(); };
        return t;
    }

    private DispatcherTimer CreateChaseStopTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        t.Tick += (_, _) => StopAttention();
        return t;
    }

    private void StopAttention()
    {
        _chaseTimer?.Stop();
        _chaseStopTimer?.Stop();
        if (!_attentionFlash) return;
        _attentionFlash = false;
        InvalidateVisual();
    }

    // ── "Jump to next session" landing highlight ───────────────────────────────
    // The session the cycle hotkey last jumped to, plus when it landed. The row is washed blue with a
    // bright left bar that holds full then fades, so the user can see where the cycle put them. Reset on
    // each press, so rapid cycling keeps a stable marker on the current target that lingers, then fades.
    private string? _cycleHighlightId;
    private DateTime _cycleHighlightStart;
    private DispatcherTimer? _cycleTimer;
    private const double CycleHoldMs = 2200;
    private const double CycleFadeMs = 900;

    /// <summary>Marks <paramref name="sessionId"/> as the session the "jump to next session" hotkey just
    /// focused, briefly highlighting its row. Surfaces the panel first (pops the dense popup or expands a
    /// collapsed float) so the highlight is actually visible. Called on the UI thread from the App.</summary>
    public void HighlightCycledSession(string sessionId)
    {
        if (_denseCtl.IsDense) _denseCtl.OpenPopup();
        else if (!_expanded && _rows.Count > 0)
        {
            _expanded = true;
            UpdateTickTimer();
            InvalidateMeasure();
        }

        _cycleHighlightId = sessionId;
        _cycleHighlightStart = DateTime.Now;
        (_cycleTimer ??= CreateCycleTimer()).Stop();
        _cycleTimer.Start();
        InvalidateVisual();
    }

    private DispatcherTimer CreateCycleTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        t.Tick += (_, _) =>
        {
            if ((DateTime.Now - _cycleHighlightStart).TotalMilliseconds >= CycleHoldMs + CycleFadeMs)
            {
                _cycleHighlightId = null;
                _cycleTimer?.Stop();
            }
            InvalidateVisual();
        };
        return t;
    }

    // Opacity (0..1) of the landing highlight: full through the hold window, then a linear fade out.
    private double CycleHighlightOpacity()
    {
        if (_cycleHighlightId is null) return 0;
        double elapsed = (DateTime.Now - _cycleHighlightStart).TotalMilliseconds;
        if (elapsed <= CycleHoldMs) return 1;
        return Math.Clamp(1 - (elapsed - CycleHoldMs) / CycleFadeMs, 0, 1);
    }

    // Stop the animation timers if the canvas leaves the visual tree (belt-and-braces; the overlay
    // normally lives for the whole app session).
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _chaseTimer?.Stop();
        _chaseStopTimer?.Stop();
        _cycleTimer?.Stop();
        _dwellTimer?.Stop();
        _tickTimer?.Stop();
        _autoCloseBarTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // ── Auto-close countdown + per-second tick (4.15) ──────────────────────────
    // A per-second repaint keeps the running-elapsed / waiting-on-you labels current between scans; it
    // runs only while the panel is expanded with a session that actually shows a live label.
    private DispatcherTimer? _tickTimer;

    // Auto-close: an auto-started tray whose last session ended shows a quietly depleting bar over the
    // header edge for the grace period, then the app exits. _autoCloseEnds is when the bar empties.
    private DispatcherTimer? _autoCloseBarTimer;
    private bool _autoCloseActive;
    private DateTime _autoCloseEnds;
    private int _autoCloseDurationMs;

    /// <summary>Begins the auto-close countdown indicator: a depleting bar due <paramref name="durationMs"/>
    /// from now. Idempotent — the app arms it once when sessions hit zero, so it won't reset mid-count.</summary>
    public void StartAutoCloseCountdown(int durationMs)
    {
        _autoCloseActive = true;
        _autoCloseDurationMs = durationMs;
        _autoCloseEnds = DateTime.Now.AddMilliseconds(durationMs);
        _autoCloseBarTimer ??= CreateAutoCloseBarTimer();
        if (!_autoCloseBarTimer.IsEnabled) _autoCloseBarTimer.Start();
        InvalidateVisual();
    }

    /// <summary>Hides the countdown bar (a session reappeared, or the conditions no longer hold).</summary>
    public void CancelAutoCloseCountdown()
    {
        if (!_autoCloseActive && (_autoCloseBarTimer is null || !_autoCloseBarTimer.IsEnabled)) return;
        _autoCloseActive = false;
        _autoCloseBarTimer?.Stop();
        InvalidateVisual();
    }

    // Repaints the bar every 50ms while active; stops itself once the deadline passes (the app's grace
    // timer fires the exit at that point and tears the window down).
    private DispatcherTimer CreateAutoCloseBarTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) =>
        {
            if (!_autoCloseActive || DateTime.Now >= _autoCloseEnds) _autoCloseBarTimer?.Stop();
            InvalidateVisual();
        };
        return t;
    }

    // Starts/stops the per-second label tick to match what's on screen: a running session's elapsed run
    // time or a blocked session's "waiting on you" timer, and only while the panel is expanded.
    private void UpdateTickTimer()
    {
        bool need = ShowFullPanel && _sessions.Any(s =>
            s.Status == SessionStatus.Running
            || (_showWaitingTimer && s.Status == SessionStatus.AwaitingInput));
        _tickTimer ??= CreateTickTimer();
        if (need && !_tickTimer.IsEnabled) _tickTimer.Start();
        else if (!need && _tickTimer.IsEnabled) _tickTimer.Stop();
    }

    private DispatcherTimer CreateTickTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => InvalidateVisual();
        return t;
    }

    // A thin bar hugging the top edge that shrinks from full to empty over the grace period, quietly
    // hinting when the window will close itself. Drawn in the muted idle grey so it stays unobtrusive;
    // harmless once the deadline has passed (renders empty) until the window is torn down.
    private void DrawAutoCloseBar(DrawingContext ctx, double width)
    {
        double remaining = (_autoCloseEnds - DateTime.Now).TotalMilliseconds;
        double frac = _autoCloseDurationMs > 0 ? Math.Clamp(remaining / _autoCloseDurationMs, 0, 1) : 0;

        const double TrackH = 3, Y = 4;
        double left = HorizPad, right = width - HorizPad;
        double fillW = Math.Round((right - left) * frac);
        if (fillW > 0)
            OverlayDraw.Pill(ctx, new SolidColorBrush(IdleColor), new Rect(left, Y, fillW, TrackH));
    }

    // ── Service status footer ─────────────────────────────────────────────────
    // A slim severity-tinted band across the panel bottom (rounded to match the panel's bottom corners),
    // with a status dot, the status-page description, and a chevron hinting it's clickable. Its hit-rect
    // is captured for the click → incident menu (see RouteClick / ShowStatusMenu).
    private void DrawStatusFooter(DrawingContext ctx, double width, double panelHeight)
    {
        double top = panelHeight - FooterHeight;
        _footerRect = new Rect(0, top, width, FooterHeight);

        var color = StatusColor(_status.Level);

        // Severity-tinted fill (brighter on hover). Clipped to the panel's rounded rect so the band's
        // bottom corners follow the panel edge instead of squaring off past it.
        var panelRect = new Rect(0.5, 0.5, width - 1, panelHeight - 1);
        using (ctx.PushClip(new RoundedRect(panelRect, Corner)))
            ctx.FillRectangle(new SolidColorBrush(color, _hoveredFooter ? 0.30 : 0.18),
                new Rect(1, top, width - 2, FooterHeight));

        // Hairline rule separating the band from whatever's above it.
        ctx.DrawLine(SepPen, new Point(HorizPad, top), new Point(width - HorizPad, top));

        double midY = top + FooterHeight / 2;
        double x = HorizPad;
        ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(x + 3, midY), 3, 3);
        x += 12;

        // Chevron on the right hints the click target; reserve its width so the text truncates before it.
        var chevron = OverlayDraw.Text("›", 12, new SolidColorBrush(color), FontWeight.Bold);
        double chevX = width - HorizPad - chevron.Width;

        string text = string.IsNullOrWhiteSpace(_status.Description) ? "Service issue" : _status.Description;
        var label = OverlayDraw.Text(OverlayDraw.Truncate(text, StatusSize, chevX - x - 6, FontWeight.Bold),
            StatusSize, new SolidColorBrush(color), FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, label, x, midY);
        OverlayDraw.TextLeftMid(ctx, chevron, chevX, midY);
    }

    private static Color StatusColor(StatusLevel level) => level switch
    {
        StatusLevel.Minor       => FooterMinorColor,
        StatusLevel.Maintenance => FooterMaintColor,
        StatusLevel.None        => MutedColor,
        _                       => FooterMajorColor, // Major / Critical
    };

    // Pops the outage menu at the footer: the overall description (as a disabled header), one entry per
    // unresolved incident (opening its status-page link), then a final "Open status.claude.com". Mirrors
    // the artifact picker's flyout.
    private void ShowStatusMenu()
    {
        var items = new List<Control>();

        if (!string.IsNullOrWhiteSpace(_status.Description))
            items.Add(new MenuItem { Header = _status.Description, IsEnabled = false });

        foreach (var inc in _status.Incidents)
        {
            string header = inc.Impact is "minor" or "major" or "critical"
                ? $"{char.ToUpperInvariant(inc.Impact[0])}{inc.Impact[1..]} — {inc.Name}"
                : inc.Name;
            if (header.Length > 72) header = header[..71].TrimEnd() + "…";
            string url = inc.Url;
            items.Add(MenuItem(header, () => OpenUrl(url)));
        }

        if (items.Count > 0) items.Add(new Separator());
        items.Add(MenuItem("Open status.claude.com", () => OpenUrl(_status.PageUrl)));

        ShowFlyout(items);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort — no default browser, etc. */ }
    }

    // Draws the attention border as an animated neon "chase": a bright comet head with a trailing tail
    // travels the rounded-rect perimeter over a faintly-lit base outline, with a few progressively wider/
    // fainter passes for a soft glow. Everything is clipped to the panel so the bloom only spills inward —
    // the crisp outer edge stays the filled rounded-rect boundary. Mirrors the WinForms DrawChaseBorder.
    private void DrawChaseBorder(DrawingContext ctx, Rect rect, Color color)
    {
        double radius = Math.Min(Corner, Math.Min(rect.Width, rect.Height) / 2);
        var pts = RoundedRectSamples(rect, radius, out var clip);
        int samples = pts.Length;
        if (samples < 2)
        {
            ctx.DrawRectangle(null, new Pen(new ImmutableSolidColorBrush(color), 1.5), new RoundedRect(rect, radius));
            return;
        }

        double head = _chasePhase - Math.Floor(_chasePhase); // 0..1 travelling comet head
        const double TailLen = 0.55;                         // comet tail, as a fraction of the loop
        const double BaseA   = 42;                           // faint always-lit outline
        const double PeakA   = 213;                          // extra brightness at the head

        // Comet intensity at an arc fraction p: brightest at the head, fading along the tail behind it.
        double Intensity(double p)
        {
            double dd = head - p;
            dd -= Math.Floor(dd);        // distance behind the head, 0..1 (wrapped)
            double t = 1 - dd / TailLen; // 1 at the head → 0 at the tail's end
            return t <= 0 ? 0 : t * t;
        }

        // Clip to the panel so the wide bloom passes glow inward only.
        using (ctx.PushGeometryClip(clip))
        {
            // Widest & faintest first so a bright, near-white core sits on a soft coloured halo.
            (double w, double aMul)[] passes = { (7, 0.10), (4, 0.22), (2.2, 0.5), (1.4, 1.0) };
            foreach (var (w, aMul) in passes)
            {
                for (int k = 0; k < samples; k++)
                {
                    double inten = Intensity((k + 0.5) / samples);
                    int a = (int)Math.Clamp((BaseA + inten * PeakA) * aMul, 0, 255);
                    if (a <= 1) continue;
                    // Heat the colour toward white near the head for the neon "hot core" look.
                    Color c = inten > 0.05 ? Palette.Blend(color, Colors.White, (float)(inten * 0.5)) : color;
                    var pen = new Pen(new ImmutableSolidColorBrush(Color.FromArgb((byte)a, c.R, c.G, c.B)), w,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                    ctx.DrawLine(pen, pts[k], pts[(k + 1) % samples]);
                }
            }
        }
    }

    // Resamples the rounded-rect perimeter into evenly-spaced points (constant-rate motion everywhere,
    // corners and sides alike) and hands back a closed geometry of the same outline for clipping. Builds
    // a dense polyline (straight edges + ~1px-resolution corner arcs), then walks its cumulative arc
    // length to place uniform samples — the DrawingContext analogue of WinForms' Flatten + resample.
    private static Point[] RoundedRectSamples(Rect r, double radius, out Geometry clip)
    {
        double x = r.X, y = r.Y, w = r.Width, h = r.Height, rad = radius;
        var dense = new List<Point>();

        void Arc(double cx, double cy, double startDeg, double endDeg)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(rad));
            for (int i = 0; i <= steps; i++)
            {
                double deg = startDeg + (endDeg - startDeg) * i / steps;
                double a = deg * Math.PI / 180.0;
                dense.Add(new Point(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a)));
            }
        }

        // Clockwise from the top edge (y-down screen space): top → TR → right → BR → bottom → BL → left → TL.
        dense.Add(new Point(x + rad, y));
        dense.Add(new Point(x + w - rad, y));
        Arc(x + w - rad, y + rad, -90, 0);
        dense.Add(new Point(x + w, y + rad));
        dense.Add(new Point(x + w, y + h - rad));
        Arc(x + w - rad, y + h - rad, 0, 90);
        dense.Add(new Point(x + w - rad, y + h));
        dense.Add(new Point(x + rad, y + h));
        Arc(x + rad, y + h - rad, 90, 180);
        dense.Add(new Point(x, y + h - rad));
        dense.Add(new Point(x, y + rad));
        Arc(x + rad, y + rad, 180, 270);

        // Closed geometry over the same outline, for clipping.
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(dense[0], isFilled: true);
            for (int i = 1; i < dense.Count; i++) gc.LineTo(dense[i]);
            gc.EndFigure(true);
        }
        clip = geo;

        int m = dense.Count;
        var cum = new double[m + 1];
        for (int i = 0; i < m; i++)
        {
            var a = dense[i];
            var b = dense[(i + 1) % m];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            cum[i + 1] = cum[i] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[m];
        if (total <= 0) return [];

        int samples = Math.Clamp((int)(total / 3.0), 96, 1024);
        var pts = new Point[samples];
        int seg = 0;
        for (int k = 0; k < samples; k++)
        {
            double target = total * k / samples;
            while (seg < m && cum[seg + 1] < target) seg++;
            var a = dense[seg % m];
            var b = dense[(seg + 1) % m];
            double segLen = cum[seg + 1] - cum[seg];
            double t = segLen > 0 ? (target - cum[seg]) / segLen : 0;
            pts[k] = new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
        return pts;
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

    private void ShowNoteTooltip(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Session?.Note is not { } note) return;
        if (!_noteRects.TryGetValue(row, out var r)) return;
        Tooltip().ShowText(note, ToScreen(r.Left, r.Bottom + 4));
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
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"))); }
        catch { return null; }
    }
}
