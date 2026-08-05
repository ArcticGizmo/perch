namespace Perch.Theming;

/// <summary>
/// A plain 8-bit-per-channel sRGB colour, deliberately UI-framework-free so <c>Perch.Core</c> can reason
/// about colour (contrast maths, the theme token model) without taking an Avalonia dependency — the project
/// rule keeps Core UI-free. The UI edge (<c>Perch.Avalonia.Theming</c>) converts to/from Avalonia's
/// <c>Color</c>. Stored as three bytes; alpha lives outside this type (Perch's chrome is opaque, and the one
/// translucent fill carries its own alpha at the draw site).
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#rrggbb</c> / <c>rrggbb</c> (and the 3-digit <c>#rgb</c> shorthand). Throws on garbage.</summary>
    public static Rgb FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6)
            throw new FormatException($"'{hex}' is not a 6- or 3-digit hex colour.");
        return new Rgb(
            Convert.ToByte(s.Substring(0, 2), 16),
            Convert.ToByte(s.Substring(2, 2), 16),
            Convert.ToByte(s.Substring(4, 2), 16));
    }

    /// <summary>The <c>#RRGGBB</c> form, upper-cased.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();
}
