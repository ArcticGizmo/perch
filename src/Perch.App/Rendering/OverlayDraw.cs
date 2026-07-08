using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Shared owner-drawing primitives for the overlay — the Avalonia counterpart of the WinForms
/// <c>PaintKit</c>. Centralises rounded-rect / pill drawing and, crucially, the CLAUDE.md rule that
/// text is sized from the font's line height (<see cref="FormattedText.Height"/>), never a magic pixel
/// value, so glyphs never clip on a DPI change.
/// </summary>
internal static class OverlayDraw
{
    /// <summary>The overlay's default typeface. WithInterFont() makes Inter the default family, so
    /// <see cref="Typeface.Default"/> resolves to Inter — matching the rest of the app.</summary>
    public static Typeface Face(FontWeight weight = FontWeight.Normal) =>
        new(FontFamily.Default, FontStyle.Normal, weight);

    /// <summary>Fills (and optionally strokes) a rounded rectangle.</summary>
    public static void Panel(DrawingContext ctx, Rect r, IBrush? fill, IPen? border, double radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double d = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        ctx.DrawRectangle(fill, border, new RoundedRect(r, d));
    }

    /// <summary>Fills a "pill" bar — a rounded rectangle with fully-rounded ends.</summary>
    public static void Pill(DrawingContext ctx, IBrush brush, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double radius = Math.Min(r.Height, r.Width) / 2;
        ctx.DrawRectangle(brush, null, new RoundedRect(r, radius));
    }

    /// <summary>Builds a <see cref="FormattedText"/> ready to draw. Read <c>.Height</c>/<c>.Width</c>
    /// to position it — never assume a pixel line height.</summary>
    public static FormattedText Text(string s, double size, IBrush brush,
        FontWeight weight = FontWeight.Normal) =>
        new(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(weight), size, brush);

    /// <summary>Draws <paramref name="ft"/> left-aligned at <paramref name="x"/>, vertically centred on
    /// <paramref name="midY"/> using its measured line height (the anti-clipping rule).</summary>
    public static void TextLeftMid(DrawingContext ctx, FormattedText ft, double x, double midY)
        => ctx.DrawText(ft, new Point(x, midY - ft.Height / 2));

    /// <summary>Strokes a circular arc using GDI-style angles (0° = east, positive = clockwise in the
    /// y-down screen space), matching the WinForms <c>Graphics.DrawArc</c> calls the glyphs were built
    /// with. <paramref name="cx"/>/<paramref name="cy"/> is the circle centre, <paramref name="r"/> its
    /// radius.</summary>
    public static void Arc(DrawingContext ctx, IPen pen, double cx, double cy, double r,
        double startDeg, double sweepDeg)
    {
        Point At(double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(At(startDeg), isFilled: false);
            gc.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0,
                Math.Abs(sweepDeg) > 180,
                sweepDeg >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>Measured width of a string at the given size/weight (uses a throwaway brush).</summary>
    public static double MeasureWidth(string s, double size, FontWeight weight = FontWeight.Normal)
        => Text(s, size, Brushes.White, weight).Width;

    /// <summary>Truncates <paramref name="text"/> with a trailing ellipsis so it fits
    /// <paramref name="maxWidth"/> at the given size/weight — the Avalonia counterpart of the WinForms
    /// TruncateString helper. Binary-searches the longest prefix that fits.</summary>
    public static string Truncate(string text, double size, double maxWidth, FontWeight weight = FontWeight.Normal)
    {
        if (string.IsNullOrEmpty(text) || MeasureWidth(text, size, weight) <= maxWidth) return text ?? "";
        if (maxWidth <= 0) return "";
        const string ell = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureWidth(text[..mid] + ell, size, weight) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? ell : text[..lo] + ell;
    }
}
