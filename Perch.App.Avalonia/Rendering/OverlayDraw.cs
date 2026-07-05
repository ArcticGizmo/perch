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
}
