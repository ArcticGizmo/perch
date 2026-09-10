using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The GitHub pull-request (git-merge) mark as a small control — the same state-coloured glyph the overlay
/// paints on a session row (<see cref="OverlayCanvas.DrawPrIcon"/>), so the composer toolbar's PR button reads
/// identically to the floating UI. Coloured by the PR's <see cref="PrState"/>, with the aggregate CI-check dot.
/// </summary>
internal sealed class PrGlyph : Control
{
    private readonly PrState _state;
    private readonly PrChecksRollup _checks;

    public PrGlyph(PrState state, PrChecksRollup checks)
    {
        _state = state;
        _checks = checks;
        // The glyph spans ~14 wide (merge mark + check dot) and ~12 tall centred on midY; box it with room.
        Width = 16;
        Height = 16;
    }

    public override void Render(DrawingContext ctx) =>
        OverlayCanvas.DrawPrIcon(ctx, 1.5, Bounds.Height / 2, _state, _checks, hovered: false);
}
