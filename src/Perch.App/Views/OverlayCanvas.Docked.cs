using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
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

    // Signature of the screen layout last applied, so OnScreensChanged can tell a real monitor change from
    // the work-area-only change our own reservation causes (which must not trigger a re-reserve loop).
    private string _dockScreenSig = "";

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
        ApplyDockedGeometry();
        UpdateTickTimer();
        InvalidateMeasure();
        InvalidateVisual();
        BringWindowToTop();
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

    // The DIP size of the docked mock the placement editor drags (a representative full-width column). Only
    // the side is captured, so the height is illustrative.
    public (double W, double H) DockedMockSizeDip() => (FormWidth, 320);

    // ── Transitions ──────────────────────────────────────────────────────────────
    private void EnterDocked()
    {
        if (_docked) return;
        _docked = true;
        _dockCollapsed = false;
        _hoveredRow = -1;
        SetWindowCorners(rounded: false);   // square the outer window so it sits flush to the screen edge
        ApplyDockedGeometry();
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
        HideDockDropZones();
        PlatformServices.EdgeReservation.Release();
        SetWindowCorners(rounded: true);    // restore the OS rounded corners for the floating panel
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
    private void ApplyDockedGeometry()
    {
        if (HostWindow is not { Screens: { } screens } w) return;
        var screen = DockedScreen(screens);
        // Pin the resolved monitor so a later collapse/expand or screen change stays on the same screen
        // (rather than falling back to primary when no monitor was explicitly chosen).
        _dockScreenBounds = screen.Bounds;
        _dockScreenSig = ScreenSignature(screens);

        var wa = screen.WorkingArea;      // vertical extent (clears a top/bottom taskbar); unaffected by our own left/right reserve
        var b = screen.Bounds;            // horizontal edge (physical monitor edge, so re-reserves are stable)
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        double dipW = _dockCollapsed ? DockCollapsedWidth : FormWidth;
        int physW = Math.Max(1, (int)(dipW * scale));
        int x = _dockSide == HAnchor.Left ? b.X : b.X + b.Width - physW;

        w.SizeToContent = SizeToContent.Manual;
        w.Width = dipW;
        w.Height = Math.Max(1, wa.Height / scale);
        w.Position = new PixelPoint(x, wa.Y);

        ReserveDockedColumn(screen, physW);
    }

    // A stable string of every monitor's physical bounds — changes only on a real display-layout change
    // (resolution / monitor add-remove), not on a work-area change (taskbar, our own reservation).
    private static string ScreenSignature(Screens screens)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in screens.All)
        {
            var r = s.Bounds;
            sb.Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append(',').Append(r.Height).Append(';');
        }
        return sb.ToString();
    }

    private void ReserveDockedColumn(Screen screen, int physWidth)
    {
        if (HostWindow?.TryGetPlatformHandle() is not { } h) return;
        var b = screen.Bounds;
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
