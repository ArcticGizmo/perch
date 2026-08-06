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
/// </summary>
public static class PlacementMath
{
    /// <summary>Anchor + DIP offset → clamped physical top-left position of the window.</summary>
    public static (int X, int Y) ToPosition(
        OverlayPlacement p, int waX, int waY, int waW, int waH, double scale, int physW, int physH)
    {
        var offX = (int)Math.Round(p.OffsetX * scale);
        var offY = (int)Math.Round(p.OffsetY * scale);

        var x = p.HAnchor == HAnchor.Left ? waX + offX : waX + waW - physW - offX;
        var y = p.VAnchor == VAnchor.Top ? waY + offY : waY + waH - physH - offY;

        return Clamp(x, y, waX, waY, waW, waH, physW, physH);
    }

    /// <summary>
    /// Physical top-left position → an <see cref="OverlayPlacement"/> anchored to whichever corner is
    /// nearest, with DIP offsets from those two edges. The position is clamped on-screen first, so the
    /// offsets are never negative. The monitor fields are left unset — the caller records those.
    /// </summary>
    public static OverlayPlacement FromPosition(
        int x, int y, int waX, int waY, int waW, int waH, double scale, int physW, int physH)
    {
        (x, y) = Clamp(x, y, waX, waY, waW, waH, physW, physH);

        var distLeft = x - waX;
        var distRight = waX + waW - (x + physW);
        var distTop = y - waY;
        var distBottom = waY + waH - (y + physH);

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
