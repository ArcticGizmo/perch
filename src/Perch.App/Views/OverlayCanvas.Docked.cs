using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Views;

/// <summary>
/// Docked mode: the overlay reserves a full-height screen-edge column via the OS (<see cref="IEdgeReservation"/>,
/// Windows AppBar) so maximized windows can't cover it. It's the persisted alternative to Floating
/// (<see cref="OverlayPresentationMode"/>); Ctrl+Shift+W (<see cref="ToggleDockedCollapsed"/>) toggles the
/// column between a narrow collapsed strip (status counts, reusing the dense-strip art) and the full 280px
/// panel. Only the side (left/right) is user-configurable for now; the width is fixed and the column spans
/// the monitor's work area. The richer per-element collapsed rendering is future work — see
/// docs/reserve-edge-plan.md.
/// </summary>
public sealed partial class OverlayCanvas
{
    // The narrow collapsed column width (DIP) — a touch wider than the dense strip so the status counts read
    // comfortably. Expanded uses the fixed panel width (FormWidth).
    private const double DockCollapsedWidth = 56;

    private bool _docked;
    private bool _dockCollapsed;
    private HAnchor _dockSide = HAnchor.Right;
    private PixelRect? _dockScreenBounds;   // the monitor to dock to; null = primary
    private OverlayPlacement? _dockedPlacement;

    // The bottom edge "pull-tab" toggle (a half-circle hugging the inner border) and its hover state —
    // shown in both docked states: collapse when expanded, expand when collapsed.
    private Rect _dockToggleRect;
    private bool _hoveredDockToggle;

    // The docked geometry we last sized to, as a loop-safe signature (bounds + vertical work area + scale; see
    // GeomSig). Detection re-derives only when a live OS read differs from this — so our own edge reservation
    // (which moves only the horizontal work area, excluded from the signature) never triggers a re-derive.
    private string _dockGeomSig = "";

    // Drag-to-redock state (dragging the header/strip moves the column to another monitor edge). Mirrors the
    // dense controller's drop-lane machinery, reusing DenseDropZoneWindow.
    internal bool _dockDragArmed;
    internal bool _dockWasDrag;
    internal PixelPoint _dockDragStartScreen;
    private readonly List<DenseDropZoneWindow> _dockDropZones = [];
    private DenseDropZoneWindow? _dockActiveZone;

    /// <summary>Whether the overlay is currently in Docked mode (reserving a screen-edge column).</summary>
    public bool IsDocked => _docked;

    // ── App-facing surface ───────────────────────────────────────────────────────
    /// <summary>Switches the live overlay between Floating and Docked. Idempotent.</summary>
    public void SetOverlayMode(OverlayPresentationMode mode)
    {
        bool wantDocked = mode == OverlayPresentationMode.Docked;
        if (wantDocked == _docked) return;
        if (wantDocked) EnterDocked(); else ExitDocked();
    }

    /// <summary>Ctrl+Shift+W — collapse/expand the docked column. No-op unless docked.</summary>
    public void ToggleDockedCollapsed()
    {
        if (!_docked) return;
        _dockCollapsed = !_dockCollapsed;
        _hoveredRow = -1;
        // ApplyDockedGeometry re-queries the live work area and re-pins the height, so this doubles as a
        // fallback: if a display change ever slipped past OnScreensChanged and left the column the wrong
        // height (e.g. drooping under the taskbar), the next collapse/expand re-derives it correctly.
        ApplyDockedGeometry();
        UpdateTickTimer();
        InvalidateMeasure();
        InvalidateVisual();
        BringWindowToTop();
        // Re-assert once more after this layout pass settles: InvalidateMeasure above queues a measure, and on
        // Windows a size write can be swallowed while a layout/auto-fit is mid-flight. The posted re-apply lands
        // after the queue drains, guaranteeing the pinned height sticks. Guarded against a mode change in between.
        Dispatcher.UIThread.Post(() => { if (_docked) ApplyDockedGeometry(); }, DispatcherPriority.Loaded);
    }

    /// <summary>Releases the edge reservation and any drag lanes (app shutdown / exit). Safe to call when
    /// not docked.</summary>
    public void ReleaseDocked()
    {
        HideDockDropZones();
        PlatformServices.EdgeReservation.Release();
    }

    // ── Placement seeding / live adoption (parallels the dense controller's) ──────
    // Seeds the docked side + monitor from the saved placement before the window is shown. Only the
    // horizontal anchor and monitor are meaningful; the offsets are ignored (the column is edge-locked and
    // full-height). Null keeps the default (right edge, primary).
    private void SeedDockedPlacement(OverlayPlacement? p)
    {
        _dockedPlacement = p;
        _dockSide = p is { HAnchor: HAnchor.Left } ? HAnchor.Left : HAnchor.Right;
        _dockScreenBounds = ScreenBoundsOf(p);
    }

    // Live adoption from the placement editor: re-derive the side/monitor and, when docked now, move + re-reserve.
    private void ApplyDockedPlacementLive(OverlayPlacement? p)
    {
        SeedDockedPlacement(p);
        if (_docked) { ApplyDockedGeometry(); InvalidateVisual(); }
    }

    private static PixelRect? ScreenBoundsOf(OverlayPlacement? p) =>
        p is { MonitorX: { } mx, MonitorY: { } my, MonitorW: { } mw, MonitorH: { } mh }
            ? new PixelRect(mx, my, mw, mh)
            : null;

    // The built-in default docked placement: right edge of the primary monitor.
    public static OverlayPlacement DefaultDockedPlacement() =>
        new() { HAnchor = HAnchor.Right, VAnchor = VAnchor.Top };

    // The DIP size of the docked mock the placement editor drags (a representative full-height column). The
    // width is the configured docked width (editable in the editor); the height is illustrative.
    public (double W, double H) DockedMockSizeDip() => (Math.Max(FormWidth, _configuredDockedWidth), 320);

    // ── Transitions ──────────────────────────────────────────────────────────────
    private void EnterDocked()
    {
        if (_docked) return;
        _docked = true;
        _dockCollapsed = false;
        _hoveredRow = -1;
        SetWindowCorners(rounded: false);   // square the outer window so it sits flush to the screen edge
        ApplyDockedGeometry();              // display changes re-derive via the WM_DISPLAYCHANGE/WM_SETTINGCHANGE hook
        UpdateTickTimer();
        InvalidateMeasure();
        InvalidateVisual();
        BringWindowToTop();
    }

    private void ExitDocked()
    {
        if (!_docked) return;
        _docked = false;
        _dockCollapsed = false;
        _dockDragArmed = false;
        _dockWasDrag = false;
        _dockDisplayDebounce?.Stop();
        HideDockDropZones();
        PlatformServices.EdgeReservation.Release();
        SetWindowCorners(rounded: true);    // restore the OS rounded corners for the floating panel
        if (HostWindow is { } w) { w.MinHeight = 0; w.MaxHeight = double.PositiveInfinity; } // release the docked height pin
        SizeFloatingToContent();   // window was Manual/full-height; size back to content
        PlaceAtInitialFloating();  // restore the floating placement (or default) and re-capture it
        UpdateTickTimer();
        InvalidateMeasure();
        InvalidateVisual();
        BringWindowToTop();
    }

    private void SetWindowCorners(bool rounded)
    {
        if (HostWindow?.TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.SetWindowCornerPreference(h.Handle, rounded);
    }

    // ── Drag to re-dock (side / monitor) ─────────────────────────────────────────
    // While dragging the docked header/strip, put a left- and a right-edge drop lane on every monitor;
    // releasing over one re-docks the column there. The current dock's own lane is skipped. Reuses
    // DenseDropZoneWindow (the same translucent edge lane the dense strip uses).
    internal void ShowDockDropZones()
    {
        if (_dockDropZones.Count > 0 || HostWindow?.Screens is not { } screens) return;
        foreach (var s in screens.All)
            foreach (var side in (ReadOnlySpan<DenseSide>)[DenseSide.Left, DenseSide.Right])
            {
                bool isCurrent = s.Bounds == _dockScreenBounds && (side == DenseSide.Left) == (_dockSide == HAnchor.Left);
                if (isCurrent) continue;
                var zone = new DenseDropZoneWindow(s, side);
                _dockDropZones.Add(zone);
                zone.Show();
            }
    }

    internal void UpdateDockDropZone(PixelPoint screenPt)
    {
        DenseDropZoneWindow? hit = null;
        foreach (var z in _dockDropZones)
            if (z.ContainsScreenPoint(screenPt)) { hit = z; break; }

        if (hit == _dockActiveZone) return;
        _dockActiveZone?.SetActive(false);
        _dockActiveZone = hit;
        _dockActiveZone?.SetActive(true);
    }

    internal void HideDockDropZones()
    {
        foreach (var z in _dockDropZones) z.Close();
        _dockDropZones.Clear();
        _dockActiveZone = null;
    }

    // Re-docks the column to the monitor + edge whose lane the pointer was released over. Moving the same
    // appbar (via ApplyDockedGeometry → Reserve) frees the old edge and reserves the new one. No-op if the
    // release wasn't over a lane.
    internal void PinToDockDropZone()
    {
        if (_dockActiveZone is null) return;
        _dockScreenBounds = _dockActiveZone.TargetScreen.Bounds;
        _dockSide = _dockActiveZone.Side == DenseSide.Left ? HAnchor.Left : HAnchor.Right;
        ApplyDockedGeometry();
        InvalidateVisual();
        BringWindowToTop();
    }

    // ── Geometry + reservation ───────────────────────────────────────────────────
    // Sizes/positions the window as a full-work-area-height column flush to the docked edge (collapsed or
    // expanded width) and (re)reserves that column via the OS. Called on entry, on collapse/expand, on a
    // live placement change, and on a screen change.
    // reserve:false resizes/repositions the column window without re-reserving the OS AppBar — used during a
    // live resize drag so the desktop doesn't reflow on every mouse-move; the final reserve lands on release.
    private void ApplyDockedGeometry(bool reserve = true)
    {
        if (HostWindow is not { Screens: { } screens } w) return;
        var screen = DockedScreen(screens);   // identifies WHICH monitor (self-heals to primary if it vanished)

        // Resolve the target monitor's extents LIVE from the OS, keyed on a point at the centre of the resolved
        // monitor. Avalonia's cached Screens.WorkingArea can stay stale after a resolution change — which is the
        // whole bug: re-applying then reads the old, larger work area and the column keeps drooping under the
        // taskbar. The OS read is authoritative; fall back to Avalonia's screen only when it's unavailable
        // (the macOS stub returns null). A centre point also survives a re-dock (PinToDockDropZone sets
        // _dockScreenBounds → DockedScreen → the target monitor), so we read the target, not wherever the
        // window currently sits.
        var ab = screen.Bounds;
        var os = HostWindow.TryGetPlatformHandle() is { } h
            ? PlatformServices.WindowChrome.GetMonitorGeometryAt(ab.X + ab.Width / 2, ab.Y + ab.Height / 2)
            : null;

        int bx, bw, waY, waH; double scale;
        if (os is { } g)
        {
            bx = g.BoundsX; bw = g.BoundsWidth;
            waY = g.WorkY; waH = g.WorkHeight;
            scale = g.Scale <= 0 ? 1.0 : g.Scale;
            _dockScreenBounds = new PixelRect(g.BoundsX, g.BoundsY, g.BoundsWidth, g.BoundsHeight);
        }
        else
        {
            var wa = screen.WorkingArea;   // vertical extent (clears a top/bottom taskbar)
            bx = ab.X; bw = ab.Width;
            waY = wa.Y; waH = wa.Height;
            scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            _dockScreenBounds = ab;        // pin the resolved monitor for later collapse/expand + screen changes
        }
        // Record the geometry we're sizing to, so the watchdog re-applies only when the live read differs.
        _dockGeomSig = GeomSig(_dockScreenBounds.Value, waY, waH, scale);

        double dipW = _dockCollapsed ? DockCollapsedWidth : DockExpandedWidth;
        double dipH = Math.Max(1, waH / scale);
        int physW = Math.Max(1, (int)(dipW * scale));
        int x = _dockSide == HAnchor.Left ? bx : bx + bw - physW;

        // Pin the height HARD via Min==Max, not just Height: the window is SizeToContent="Height" / CanResize
        // false, and a stray auto-fit or a stale write could otherwise leave it at its old (too-tall) size,
        // drooping under the taskbar after a resolution drop. Relax the clamp before shrinking so the move to a
        // smaller value isn't blocked by the previous, larger floor.
        w.SizeToContent = SizeToContent.Manual;
        w.MinHeight = 0;
        w.MaxHeight = double.PositiveInfinity;
        w.Width = dipW;
        w.Height = dipH;
        w.MinHeight = dipH;
        w.MaxHeight = dipH;
        w.Position = new PixelPoint(x, waY);

        if (reserve) ReserveDockedColumn(_dockScreenBounds.Value, physW);
    }

    // ── Display-change detection: fully event-driven, zero idle cost ─────────────────────────────────
    // The Windows head hooks the raw window messages the OS delivers to every top-level window (ours is one):
    // WM_DISPLAYCHANGE (resolution / monitor add-remove / DPI) and WM_SETTINGCHANGE/SPI_SETWORKAREA (taskbar
    // resize or move). Between them they cover everything that changes the column's geometry — so there is NO
    // polling timer; nothing runs while the display is static. (Avalonia's own Screens.Changed proved
    // unreliable — a resolution change didn't raise it — which is why we hook the raw messages instead.)
    //
    // Learned from ../hypertree's monitor-layout spike: these messages fire MID-reshuffle and several times per
    // change, so we never act on the raw event — we debounce, then re-derive only if the live OS geometry
    // actually moved. The one-shot debounce timer exists only in the ~500ms window after an event and then
    // stops, so a docked-but-static overlay costs nothing.
    private DispatcherTimer? _dockDisplayDebounce;

    /// <summary>A display/work-area message arrived (wired by the Windows head). Debounce — the messages repeat
    /// mid-reshuffle — then re-derive the column if the live OS geometry actually moved. No-op unless docked.
    /// The debounce also naturally chains a late work-area settle: WM_SETTINGCHANGE re-arms it after the taskbar
    /// finishes reflowing, so the final geometry always lands.</summary>
    public void OnDisplayChanged()
    {
        if (!_docked) return;
        _dockDisplayDebounce ??= CreateDisplayDebounce();
        _dockDisplayDebounce.Stop();
        _dockDisplayDebounce.Start();
    }

    // A one-shot timer: ~500ms after the last display/work-area message it re-derives the docked geometry iff
    // the live OS signature changed, then stops. Re-applying only on a real change keeps it loop-safe — the
    // signature excludes the horizontal work area our own reservation moves.
    private DispatcherTimer CreateDisplayDebounce()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_docked && CurrentDockGeomSig() is { } sig && sig != _dockGeomSig)
            {
                ApplyDockedGeometry();
                BringWindowToTop();
            }
        };
        return t;
    }

    // The docked monitor's geometry as a compact signature, read LIVE from the OS (bypassing Avalonia's cached
    // Screens). Keyed on a point at the centre of the currently-docked monitor. Null only when no monitor point
    // is resolvable yet; on the macOS stub (no OS read) it falls back to Avalonia's screen signature.
    private string? CurrentDockGeomSig()
    {
        if (HostWindow is not { Screens: { } screens }) return null;
        var anchor = _dockScreenBounds ?? DockedScreen(screens).Bounds;
        var os = HostWindow.TryGetPlatformHandle() is { } h
            ? PlatformServices.WindowChrome.GetMonitorGeometryAt(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2)
            : null;
        if (os is { } g)
            return GeomSig(new PixelRect(g.BoundsX, g.BoundsY, g.BoundsWidth, g.BoundsHeight), g.WorkY, g.WorkHeight, g.Scale);
        // No OS read (macOS stub): fall back to Avalonia's screen, in the SAME GeomSig format ApplyDockedGeometry
        // records — otherwise the watchdog would see a permanent mismatch and re-apply every tick.
        var s = DockedScreen(screens);
        return GeomSig(s.Bounds, s.WorkingArea.Y, s.WorkingArea.Height, s.Scaling <= 0 ? 1.0 : s.Scaling);
    }

    // Bounds + the *vertical* work-area extent (Y/Height) + scale, as a string. Excludes the work area's
    // X/Width on purpose: a left/right reservation (ours, or a side taskbar) moves only those, and folding them
    // in would make the watchdog re-reserve on its own change — a loop. The vertical extent and scale are never
    // touched by a left/right reserve, so keying on them catches every real display change and nothing else.
    private static string GeomSig(PixelRect b, int workY, int workHeight, double scale)
        => $"{b.X},{b.Y},{b.Width},{b.Height}/{workY},{workHeight}@{scale}";

    private void ReserveDockedColumn(PixelRect b, int physWidth)
    {
        if (HostWindow?.TryGetPlatformHandle() is not { } h) return;
        var edge = _dockSide == HAnchor.Left ? ReservedEdge.Left : ReservedEdge.Right;
        PlatformServices.EdgeReservation.Reserve(h.Handle, edge, physWidth, b.X, b.Y, b.Width, b.Height);
    }

    // Resolves the monitor to dock to by its remembered physical bounds; self-heals to primary if that
    // monitor is gone.
    private Screen DockedScreen(Screens screens)
    {
        if (_dockScreenBounds is { } bounds)
        {
            foreach (var s in screens.All)
                if (s.Bounds == bounds) return s;
            _dockScreenBounds = null;   // vanished — fall back to primary
        }
        return screens.Primary ?? screens.All[0];
    }

    // ── Painting (collapsed strip; the expanded panel reuses the main Draw path) ──
    // The collapsed docked column: a full-height flush background with the dense strip's status-count art at
    // the top. (The richer per-element collapsed rendering is future work — see the class remarks.)
    private double DrawDockedStrip(DrawingContext? ctx, double width)
    {
        double h = ctx != null ? Bounds.Height : _denseCtl.StripHeightDip();
        if (ctx != null)
        {
            var pr = new Rect(0.5, 0.5, width - 1, h - 1);
            if (_attentionFlash) { OverlayDraw.Panel(ctx, pr, BgBrush, null, 0); DrawChaseBorder(ctx, pr, AttentionColor); }
            else OverlayDraw.Panel(ctx, pr, BgBrush, BorderPen, 0);
            _denseCtl.PaintStrip(ctx, width);
            DrawDockToggleHandle(ctx, width, h, expanded: false);
            DrawInstanceBorder(ctx, width, h);
        }
        return h;
    }

    // The bottom toggle handle — a round button centred at the bottom of the column, above the taskbar. It
    // reads like a web-app nav's collapse/expand toggle: the chevron points toward the docked edge to collapse
    // (when expanded), or into the screen to expand (when collapsed). Centred (not edge-hugging) because a
    // window can't paint past its own border, so a clipped half-circle looked cut off; centring also gives the
    // collapsed strip a big, obvious click target. Drawn in both states from DrawDockedStrip / the Draw path.
    internal void DrawDockToggleHandle(DrawingContext ctx, double width, double h, bool expanded)
    {
        const double r = 16;
        double cx = width / 2;
        double cy = h - r - 12;
        _dockToggleRect = new Rect(cx - r - 4, cy - r - 4, 2 * r + 8, 2 * r + 8); // a little padding for easy clicking

        ctx.DrawEllipse(BgFillBrush, BorderPen, new Point(cx, cy), r, r);
        if (_hoveredDockToggle) ctx.DrawEllipse(RowHoverBrush, null, new Point(cx, cy), r, r);

        bool rightDock = _dockSide != HAnchor.Left;
        // expanded → collapse (toward the docked edge); collapsed → expand (into the screen).
        string glyph = expanded
            ? (rightDock ? "›" : "‹")
            : (rightDock ? "‹" : "›");
        var chev = OverlayDraw.Text(glyph, 18, _hoveredDockToggle ? FgBrush : MutedBrush, FontWeight.Bold);
        OverlayDraw.TextLeftMid(ctx, chev, cx - chev.Width / 2, cy);
    }
}
