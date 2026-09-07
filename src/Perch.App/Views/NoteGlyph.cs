using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The scratch-pad note as a small control — the same line-drawn page-with-fold glyph the overlay paints on
/// its quick-links row (<see cref="OverlayCanvas.DrawNoteIcon"/>), so the composer toolbar's "scratch pad"
/// button reads identically to the floating UI. Tinted by the brush passed in (the toolbar's own muted hue).
/// </summary>
internal sealed class NoteGlyph : Control
{
    private readonly IBrush _brush;

    public NoteGlyph(IBrush brush)
    {
        _brush = brush;
        // The glyph is w=10, h=12 centred on midY; this box holds it with a little breathing room.
        Width = 10;
        Height = 14;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawNoteIcon(ctx, 0, Bounds.Height / 2, _brush);
}
