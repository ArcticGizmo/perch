using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The Jira ticket deep-link mark as a small control — the same luggage-tag glyph, in Jira brand blue, that the
/// overlay paints on a session row whose branch carries a ticket key (<see cref="OverlayCanvas.DrawJiraIcon"/>),
/// so the composer toolbar's Jira button reads identically to the floating UI.
/// </summary>
internal sealed class JiraGlyph : Control
{
    public JiraGlyph()
    {
        // The tag spans ~13 wide and ~9 tall centred on midY; box it with a little breathing room.
        Width = 15;
        Height = 14;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawJiraIcon(ctx, 1, Bounds.Height / 2);
}
