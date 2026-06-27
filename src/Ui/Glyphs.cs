using Perch.Data;
namespace Perch.Ui;

/// <summary>
/// Shared owner-drawn glyphs. The permission-mode badge — a coloured fast-forward double-chevron — was
/// drawn the same way (bar the size) in both the overlay session row and the settings
/// <c>ModeLegend</c>; this is the single painter. Colour comes from <see cref="Theme.ModeColor"/>.
/// </summary>
internal static class Glyphs
{
    /// <summary>
    /// Draws the permission-mode badge: two stacked chevrons in the mode's colour, left edge at
    /// <paramref name="x"/> and vertically centred on <paramref name="midY"/>.
    /// <paramref name="halfHeight"/> and <paramref name="width"/> size each chevron so a caller can
    /// match its existing footprint (the overlay uses 4/5; the settings legend the larger 5/6).
    /// <paramref name="alpha"/> (0-255) fades the badge — the overlay drops it for idle sessions so the
    /// colour doesn't compete for attention when nothing's happening.
    /// </summary>
    public static void DrawModeBadge(Graphics g, PermissionMode mode, int x, int midY, int halfHeight, int width, int alpha = 255)
    {
        Color color = Theme.ModeColor(mode);
        if (alpha < 255) color = Color.FromArgb(alpha, color);
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[] { new Point(x, midY - halfHeight), new Point(x + width, midY), new Point(x, midY + halfHeight) });
        int x2 = x + width + 1;
        g.FillPolygon(brush, new[] { new Point(x2, midY - halfHeight), new Point(x2 + width, midY), new Point(x2, midY + halfHeight) });
    }
}
