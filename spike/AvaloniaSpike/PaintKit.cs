using Avalonia;
using Avalonia.Media;

namespace AvaloniaSpike;

/// <summary>
/// Spike proof of the GDI+ -> Avalonia DrawingContext translation. The WinForms app centralises
/// rounded-rect drawing in <c>Perch.Ui.PaintKit</c> (GraphicsPath + SolidBrush); the Avalonia
/// equivalent is a <see cref="RoundedRect"/> passed to <see cref="DrawingContext.DrawRectangle"/>.
/// This file exists to confirm that translation is a near 1:1 mechanical port.
/// </summary>
internal static class PaintKit
{
    /// <summary>Fills a rounded rectangle — the Avalonia analogue of PaintKit.FillRoundedRect.</summary>
    public static void FillRoundedRect(DrawingContext ctx, IBrush brush, Rect r, double radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double d = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        ctx.DrawRectangle(brush, null, new RoundedRect(r, d));
    }

    /// <summary>Fills a "pill" bar — fully-rounded ends (radius = half the shorter side), matching
    /// PaintKit.FillRoundedBar used by the overlay/settings usage bars.</summary>
    public static void FillRoundedBar(DrawingContext ctx, IBrush brush, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double radius = Math.Min(r.Height, r.Width) / 2;
        ctx.DrawRectangle(brush, null, new RoundedRect(r, radius));
    }
}
