using Avalonia.Layout;
using Avalonia.Media;

namespace Perch.Avalonia.Views;

/// <summary>
/// The drawn glyphs for the thread's floating jump-to-top / jump-to-bottom buttons: one stem with two stacked
/// heads (a two-headed arrow, ↟ / ↡). Drawn rather than typed, because the Unicode arrows fall back to a symbol
/// font and come out thin and undersized beside the body font's ↑ / ↓ on the neighbouring buttons.
/// </summary>
internal static class JumpGlyphs
{
    private static readonly Geometry Up = Geometry.Parse("M6,2 L6,15 M2,6 L6,2 L10,6 M2,10 L6,6 L10,10");
    private static readonly Geometry Down = Geometry.Parse("M6,1 L6,14 M2,10 L6,14 L10,10 M2,6 L6,10 L10,6");

    /// <summary>A two-headed arrow pointing up (jump to the top) or down (jump to the bottom).</summary>
    public static global::Avalonia.Controls.Shapes.Path DoubleArrow(bool up, IBrush stroke) => new()
    {
        Data = up ? Up : Down, Stroke = stroke, StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        Width = 12, Height = 16, Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
}
