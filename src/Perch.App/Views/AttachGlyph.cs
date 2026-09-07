using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The composer's "attach a file" mark as a small control: a plain, line-drawn diagonal paperclip we draw
/// ourselves rather than the OS paperclip emoji (which reads as Microsoft's "Clippy" — a face we don't want
/// to ship for copyright reasons). No eyes, no face — just the clip. Tinted by the brush passed in (the
/// toolbar's own muted hue).
/// </summary>
internal sealed class AttachGlyph : Control
{
    // Material "attach_file" outline on a 24x24 grid — a plain diagonal paperclip.
    private static readonly Geometry Paperclip = Geometry.Parse(
        "M16.5 6v11.5c0 2.21-1.79 4-4 4s-4-1.79-4-4V5c0-1.38 1.12-2.5 2.5-2.5s2.5 1.12 2.5 2.5v10.5" +
        "c0 .55-.45 1-1 1s-1-.45-1-1V6H10v9.5c0 1.38 1.12 2.5 2.5 2.5s2.5-1.12 2.5-2.5V5c0-2.21-1.79-4-4-4" +
        "S7 2.79 7 5v12.5c0 3.04 2.46 5.5 5.5 5.5s5.5-2.46 5.5-5.5V6h-1.5z");

    private readonly IBrush _brush;

    public AttachGlyph(IBrush brush)
    {
        _brush = brush;
        Width = 16;
        Height = 16;
    }

    public override void Render(DrawingContext ctx)
    {
        // The 24x24 geometry scaled to fill the box.
        var s = Bounds.Width / 24.0;
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s));
        ctx.DrawGeometry(_brush, null, Paperclip);
    }
}
