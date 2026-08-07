namespace Perch.Data;

/// <summary>
/// Pure, UI-free geometry for turning an <see cref="OverlayPlacement"/> into a concrete physical-pixel
/// window position and back. Deliberately free of Avalonia types (takes a work-area rect + scale +
/// physical window size as primitives) so it is trivially unit-testable and shared by every head.
/// <para>
/// Coordinate convention matches the rest of Perch: the work area (<paramref name="waX"/> … ) and the
/// returned position are <b>physical pixels</b>; the window size (<c>physW</c>/<c>physH</c>) is physical
/// pixels; the placement offsets are <b>DIP</b>, converted with <paramref name="scale"/>
/// (physical = DIP × scale).
/// </para>
/// <para>
/// The two axes are deliberately asymmetric because the overlay's <b>width is fixed but its height grows
/// and shrinks with content</b>. Horizontally we anchor the window's <em>near</em> edge (left→left gap,
/// right→right gap, so <c>physW</c> matters). Vertically we anchor the window's <b>top edge — the header —
/// to the nearest horizontal work-area edge and ignore <c>physH</c> entirely</b>. A bottom-anchored
/// overlay therefore keeps its header a fixed distance above the work-area bottom regardless of how many
/// sessions are showing (the panel grows downward from there, clamped on-screen), instead of pinning the
/// dynamic <em>bottom</em> edge and letting the header jump around as the session count changes.
/// </para>
/// </summary>
public static class PlacementMath
{
    /// <summary>Anchor + DIP offset → clamped physical top-left position of the window. The vertical offset
    /// is the header (top-edge) distance from the anchored horizontal edge — height-independent by design;
    /// see the type remarks.</summary>
    public static (int X, int Y) ToPosition(
        OverlayPlacement p, int waX, int waY, int waW, int waH, double scale, int physW, int physH)
    {
        var offX = (int)Math.Round(p.OffsetX * scale);
        var offY = (int)Math.Round(p.OffsetY * scale);

        var x = p.HAnchor == HAnchor.Left ? waX + offX : waX + waW - physW - offX;
        // Vertical anchors the top edge (the header), not the height-dependent bottom edge.
        var y = p.VAnchor == VAnchor.Top ? waY + offY : waY + waH - offY;

        return Clamp(x, y, waX, waY, waW, waH, physW, physH);
    }

    /// <summary>
    /// Physical top-left position → an <see cref="OverlayPlacement"/> anchored to whichever corner is
    /// nearest, with DIP offsets from those two edges. The position is clamped on-screen first, so the
    /// offsets are never negative. The monitor fields are left unset — the caller records those.
    /// Vertically the header (top edge) is what's anchored, so the offset doesn't depend on the panel
    /// height; the horizontal axis still measures the window's near edge (see the type remarks).
    /// </summary>
    public static OverlayPlacement FromPosition(
        int x, int y, int waX, int waY, int waW, int waH, double scale, int physW, int physH)
    {
        (x, y) = Clamp(x, y, waX, waY, waW, waH, physW, physH);

        var distLeft = x - waX;
        var distRight = waX + waW - (x + physW);
        // Vertical distances are both to the window's *top* edge, so which one is smaller simply asks
        // "is the header in the top or bottom half of the work area?" — independent of the panel height.
        var distTop = y - waY;
        var distBottom = waY + waH - y;

        var hAnchor = distLeft <= distRight ? HAnchor.Left : HAnchor.Right;
        var vAnchor = distTop <= distBottom ? VAnchor.Top : VAnchor.Bottom;

        var offXphys = hAnchor == HAnchor.Left ? distLeft : distRight;
        var offYphys = vAnchor == VAnchor.Top ? distTop : distBottom;

        return new OverlayPlacement
        {
            HAnchor = hAnchor,
            VAnchor = vAnchor,
            OffsetX = Math.Max(0, offXphys) / scale,
            OffsetY = Math.Max(0, offYphys) / scale,
        };
    }

    /// <summary>
    /// Picks the screen a window rect belongs to, for re-deriving position after a display change: the one
    /// it overlaps most, falling back to the nearest by centre-to-centre distance when it overlaps none
    /// (e.g. the window was stranded off every monitor by an undock). Returns -1 only for an empty screen
    /// list. Each screen is its full physical bounds <c>(X, Y, W, H)</c>; the rect is physical pixels too.
    /// <para>
    /// Overlap tolerates a resolution change on the same physical monitor — the bounds differ but the
    /// window still sits over that monitor — which exact-bounds matching cannot, so this is how the
    /// "restore to the same corner distance" path finds the monitor to restore against.
    /// </para>
    /// </summary>
    public static int PickMostOverlapping(
        IReadOnlyList<(int X, int Y, int W, int H)> screens, int rx, int ry, int rw, int rh)
    {
        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            long ox = Math.Max(0L, Math.Min(rx + rw, s.X + s.W) - Math.Max(rx, s.X));
            long oy = Math.Max(0L, Math.Min(ry + rh, s.Y + s.H) - Math.Max(ry, s.Y));
            long area = ox * oy;
            if (area > bestArea) { bestArea = area; best = i; }
        }
        if (best >= 0 || screens.Count == 0) return best;

        // Overlaps nothing — pick the nearest monitor by squared centre distance so the window still
        // lands somewhere sensible rather than off-screen.
        long rcx = rx + rw / 2, rcy = ry + rh / 2;
        long bestDist = long.MaxValue;
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            long dx = (s.X + s.W / 2) - rcx, dy = (s.Y + s.H / 2) - rcy;
            long dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    /// <summary>Keeps the window fully inside the work area; on overflow it pins to the top-left edge.</summary>
    public static (int X, int Y) Clamp(
        int x, int y, int waX, int waY, int waW, int waH, int physW, int physH)
    {
        // Upper bound first, then lower — so a window larger than the work area lands flush at waX/waY.
        x = Math.Min(x, waX + waW - physW);
        x = Math.Max(x, waX);
        y = Math.Min(y, waY + waH - physH);
        y = Math.Max(y, waY);
        return (x, y);
    }
}
