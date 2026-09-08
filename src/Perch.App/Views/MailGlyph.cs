using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The external-notification (envelope) mark as a small control — the same glyph the overlay paints on a
/// session row opted into external (ntfy) notifications (<see cref="OverlayCanvas.DrawMailIcon"/>), so the
/// composer toolbar's external-notify button reads identically to the floating UI.
/// </summary>
internal sealed class MailGlyph : Control
{
    public MailGlyph()
    {
        // The envelope is w=11, h=8 centred on midY; box it with a little breathing room.
        Width = 13;
        Height = 12;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawMailIcon(ctx, 1, Bounds.Height / 2);
}
