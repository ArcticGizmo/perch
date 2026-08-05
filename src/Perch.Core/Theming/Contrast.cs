namespace Perch.Theming;

/// <summary>
/// WCAG 2.x contrast maths over <see cref="Rgb"/> — the accessibility spine the theme system is built on.
/// Drives both the automated palette-contrast tests (so a shipped theme can't regress under the AA floor)
/// and the in-app theme designer's live ratio badge. UI-free by design (see <see cref="Rgb"/>).
///
/// <para>The ratio is the standard <c>(L1 + 0.05) / (L2 + 0.05)</c> where <c>L</c> is WCAG relative
/// luminance of the linearised sRGB channels. Sanity anchors: white-on-black = 21:1, and <c>#767676</c> on
/// white ≈ 4.54:1 (the canonical AA-for-body threshold grey).</para>
/// </summary>
public static class Contrast
{
    /// <summary>AA contrast floor for normal-size body text.</summary>
    public const double AaText = 4.5;

    /// <summary>AA floor for large text (≥ 18.66px bold or ≥ 24px) — also the non-text (icon/border) floor.</summary>
    public const double AaLarge = 3.0;

    /// <summary>The WCAG 1.4.11 non-text contrast floor for UI glyphs, focus rings and control boundaries.</summary>
    public const double NonText = 3.0;

    /// <summary>AAA floor for normal-size body text.</summary>
    public const double AaaText = 7.0;

    /// <summary>WCAG relative luminance (0..1) of an sRGB colour.</summary>
    public static double RelativeLuminance(Rgb c) =>
        0.2126 * Linearise(c.R) + 0.7152 * Linearise(c.G) + 0.0722 * Linearise(c.B);

    /// <summary>The WCAG 2.x contrast ratio (1..21) between two colours; order-independent.</summary>
    public static double Ratio(Rgb a, Rgb b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>True when <paramref name="fg"/> on <paramref name="bg"/> meets the AA body-text floor.</summary>
    public static bool PassesAA(Rgb fg, Rgb bg) => Ratio(fg, bg) >= AaText;

    /// <summary>True when the pair meets the AA large-text / non-text floor.</summary>
    public static bool PassesAaLarge(Rgb fg, Rgb bg) => Ratio(fg, bg) >= AaLarge;

    /// <summary>True when the pair meets the AAA body-text floor.</summary>
    public static bool PassesAAA(Rgb fg, Rgb bg) => Ratio(fg, bg) >= AaaText;

    // sRGB -> linear for one channel (the WCAG piecewise transfer function).
    private static double Linearise(byte channel)
    {
        double s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
