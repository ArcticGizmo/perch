using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The remote-control (broadcast) mark as a small control — the same glyph the overlay paints on a
/// remote-controlled session row (<see cref="OverlayCanvas.DrawRemoteIcon"/>), so the composer toolbar's
/// remote-control indicator reads identically to the floating UI.
/// </summary>
internal sealed class RemoteGlyph : Control
{
    public RemoteGlyph()
    {
        Width = 14;
        Height = 14;
    }

    // The broadcast glyph isn't symmetric about its origin — the source dot sits at (originX, midY+4) with the
    // waves rising up-right — so centre it by its actual bounds: horizontally originX-2..originX+9 (centre
    // originX+3.5), vertically midY-5..midY+6 (centre midY+0.5). Solving for the box centre keeps it aligned
    // with the other toolbar glyphs (which are symmetric and just draw at the midpoint).
    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawRemoteIcon(ctx, Bounds.Width / 2 - 3.5, Bounds.Height / 2 - 0.5);
}
