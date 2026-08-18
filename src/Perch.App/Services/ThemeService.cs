using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Perch.Avalonia.Theming;
using Perch.Theming;

namespace Perch.Avalonia.Services;

/// <summary>
/// Applies a colour <see cref="Theme"/> across the whole app at runtime. Swapping is two steps: point
/// <see cref="Palette"/> at the new theme (which mutates every cached brush in place, so Fluent controls
/// bound to those brushes repaint themselves), then invalidate every open window's owner-drawn surfaces
/// (the overlay, dashboards and custom controls read <see cref="Palette"/> fresh each paint but must be
/// told to repaint). This is the single entry point the settings Appearance page and startup both use.
/// </summary>
internal static class ThemeService
{
    /// <summary>The built-in theme for <paramref name="id"/>, or Midnight for an unknown/missing id.</summary>
    public static Theme Resolve(string? id) => Themes.ById(id) ?? Themes.Midnight;

    /// <summary>Apply a theme app-wide, resolving the desktop lifetime itself — for callers (like the theme
    /// designer's live preview) that don't hold a reference to it. Pass a <paramref name="cvd"/> other than
    /// <see cref="CvdType.None"/> to preview the theme under a colour-vision deficiency.</summary>
    public static void ApplyLive(Theme theme, CvdType cvd = CvdType.None) =>
        Apply(theme, Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime, cvd);

    /// <summary>Swap the active theme and repaint every open window. <paramref name="desktop"/> may be null
    /// at startup (nothing shown yet) — the swap still updates <see cref="Palette"/> for the first paint.</summary>
    public static void Apply(Theme theme, IClassicDesktopStyleApplicationLifetime? desktop, CvdType cvd = CvdType.None)
    {
        Palette.Apply(theme, cvd);
        // Flip the Fluent variant so templated controls (buttons, textboxes, scrollbars, ColorPicker) match
        // a light/dark theme — the owner-drawn surfaces already follow Palette. Windows that manage their own
        // per-window variant (GitTree, Markdown) still override this locally.
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (desktop is null) return;
        foreach (var window in desktop.Windows)
            Repaint(window);
    }

    // Invalidate a window and every visual under it, so owner-drawn controls repaint in the new palette.
    private static void Repaint(Visual root)
    {
        root.InvalidateVisual();
        foreach (var v in root.GetVisualDescendants())
            if (v is Control c)
                c.InvalidateVisual();
    }
}
