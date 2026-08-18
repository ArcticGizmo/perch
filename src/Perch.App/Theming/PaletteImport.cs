using ArcticGizmo.Avalonia.Palette;
using Perch.Theming;
using PkgRgb = ArcticGizmo.Avalonia.Palette.Color.Rgb;

namespace Perch.Avalonia.Theming;

/// <summary>
/// Harvests the curated, WCAG-AA palettes from the <c>ArcticGizmo.Avalonia.Palette</c> package (Nord,
/// Gruvbox, Solarized, One, Tokyo Night, Rosé Pine, Sepia, …) into Perch's own <see cref="Theme"/> model.
/// We take each palette's <em>chrome / text / accent</em> — the roles a user actually tints — and map them
/// onto Perch's roles, deriving the few Perch-specific roles the package doesn't carry (the translucent
/// overlay surface + its row hover, the usage-bar track and expected-usage tick, the teammate tree line).
///
/// <para><b>Semantics stay Perch's.</b> The package folds status colours (Success/Warning/Danger/…) into
/// each palette, but Perch keeps its semantic hues in <see cref="FixedColors"/> so running=green /
/// awaiting=yellow / error=red stay identical under every theme — the overlay's glanceable read. We import
/// only the tintable roles; the package's status/syntax/diff/editor tokens are ignored.</para>
///
/// <para><b>Both polarities.</b> Dark and light schemes are imported (light seeds its semantic glyphs from
/// the light default set via the <see cref="Themes.Daylight"/> base). The Aurora and High Contrast families
/// are skipped — they duplicate Perch's own Midnight / High Contrast / Daylight presets.</para>
/// </summary>
public static class PaletteImport
{
    private static readonly PkgRgb Black = new(0, 0, 0);
    private static readonly PkgRgb White = new(255, 255, 255);

    // Families already covered by a hand-authored Perch preset — skip to avoid near-duplicates.
    private static readonly HashSet<string> SkipFamilies =
        new(StringComparer.OrdinalIgnoreCase) { "Aurora", "High Contrast" };

    /// <summary>Every imported Perch theme (both light and dark schemes), in the package's display order.</summary>
    public static IReadOnlyList<Theme> All() =>
        PaletteCatalog.All
            .Where(p => !SkipFamilies.Contains(p.Family))
            .Select(ToTheme)
            .ToArray();

    /// <summary>Map one package <see cref="PaletteDefinition"/> onto a Perch <see cref="Theme"/>. We start
    /// from the polarity-matching Perch base (so the semantic glyph hues and any future roles inherit the
    /// right default set — imported palettes keep Perch's stable glyphs) and overwrite the chrome/text/accent
    /// from the package. The few Perch roles the package doesn't carry are derived in the package's own
    /// <see cref="PkgRgb"/> space, mirroring its hover/tint ratios and honouring the palette's polarity.</summary>
    public static Theme ToTheme(PaletteDefinition p)
    {
        // The overlay panel sits a step off the sunken surface — darker on a dark scheme, lighter on a light
        // one (a MixWith-black would muddy a light palette).
        var overlay     = p.SurfaceSunken.MixWith(p.IsDark ? Black : White, 0.35);
        var raisedHover = p.SurfaceRaised.MixWith(p.TextPrimary, 0.09);   // button/card hover
        var rowHover    = overlay.MixWith(p.TextPrimary, 0.07);          // subtle wash on a hovered session row
        var track       = p.Surface.MixWith(p.TextPrimary, 0.12);       // usage-bar trough
        var treeLine    = p.Border.MixWith(p.TextPrimary, 0.12);        // teammate connector
        var expected    = p.TextMuted.MixWith(p.TextPrimary, 0.35);     // expected-usage tick on a bar

        var baseTheme = p.IsDark ? Themes.Midnight : Themes.Daylight;
        return baseTheme with
        {
            Id     = p.Id,
            // The variant is in the display name so the light/dark twins (which share a package name) are
            // distinct in every picker and dropdown, e.g. "Nord (Dark)" / "Nord (Light)".
            Name   = $"{p.Name} ({(p.IsDark ? "Dark" : "Light")})",
            IsDark = p.IsDark,

            Surface            = C(p.Surface),
            SurfaceSunken      = C(p.SurfaceSunken),
            SurfaceRaised      = C(p.SurfaceRaised),
            SurfaceRaisedHover = C(raisedHover),
            OverlaySurface     = C(overlay),
            OverlayRowHover    = C(rowHover),
            Track              = C(track),
            Border             = C(p.Border),
            Separator          = C(p.Separator),
            TreeLine           = C(treeLine),

            TextPrimary  = C(p.TextPrimary),
            TextTitle    = C(p.TextTitle),
            TextMuted    = C(p.TextMuted),
            ExpectedMark = C(expected),

            Accent      = C(p.Accent),
            AccentHover = C(p.AccentHover),
            FocusRing   = C(p.AccentHover),
            // Semantic glyph hues inherit from baseTheme (SemanticDark / SemanticLight) — glyphs stay stable.
        };
    }

    // Package Rgb -> Perch Rgb (both are opaque 8-bit-per-channel triples).
    private static Rgb C(PkgRgb c) => new(c.R, c.G, c.B);
}
