using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Perch.Avalonia.Rendering;
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

    /// <summary>Releases the edge reservation (app shutdown / exit). Safe to call when not docked.</summary>
    public void ReleaseDocked() => PlatformServices.EdgeReservation.Release();

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

    // The bottom edge toggle handle — a half-circle pull-tab hugging the inner border (the edge facing the
    // desktop, away from the docked screen edge), near the bottom above the taskbar. It reads like a web-app
    // nav's collapse/expand toggle: the chevron points toward the docked edge to collapse (when expanded), or
    // into the screen to expand (when collapsed). Drawn in both states so it stays in the same spot as the
    // column toggles. Called from DrawDockedStrip (collapsed) and the main Draw path (expanded).
    internal void DrawDockToggleHandle(DrawingContext ctx, double width, double h, bool expanded)
    {
        const double r = 13;
        double cy = h - r - 10;
        bool rightDock = _dockSide != HAnchor.Left;   // right-docked → inner edge is the left (x=0)
        double cx = rightDock ? 0 : width;            // centre on the inner border so the tab straddles it

        // Hit-rect = the visible (in-bounds) half of the tab.
        _dockToggleRect = rightDock
            ? new Rect(0, cy - r, r + 5, 2 * r)
            : new Rect(width - r - 5, cy - r, r + 5, 2 * r);

        ctx.DrawEllipse(BgFillBrush, BorderPen, new Point(cx, cy), r, r);
        if (_hoveredDockToggle) ctx.DrawEllipse(RowHoverBrush, null, new Point(cx, cy), r, r);

        // expanded → collapse (toward the docked edge); collapsed → expand (into the screen).
        string glyph = expanded
            ? (rightDock ? "›" : "‹")
            : (rightDock ? "‹" : "›");
        var chev = OverlayDraw.Text(glyph, 15, _hoveredDockToggle ? FgBrush : MutedBrush, FontWeight.Bold);
        double halfCx = rightDock ? r / 2 : width - r / 2;
        OverlayDraw.TextLeftMid(ctx, chev, halfCx - chev.Width / 2, cy);
    }
}
