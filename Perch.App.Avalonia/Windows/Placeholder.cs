using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A tiny centred "not yet ported" label for the window shells created in step 5.1. Each later step
/// replaces its window's content with the real owner-drawn surface, so this disappears piece by piece.
/// </summary>
internal static class Placeholder
{
    public static Control For(string title) => new TextBlock
    {
        Text = $"{title} — coming in Phase 5",
        Foreground = Palette.MutedBrush,
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
