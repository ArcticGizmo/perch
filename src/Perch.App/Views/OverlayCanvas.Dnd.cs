using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Views;

/// <summary>
/// The header's Do Not Disturb button: a sleeping bell (with "z"s) at the left edge of the right-hand glyph cluster, filled
/// in the accent colour while the OS is in Do Not Disturb and outlined in the muted colour while it isn't. A
/// click raises <see cref="DndToggleRequested"/> for the app to flip the OS state (the canvas never touches the
/// platform); the app polls the real state back in through <see cref="SetDndActive"/>. Opt-in via
/// <c>AppSettings.ShowDndButton</c>.
/// </summary>
public sealed partial class OverlayCanvas
{
    private bool _showDndButton;
    private bool _dndOn;
    private bool _hoveredDnd;
    private Rect _dndRect;

    /// <summary>Raised when the header's Do Not Disturb button is clicked. The argument is the state the user
    /// asked for (the opposite of what the button showed).</summary>
    public event Action<bool>? DndToggleRequested;

    // A sleeping bell, built once around the origin (the 16px box spans -8..8) and translated into place, so the
    // paint path allocates nothing: the bell body plus its clapper on the left, and two "z"s rising off its
    // shoulder at the top right. The bell fills when DND is on; the z's are always strokes.
    private static readonly Geometry DndBell = Geometry.Parse(
        "M -7.5,4 L -6.5,3 L -6.5,-0.5 C -6.5,-3.5 -5,-5.5 -2.5,-5.5 C 0,-5.5 1.5,-3.5 1.5,-0.5 L 1.5,3 L 2.5,4 Z " +
        "M -4,5.2 A 1.5,1.5 0 0 0 -1,5.2 Z");
    private static readonly Geometry DndZs = Geometry.Parse(
        "M 3.5,-3.5 L 7,-3.5 L 3.5,0 L 7,0 " +
        "M 5.2,-8 L 7.6,-8 L 5.2,-5.6 L 7.6,-5.6");

    /// <summary>Show/hide the header's Do Not Disturb button.</summary>
    public void SetShowDndButton(bool show)
    {
        if (_showDndButton == show) return;
        _showDndButton = show;
        if (!show) { _dndRect = default; _hoveredDnd = false; }
        InvalidateVisual();
    }

    /// <summary>Feeds the current OS Do Not Disturb state to the button (repaint only on a change).</summary>
    public void SetDndActive(bool on)
    {
        if (_dndOn == on) return;
        _dndOn = on;
        if (_showDndButton) InvalidateVisual();
    }

    private bool OverDndButton(Point p) => _showDndButton && _dndRect.Width > 0 && _dndRect.Contains(p);

    // Paints the button with its right edge at clusterLeft - IconGap and returns its left edge, so the caller can
    // keep stacking leftwards. Clears the hit-rect when hidden.
    private double DrawDndButton(DrawingContext ctx, double clusterLeft)
    {
        if (!_showDndButton) { _dndRect = default; return clusterLeft; }

        double x = clusterLeft - IconGap - IconBoxW;
        _dndRect = new Rect(x, (HeaderHeight - IconBoxH) / 2, IconBoxW, IconBoxH);
        if (_hoveredDnd)
            ctx.DrawRectangle(RowHoverBrush, null, _dndRect.Inflate(2), 4, 4);

        using (ctx.PushTransform(Matrix.CreateTranslation(_dndRect.Center.X, _dndRect.Center.Y)))
        {
            var brush = _dndOn ? RemoteBrush : MutedBrush;
            if (_dndOn) ctx.DrawGeometry(brush, null, DndBell);
            else ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.3, lineJoin: PenLineJoin.Round), DndBell);
            ctx.DrawGeometry(null, OverlayDraw.Pen(brush, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), DndZs);
        }
        return x;
    }

    private void ShowDndTooltip()
    {
        if (!OverDndButton(_dndRect.Center)) return;
        var lines = new List<OverlayTooltip.Line>
        {
            new(_dndOn ? "Do Not Disturb is on" : "Do Not Disturb is off", OverlayTooltip.FgColor, true),
            new(_dndOn ? "Click to turn it off" : "Click to silence notifications", OverlayTooltip.MutedColor, false),
        };
        Tooltip().ShowLines(lines, ToScreen(_dndRect.Left - 100, _dndRect.Bottom + 4));
    }

    /// <summary>HeadlessRenderer hook: paint the button in its hovered state (no pointer in a capture).</summary>
    internal void HoverDndForRender(bool hovered) { _hoveredDnd = hovered; InvalidateVisual(); }
}
