using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The published-Artifact mark as a small control — the same two-offset-squares glyph the overlay paints on a
/// session row (<see cref="OverlayCanvas.DrawArtifactIcon(DrawingContext,double,double,IBrush)"/>), so the
/// composer toolbar's "Artifacts" button reads identically to the floating UI. Tinted by the brush passed in
/// (defaults to the overlay's ambient amber when null).
/// </summary>
internal sealed class ArtifactGlyph : Control
{
    private readonly IBrush? _brush;

    public ArtifactGlyph(IBrush? brush = null)
    {
        _brush = brush;
        // The glyph spans ~11x11 centred on midY; this box holds it with a little breathing room.
        Width = 14;
        Height = 14;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawArtifactIcon(ctx, 1.5, Bounds.Height / 2, _brush);
}
