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
        // The broadcast waves rise up-right from a source dot; the glyph is bottom-heavy, so it's drawn a
        // touch above centre to sit in the box (~11 wide, ~13 tall).
        Width = 15;
        Height = 16;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawRemoteIcon(ctx, 2.5, Bounds.Height / 2 - 2);
}
