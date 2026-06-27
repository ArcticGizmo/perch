using System.Drawing.Drawing2D;

namespace Perch.Ui;

/// <summary>
/// Shared GDI+ drawing primitives. Centralises the rounded-rectangle path construction that was
/// re-implemented in nearly every owner-drawn surface (the overlay, the popups, the stats dashboard,
/// and the settings controls) under names like <c>RoundedRect</c> / <c>RoundedPath</c> / <c>FillRound</c>
/// / <c>FillRoundedBar</c>. Behaviour matches the most defensive of those copies: the corner radius is
/// clamped to the rectangle, and a degenerate size falls back to a plain rectangle.
/// </summary>
internal static class PaintKit
{
    /// <summary>A rounded-rectangle path with the corner radius clamped to the rectangle, so a radius
    /// larger than the box (or a near-zero box) degrades gracefully instead of drawing artefacts.</summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills a rounded rectangle of the given colour and corner radius (no-op for an empty rect).</summary>
    public static void FillRoundedRect(Graphics g, Color color, Rectangle r, int radius)
    {
        if (r.Width <= 0 || r.Height <= 0)
            return;
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Fills a "pill" bar — a horizontal bar with fully-rounded ends (radius = half the shorter
    /// side). The shape the overlay and settings usage bars are built from.</summary>
    public static void FillRoundedBar(Graphics g, Brush brush, int x, int y, int w, int h)
    {
        if (w <= 0)
            return;
        int r = Math.Min(h / 2, w / 2);
        if (r <= 0)
        {
            g.FillRectangle(brush, x, y, w, h);
            return;
        }
        using var path = RoundedRect(new Rectangle(x, y, w, h), r);
        g.FillPath(brush, path);
    }
}
