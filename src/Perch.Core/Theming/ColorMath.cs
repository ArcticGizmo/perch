namespace Perch.Theming;

/// <summary>
/// HSL colour maths over <see cref="Rgb"/>, for the theme designer: re-tinting the neutral ramp toward a
/// chosen hue (the "make my theme a bit more Perch-red" slider) and nudging a colour's lightness until it
/// clears a contrast target (the "fix contrast" button). UI-free like the rest of <c>Perch.Theming</c>.
/// Hue is degrees [0,360); saturation and lightness are [0,1].
/// </summary>
public static class ColorMath
{
    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double d = max - min;

        double h = 0, s = 0;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)      h = (g - b) / d + (g < b ? 6.0 : 0.0);
            else if (max == g) h = (b - r) / d + 2.0;
            else               h = (r - g) / d + 4.0;
            h *= 60.0;
        }
        return (h, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        if (s <= 1e-9)
        {
            byte v = ToByte(l);
            return new Rgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360.0;
        return new Rgb(
            ToByte(HueToChannel(p, q, hk + 1.0 / 3.0)),
            ToByte(HueToChannel(p, q, hk)),
            ToByte(HueToChannel(p, q, hk - 1.0 / 3.0)));
    }

    /// <summary>The colour's WCAG relative luminance-friendly lightness (HSL L). Handy for ordering.</summary>
    public static double Lightness(Rgb c) => ToHsl(c).L;

    /// <summary>Re-tints a (near-)neutral colour toward <paramref name="hueDeg"/> at <paramref name="chroma"/>
    /// saturation, keeping its original lightness — the core of the designer's neutral-tint slider. At
    /// <paramref name="chroma"/> 0 the colour is left a pure grey of the same lightness.</summary>
    public static Rgb Retint(Rgb c, double hueDeg, double chroma)
    {
        var (_, _, l) = ToHsl(c);
        return FromHsl(hueDeg, Math.Clamp(chroma, 0, 1), l);
    }

    /// <summary>Returns <paramref name="fg"/> shifted in lightness (away from <paramref name="bg"/>) just
    /// enough to reach <paramref name="targetRatio"/> against it, preserving hue and saturation — the
    /// "nudge to pass" fix. Returns the original if it already passes or no shift can reach the target.</summary>
    public static Rgb NudgeToContrast(Rgb fg, Rgb bg, double targetRatio)
    {
        if (Contrast.Ratio(fg, bg) >= targetRatio) return fg;

        var (h, s, l) = ToHsl(fg);
        // Move toward whichever end (white/black) is further from the background, in fine steps.
        bool lighten = Contrast.RelativeLuminance(bg) < 0.5;
        Rgb best = fg;
        for (int i = 1; i <= 100; i++)
        {
            double nl = Math.Clamp(l + (lighten ? i : -i) / 100.0, 0, 1);
            var cand = FromHsl(h, s, nl);
            if (Contrast.Ratio(cand, bg) >= targetRatio) return cand;
            best = cand;
            if (nl is <= 0 or >= 1) break;
        }
        return best; // couldn't fully reach it (e.g. mid-grey bg) — return the most-contrasting attempt
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255.0);
}
