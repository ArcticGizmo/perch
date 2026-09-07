using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Markdown mark as a small control — the same rounded badge with an "M" and a down-arrow the overlay
/// paints on a session row that produced a <c>.md</c> file
/// (<see cref="OverlayCanvas.DrawMdIcon(DrawingContext,double,double,IBrush)"/>), so the composer toolbar's
/// "Markdown files" button reads identically to the floating UI. Tinted by the brush passed in (defaults to
/// the overlay's full-strength pink when null).
/// </summary>
internal sealed class MarkdownGlyph : Control
{
    private readonly IBrush? _brush;

    public MarkdownGlyph(IBrush? brush = null)
    {
        _brush = brush;
        // The glyph is 16x11 centred on midY; this box holds it with a little breathing room.
        Width = 18;
        Height = 14;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawMdIcon(ctx, 1, Bounds.Height / 2, _brush);
}
