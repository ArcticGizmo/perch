using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Windows;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>Which screen edge the dense strip docks to (the Avalonia port of the WinForms enum).</summary>
internal enum DenseSide { Left, Right }

/// <summary>
/// The slim surface <see cref="DenseController"/> drives so it can own dense mode without owning the
/// whole overlay — the Avalonia counterpart of the WinForms <c>IDenseHost</c>. Geometry is expressed in
/// two coordinate systems: the window position is physical pixels (matching <see cref="Screen.WorkingArea"/>
/// and <see cref="Window.Position"/>), while sizes are DIPs (matching <see cref="Window.Width"/>). The
/// rest are the few overlay internals the dense logic needs.
/// </summary>
internal interface IDenseHost
{
    Screens? Screens { get; }                 // to resolve which monitor the strip docks to
    PixelPoint WindowPosition { get; set; }   // physical top-left on the virtual desktop
    double WindowScaling { get; }             // DIP → physical factor of the window's current screen

    /// <summary>Places the overlay window: physical top-left + DIP client size (turns SizeToContent off).</summary>
    void PlaceWindow(PixelPoint position, double dipWidth, double dipHeight);
    /// <summary>Restores floating auto-sizing (SizeToContent height, fixed width) at the given position.</summary>
    void RestoreFloating(PixelPoint position);

    double FullPanelWidthDip { get; }         // the floating / open-popup width
    double FullPanelHeightDip { get; }        // header + strips + rows
    IReadOnlyList<ClaudeSession> Sessions { get; }
    Bitmap? Icon { get; }

    void RelayoutWindow();
    void UpdateTickTimer();
    void ClearRowHover();
    void HideTooltips();
    void Invalidate();
}

/// <summary>
/// Owns "dense mode": the alternate, out-of-the-way presentation where the overlay shrinks to a slim
/// strip hugging a screen edge and expands to the full panel on hover. Port of the WinForms
/// <c>DenseModeController</c>: the whole dense state machine (whether dense is on, whether the hover popup
/// is open, the remembered strip Y, which screen edge/monitor it docks to, the floating position to
/// restore on exit) plus the drag-to-redock drop lanes and the closed-strip painting. The owning
/// <see cref="OverlayCanvas"/> drives it through <see cref="IDenseHost"/>.
/// </summary>
internal sealed class DenseController : IDisposable
{
    // Dense mode's first-time default sits well below the working-area top (4× the floating gap) so the
    // slim right-edge strip doesn't overlay application close icons. (DIP; scaled per screen.)
    private const double DenseTopGapDefault = 64;

    // A narrow strip hugging a screen edge that expands on hover. All DIPs.
    private const double DenseClosedWidth = 44;
    private const double DenseTopPad      = 8;
    private const double DenseIconSize    = 22;
    private const double DenseGap         = 6;
    private const double DenseRowHeight   = 22;
    private const double DenseBottomPad   = 8;

    private readonly IDenseHost _host;

    // The session-status dot colours, top-to-bottom display order, supplied by the host so the strip's
    // counts match the rest of the overlay exactly.
    private readonly Color _running, _awaiting, _attention, _apiError, _idle;

    // _dense toggles the whole mode; _denseOpen is the hover-expanded popup within it. Floating and dense
    // each keep their own position: _floatingLoc holds the floating position (physical) while we're in
    // dense mode, and _denseY holds the dense strip's Y (physical; its X is locked to the docked edge).
    private bool  _dense;
    private bool  _denseOpen;
    private int   _denseY;
    private bool  _denseYInit;
    private PixelPoint _floatingLoc;

    // The dense strip's Y remembered corner-relative (which vertical edge + DIP distance from it), the
    // vertical twin of the floating window's _effectiveFloating. _denseY is a concrete physical Y for the
    // current screen; this survives a resolution/work-area change so OnScreensChanged can re-derive _denseY
    // and keep the strip the same distance from its anchored edge instead of drifting. Updated on every
    // gesture that sets _denseY (seed, first entry, vertical drag, drop-zone re-pin).
    private VAnchor _denseVAnchor = VAnchor.Top;
    private double  _denseVOffsetDip = DenseTopGapDefault;

    private DenseSide _denseSide = DenseSide.Right;

    // Which monitor the dense strip docks to, identified by its physical bounds so a stale reference can't
    // pin us to a disconnected monitor. Null means the primary screen.
    private PixelRect? _denseScreenBounds;

    // The user-defined initial dense placement (from the placement editor), seeded before the window is
    // shown. Its edge/monitor are applied immediately; its vertical offset is resolved into _denseY the
    // first time dense mode is entered (when a live screen + scale are available). Null keeps the default.
    private OverlayPlacement? _seededPlacement;

    private readonly List<DenseDropZoneWindow> _dropZones = [];
    private DenseDropZoneWindow? _activeDropZone;

    // Collapses the hover-opened dense popup once the pointer has been away for 750ms. Pointer re-entry
    // (OnPointerEntered → OnMouseEntered) cancels it, so a quick out-and-back keeps it open.
    private readonly DispatcherTimer _closeTimer;

    public DenseController(IDenseHost host, Color running, Color awaiting, Color attention, Color apiError, Color idle)
    {
        _host      = host;
        _running   = running;
        _awaiting  = awaiting;
        _attention = attention;
        _apiError  = apiError;
        _idle      = idle;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); if (_dense && _denseOpen) ClosePopup(); };
    }

    // ── State exposed to the overlay ────────────────────────────────────────────
    public bool      IsDense       => _dense;
    public bool      IsOpen        => _denseOpen;
    public bool      IsClosedStrip => _dense && !_denseOpen;   // strip visible, popup not open
    public DenseSide Side          => _denseSide;

    // Is the full session body currently on screen in dense mode (the hover-opened popup)?
    public bool ShowsFullPanel => _dense && _denseOpen;

    // ── Geometry ──────────────────────────────────────────────────────────────
    // Sizes and positions the window for the current dense state: the closed strip or the open popup,
    // docked to the remembered edge at the remembered Y. Called from the host's RelayoutWindow.
    public void ApplyGeometry()
    {
        var screen = DenseScreen();
        var wa = screen.WorkingArea;
        double scale = screen.Scaling;
        double dipW = _denseOpen ? _host.FullPanelWidthDip : DenseClosedWidth;
        double dipH = _denseOpen ? _host.FullPanelHeightDip : StripHeightDip();
        int physW = (int)(dipW * scale), physH = (int)(dipH * scale);
        _host.PlaceWindow(new PixelPoint(DenseX(physW, wa), ClampDenseY(_denseY, physH, wa)), dipW, dipH);
    }

    // The closed dense strip's DIP size, for the placement editor's mock (width is the fixed strip width).
    public (double W, double H) StripSizeDip() => (DenseClosedWidth, StripHeightDip());

    // The built-in default dense placement: docked to the right edge, DenseTopGapDefault below the top.
    public static OverlayPlacement DefaultPlacement() =>
        new() { HAnchor = HAnchor.Right, VAnchor = VAnchor.Top, OffsetY = DenseTopGapDefault };

    // Height (DIP) of the closed dense strip: the icon plus one row per non-zero status.
    public double StripHeightDip()
    {
        int visible = StatusCounts().Count(c => c.count > 0);
        double h = DenseTopPad + DenseIconSize;
        if (visible > 0) h += DenseGap + visible * DenseRowHeight;
        return h + DenseBottomPad;
    }

    // Keeps the dense strip fully on screen vertically as its height changes with the session count.
    private static int ClampDenseY(int y, int physHeight, PixelRect wa) =>
        Math.Clamp(y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - physHeight));

    // Physical X that hugs the docked edge: flush left, or flush right accounting for width.
    private int DenseX(int physWidth, PixelRect wa) =>
        _denseSide == DenseSide.Left ? wa.X : wa.X + wa.Width - physWidth;

    // Resolves the monitor the dense strip docks to. If the remembered monitor is gone, it self-heals by
    // forgetting it and falling back to the primary screen.
    private Screen DenseScreen()
    {
        var screens = _host.Screens;
        if (screens is null) throw new InvalidOperationException("dense mode needs a shown window");
        if (_denseScreenBounds is { } b)
        {
            foreach (var s in screens.All)
                if (s.Bounds == b) return s;
            _denseScreenBounds = null;  // monitor vanished — reset to primary
        }
        return screens.Primary ?? screens.All[0];
    }

    // ── Transitions ─────────────────────────────────────────────────────────────
    public void Toggle() { if (_dense) Exit(); else Enter(); }

    // Seeds the user-defined initial dense placement before the window is shown. The edge and monitor
    // apply straight away (pure data); the vertical position is resolved lazily on first Enter, when a
    // live screen and scale exist. Null leaves the built-in default (right edge, primary, top-gap Y).
    public void SeedPlacement(OverlayPlacement? p) => AdoptPlacement(p, reposition: false);

    // Live adoption from the placement editor: like SeedPlacement, but re-derives the strip Y from the new
    // placement (or default) and moves the strip immediately when dense mode is currently showing.
    public void ApplyPlacement(OverlayPlacement? p) => AdoptPlacement(p, reposition: true);

    private void AdoptPlacement(OverlayPlacement? p, bool reposition)
    {
        _seededPlacement = p;
        _denseSide = p is { HAnchor: HAnchor.Left } ? DenseSide.Left : DenseSide.Right;
        _denseScreenBounds = ScreenBoundsOf(p);
        if (!reposition) return;

        _denseYInit = false; // re-derive Y from the new placement (or default) on next entry
        if (_dense)
        {
            _denseY = SeedDenseVertical(DenseScreen());
            _denseYInit = true;
            _host.RelayoutWindow();
            _host.Invalidate();
        }
    }

    private static PixelRect? ScreenBoundsOf(OverlayPlacement? p) =>
        p is { MonitorX: { } mx, MonitorY: { } my, MonitorW: { } mw, MonitorH: { } mh }
            ? new PixelRect(mx, my, mw, mh)
            : null;

    // Sets the remembered corner-relative vertical (anchored edge + DIP offset) from the seeded placement, or
    // the built-in default when none, then returns the concrete physical Y for the given screen.
    private int SeedDenseVertical(Screen s)
    {
        if (_seededPlacement is { } sp) { _denseVAnchor = sp.VAnchor; _denseVOffsetDip = sp.OffsetY; }
        else { _denseVAnchor = VAnchor.Top; _denseVOffsetDip = DenseTopGapDefault; }
        return DeriveDenseY(s);
    }

    // Physical Y from the remembered corner-relative vertical: its DIP offset from the anchored (top/bottom)
    // work-area edge, using the current strip height, clamped on-screen. Mirrors PlacementMath's vertical
    // axis (X is edge-locked in dense mode, so only Y is derived here).
    private int DeriveDenseY(Screen s)
    {
        var wa = s.WorkingArea;
        double scale = s.Scaling;
        int physH = (int)(StripHeightDip() * scale);
        int offY = (int)Math.Round(_denseVOffsetDip * scale);
        int y = _denseVAnchor == VAnchor.Top ? wa.Y + offY : wa.Y + wa.Height - physH - offY;
        return ClampDenseY(y, physH, wa);
    }

    // Records the current _denseY as a corner-relative vertical (nearest vertical edge + DIP distance from it)
    // after a drag or drop-zone re-pin, so a later screen change re-derives the same distance instead of a
    // stale absolute Y. The vertical twin of the floating window's CaptureFloatingPlacement.
    private void CaptureDenseVertical(Screen s)
    {
        var wa = s.WorkingArea;
        double scale = s.Scaling;
        int physH = (int)(StripHeightDip() * scale);
        int distTop = _denseY - wa.Y;
        int distBottom = wa.Y + wa.Height - (_denseY + physH);
        if (distTop <= distBottom) { _denseVAnchor = VAnchor.Top; _denseVOffsetDip = Math.Max(0, distTop) / scale; }
        else { _denseVAnchor = VAnchor.Bottom; _denseVOffsetDip = Math.Max(0, distBottom) / scale; }
    }

    private void Enter()
    {
        if (_dense) return;
        _floatingLoc = _host.WindowPosition;            // remember where floating lives
        if (!_denseYInit)
        {
            _denseY = SeedDenseVertical(DenseScreen());
            _denseYInit = true;
        }
        _dense = true;
        _denseOpen = false;
        _host.ClearRowHover();
        _host.HideTooltips();
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    private void Exit()
    {
        if (!_dense) return;
        _dense = false;
        _denseOpen = false;
        _closeTimer.Stop();
        _host.HideTooltips();
        _host.RestoreFloating(_floatingLoc);            // restore the floating position + auto-size
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    public void OpenPopup()
    {
        if (!_dense || _denseOpen) return;
        _denseOpen = true;
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    // Collapse the hover/attention popup back to the closed strip without leaving dense mode. The
    // dense-toggle hotkey uses this to dismiss a preview that popped open (e.g. when a session needs
    // attention) rather than exiting dense outright.
    public void ClosePreview() => ClosePopup();

    private void ClosePopup()
    {
        if (!_dense || !_denseOpen) return;
        _denseOpen = false;
        _closeTimer.Stop();
        _host.ClearRowHover();
        _host.HideTooltips();
        _host.RelayoutWindow();
        _host.UpdateTickTimer();
        _host.Invalidate();
    }

    // ── Pointer / hover plumbing the overlay forwards ─────────────────────────────
    // Hovering the strip pops the full panel open; any re-entry cancels a pending auto-close.
    public void OnPointerEntered()
    {
        _closeTimer.Stop();
        OpenPopup();
    }

    // Begin the countdown that collapses the open popup back to the strip. The overlay gates this on the
    // popup being open and no drag in progress.
    public void SchedulePopupClose() { _closeTimer.Stop(); _closeTimer.Start(); }

    // Re-evaluate docking when a monitor is added/removed or a resolution/work-area changes. DenseScreen
    // self-heals to primary if the docked monitor vanished; re-deriving _denseY from the remembered
    // corner-relative vertical keeps the strip the same distance from its anchored edge across the change
    // (a bare relayout would leave a bottom-anchored or origin-shifted strip drifted).
    public void OnScreensChanged()
    {
        if (!_denseYInit && !_dense) return; // nothing positioned yet; the first Enter will seed it
        if (_host.Screens is null) return;
        _denseY = DeriveDenseY(DenseScreen());
        if (_dense) { _host.RelayoutWindow(); _host.Invalidate(); }
    }

    // ── Drag to re-dock ───────────────────────────────────────────────────────────
    // While dragging the dense strip, every monitor gets a left- and a right-edge drop lane; releasing over
    // one re-pins the strip to that monitor and edge. The lane matching the current dock is skipped.
    public void ShowDropZones()
    {
        if (_dropZones.Count > 0 || _host.Screens is not { } screens) return;
        var current = DenseScreen();
        foreach (var s in screens.All)
            foreach (var side in (ReadOnlySpan<DenseSide>)[DenseSide.Left, DenseSide.Right])
            {
                if (s.Bounds == current.Bounds && side == _denseSide) continue;
                var zone = new DenseDropZoneWindow(s, side);
                _dropZones.Add(zone);
                zone.Show();
            }
    }

    // Dense stays hugging the current monitor's docked edge, moving only vertically; drop lanes let it be
    // re-pinned on release. <paramref name="targetTop"/> is the proposed strip Y (physical);
    // <paramref name="cursorScreen"/> drives the active drop-lane highlight.
    public void DragVertical(int targetTop, PixelPoint cursorScreen)
    {
        var screen = DenseScreen();
        var wa = screen.WorkingArea;
        double scale = screen.Scaling;
        int physW = (int)((_denseOpen ? _host.FullPanelWidthDip : DenseClosedWidth) * scale);
        int physH = (int)((_denseOpen ? _host.FullPanelHeightDip : StripHeightDip()) * scale);
        int newY = ClampDenseY(targetTop, physH, wa);
        _host.WindowPosition = new PixelPoint(DenseX(physW, wa), newY);
        _denseY = newY;
        CaptureDenseVertical(screen); // remember the dragged height corner-relative for later screen changes
        UpdateActiveDropZone(cursorScreen);
    }

    private void UpdateActiveDropZone(PixelPoint screenPt)
    {
        DenseDropZoneWindow? hit = null;
        foreach (var z in _dropZones)
            if (z.ContainsScreenPoint(screenPt)) { hit = z; break; }

        if (hit == _activeDropZone) return;
        _activeDropZone?.SetActive(false);
        _activeDropZone = hit;
        _activeDropZone?.SetActive(true);
    }

    public void HideDropZones()
    {
        foreach (var z in _dropZones) z.Close();
        _dropZones.Clear();
        _activeDropZone = null;
    }

    // Re-pins the dense strip to the monitor and edge whose drop lane the pointer was released over,
    // dropping it at the release height (clamped onto that monitor). No-op if nothing was hovered.
    public void PinToActiveDropZone(PixelPoint releaseScreen)
    {
        if (_activeDropZone is null) return;
        var screen = _activeDropZone.TargetScreen;
        _denseScreenBounds = screen.Bounds;
        _denseSide = _activeDropZone.Side;
        double scale = screen.Scaling;
        int physH = (int)((_denseOpen ? _host.FullPanelHeightDip : StripHeightDip()) * scale);
        _denseY = ClampDenseY(releaseScreen.Y - physH / 2, physH, screen.WorkingArea);
        _denseYInit = true;
        CaptureDenseVertical(screen); // remember the re-pinned height corner-relative on the new monitor
        _host.RelayoutWindow();
        _host.Invalidate();
    }

    // ── Painting ────────────────────────────────────────────────────────────────
    // The statuses in top-to-bottom display order, paired with their dot colour.
    private (Color color, int count)[] StatusCounts() =>
    [
        (_running,   _host.Sessions.Count(s => s.Status == SessionStatus.Running)),
        (_awaiting,  _host.Sessions.Count(s => s.Status == SessionStatus.AwaitingInput)),
        (_attention, _host.Sessions.Count(s => s.Status == SessionStatus.NeedsAttention)),
        (_apiError,  _host.Sessions.Count(s => s.Status == SessionStatus.ApiError)),
        (_idle,      _host.Sessions.Count(s => s.Status == SessionStatus.Idle)),
    ];

    // The closed dense view: the perch icon, then one centered "dot + count" row for each status that has
    // at least one session. With no sessions at all, only the icon shows. Drawn in DIPs.
    public void PaintStrip(DrawingContext ctx, double clientWidthDip)
    {
        double cx = clientWidthDip / 2;

        if (_host.Icon is { } icon)
            ctx.DrawImage(icon, new Rect(cx - DenseIconSize / 2, DenseTopPad, DenseIconSize, DenseIconSize));

        const double dot = 8;
        double y = DenseTopPad + DenseIconSize + DenseGap;
        foreach (var (color, count) in StatusCounts())
        {
            if (count == 0) continue;
            var label = OverlayDraw.Text(count.ToString(), 12, new SolidColorBrush(color), FontWeight.Bold);
            double groupW = dot + 4 + label.Width;
            double startX = cx - groupW / 2;
            double midY = y + DenseRowHeight / 2;
            ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(startX + dot / 2, midY), dot / 2, dot / 2);
            OverlayDraw.TextLeftMid(ctx, label, startX + dot + 4, midY);
            y += DenseRowHeight;
        }
    }

    public void Dispose()
    {
        HideDropZones();
        _closeTimer.Stop();
    }
}
