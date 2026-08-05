using System.Text;

namespace Perch.Theming;

/// <summary>
/// A compact, copy-pasteable share code for a theme — small enough to fit a QR and tidy on the clipboard.
/// Format <c>perch1:&lt;base64&gt;</c> where the payload is <c>name\nisDark\nhex,hex,…</c> (the 30 role
/// colours in a fixed order). Decoding starts from <see cref="Themes.Midnight"/>, so a code from a future
/// version with extra roles still yields a usable theme (unknown trailing roles keep Midnight's value).
/// </summary>
public static class ThemeCodec
{
    private const string Prefix = "perch1:";

    // The role read/write pairs, in a fixed order shared by encode and decode.
    private static readonly (Func<Theme, Rgb> Get, Func<Theme, Rgb, Theme> Set)[] Roles =
    [
        (t => t.Surface,            (t, c) => t with { Surface = c }),
        (t => t.SurfaceSunken,      (t, c) => t with { SurfaceSunken = c }),
        (t => t.SurfaceRaised,      (t, c) => t with { SurfaceRaised = c }),
        (t => t.SurfaceRaisedHover, (t, c) => t with { SurfaceRaisedHover = c }),
        (t => t.OverlaySurface,     (t, c) => t with { OverlaySurface = c }),
        (t => t.OverlayRowHover,    (t, c) => t with { OverlayRowHover = c }),
        (t => t.Track,              (t, c) => t with { Track = c }),
        (t => t.Border,             (t, c) => t with { Border = c }),
        (t => t.Separator,          (t, c) => t with { Separator = c }),
        (t => t.TreeLine,           (t, c) => t with { TreeLine = c }),
        (t => t.TextPrimary,        (t, c) => t with { TextPrimary = c }),
        (t => t.TextTitle,          (t, c) => t with { TextTitle = c }),
        (t => t.TextMuted,          (t, c) => t with { TextMuted = c }),
        (t => t.ExpectedMark,       (t, c) => t with { ExpectedMark = c }),
        (t => t.Accent,             (t, c) => t with { Accent = c }),
        (t => t.AccentHover,        (t, c) => t with { AccentHover = c }),
        (t => t.Brand,              (t, c) => t with { Brand = c }),
        (t => t.BrandHover,         (t, c) => t with { BrandHover = c }),
        (t => t.Danger,             (t, c) => t with { Danger = c }),
        (t => t.StatusRunning,      (t, c) => t with { StatusRunning = c }),
        (t => t.StatusAttention,    (t, c) => t with { StatusAttention = c }),
        (t => t.StatusAwaiting,     (t, c) => t with { StatusAwaiting = c }),
        (t => t.StatusIdle,         (t, c) => t with { StatusIdle = c }),
        (t => t.StatusError,        (t, c) => t with { StatusError = c }),
        (t => t.StatusWarn,         (t, c) => t with { StatusWarn = c }),
        (t => t.SubAgent,           (t, c) => t with { SubAgent = c }),
        (t => t.Teal,               (t, c) => t with { Teal = c }),
        (t => t.Burn,               (t, c) => t with { Burn = c }),
        (t => t.TeamGray,           (t, c) => t with { TeamGray = c }),
        (t => t.ModeAcceptEdits,    (t, c) => t with { ModeAcceptEdits = c }),
    ];

    public static string Encode(Theme t)
    {
        var name = (t.Name ?? "Theme").Replace('\n', ' ').Replace('\r', ' ');
        var hexes = string.Join(",", Roles.Select(r => r.Get(t).ToHex().TrimStart('#')));
        var payload = $"{name}\n{(t.IsDark ? 1 : 0)}\n{hexes}";
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>Decodes a share code into a theme (with a placeholder id the caller replaces), or null if
    /// the string isn't a valid <c>perch1:</c> code.</summary>
    public static Theme? Decode(string? code)
    {
        if (code is null) return null;
        code = code.Trim();
        if (!code.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(code[Prefix.Length..]));
            var lines = payload.Split('\n');
            if (lines.Length < 3) return null;

            var theme = Themes.Midnight with
            {
                Id = "custom",
                Name = string.IsNullOrWhiteSpace(lines[0]) ? "Imported theme" : lines[0],
                IsDark = lines[1].Trim() != "0",
            };
            var hexes = lines[2].Split(',');
            for (int i = 0; i < Roles.Length && i < hexes.Length; i++)
                theme = Roles[i].Set(theme, Rgb.FromHex(hexes[i]));
            return theme;
        }
        catch
        {
            return null;
        }
    }
}
