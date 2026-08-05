using Avalonia.Media;
using Perch.Theming;

namespace Perch.Avalonia.Theming;

/// <summary>Bridges <c>Perch.Core</c>'s UI-free <see cref="Rgb"/> to Avalonia's <see cref="Color"/> /
/// <see cref="SolidColorBrush"/> at the UI edge, so Core can stay framework-free while the app paints.</summary>
internal static class RgbExtensions
{
    public static Color ToColor(this Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    /// <summary>The colour with an explicit alpha (for translucent fills like the overlay panel).</summary>
    public static Color ToColor(this Rgb c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static SolidColorBrush ToBrush(this Rgb c) => new(c.ToColor());
}
