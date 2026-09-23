namespace Perch.Statusline;

using System.Linq;
using System.Text;

/// <summary>
/// A semantic colour a rendered statusline run can carry. These are roles, not literal colours — the
/// terminal renderer (<see cref="StatuslineRenderer"/>) maps each to an ANSI truecolor code, and the
/// in-app designer maps the same set onto <c>Perch.Theming.Palette</c> brushes so the preview matches
/// what the terminal shows. <see cref="StatusColor.Default"/> means "no colour" — inherit the terminal's
/// foreground.
/// </summary>
internal enum StatusColor
{
    Default,
    Teal,
    Amber,
    Green,
    Red,
    Yellow,
    Blue,
    Violet,
    Muted,
}

/// <summary>One coloured run of a rendered statusline: literal text plus the colour it should paint in.
/// A rendered line is an ordered list of these (see <see cref="StatuslineTemplate.Render"/>); newlines
/// live inside <see cref="Text"/> so a multi-line template renders as multiple terminal rows.
///
/// <para><see cref="Rgb"/> carries a <em>custom</em> truecolor (packed <c>0xRRGGBB</c>) from a
/// <c>color:#rrggbb</c> filter; it is <c>-1</c> when no custom colour was given, in which case
/// <see cref="Color"/> (a named/semantic role) applies. A custom RGB always wins over the role.</para>
///
/// <para><see cref="Bg"/> / <see cref="BgRgb"/> are the same idea for the <em>background</em>, set by a
/// <c>{{#bg:…}}</c> region. <see cref="StatusColor.Default"/> + <c>-1</c> mean no background (transparent).
/// The terminal renderer emits an SGR <c>48;2;…</c> pair when a background is set.</para></summary>
internal readonly record struct StatuslineSegment(
    string Text, StatusColor Color, int Rgb = -1, StatusColor Bg = StatusColor.Default, int BgRgb = -1);

/// <summary>A rendered <see cref="StatuslineSegment"/> plus the span of template source that produced it
/// (character offsets into the original template). <see cref="IsTag"/> is true when it came from a
/// <c>{{…}}</c> token (as opposed to literal text). The designer preview uses this to highlight the
/// element under the editor caret. See <see cref="StatuslineTemplate.RenderPlaced"/>.</summary>
internal readonly record struct PlacedSegment(StatuslineSegment Segment, int SourceStart, int SourceLength, bool IsTag);

/// <summary>Maps <see cref="StatusColor"/> roles to concrete RGB, shared by the ANSI renderer and any
/// UI preview so both agree on the palette. The values mirror the designer mockup's colour classes and
/// sit in the Perch palette family.</summary>
internal static class StatusColors
{
    public static (byte R, byte G, byte B) Rgb(StatusColor c) => c switch
    {
        StatusColor.Teal   => (0x46, 0xc6, 0xb8),
        StatusColor.Amber  => (0xe3, 0xa8, 0x4e),
        StatusColor.Green  => (0x5f, 0xbf, 0x7f),
        StatusColor.Red    => (0xe5, 0x68, 0x6a),
        StatusColor.Yellow => (0xe3, 0xb3, 0x41),
        StatusColor.Blue   => (0x5b, 0x9b, 0xd6),
        StatusColor.Violet => (0xb0, 0x85, 0xe0),
        StatusColor.Muted  => (0x8b, 0x95, 0xa6),
        _                  => (0, 0, 0),
    };

    /// <summary>Parses a filter colour name (e.g. <c>color:teal</c>) to its role; unknown names —
    /// including null/empty — fall back to <see cref="StatusColor.Default"/> so a typo just renders
    /// uncoloured rather than throwing.</summary>
    public static StatusColor Parse(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "teal"   => StatusColor.Teal,
        "amber"  => StatusColor.Amber,
        "green"  => StatusColor.Green,
        "red"    => StatusColor.Red,
        "yellow" => StatusColor.Yellow,
        "blue"   => StatusColor.Blue,
        "violet" => StatusColor.Violet,
        "muted"  => StatusColor.Muted,
        _        => StatusColor.Default,
    };

    /// <summary>All named roles a template author can pass to <c>| color:</c> (excludes Default).</summary>
    public static readonly string[] Names =
        { "teal", "amber", "green", "red", "yellow", "blue", "violet", "muted" };

    /// <summary>Parses a hex colour arg (<c>#46c6b8</c>, <c>46c6b8</c>, or 3-digit <c>#abc</c>) to a packed
    /// <c>0xRRGGBB</c> int, or -1 when it isn't valid hex. Terminals render 24-bit truecolor, so any hex
    /// works — the ANSI renderer and the generated Node script parse it the same way.</summary>
    public static int ParseHex(string? s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        var h = s[0] == '#' ? s[1..] : s;
        if (h.Length == 3)   // #abc → #aabbcc
            h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
        if (h.Length != 6) return -1;
        foreach (var c in h)
            if (!Uri.IsHexDigit(c)) return -1;
        return System.Convert.ToInt32(h, 16);
    }
}

/// <summary>Turns rendered <see cref="StatuslineSegment"/>s into the string a statusline command prints
/// to stdout: ANSI truecolor escapes for the terminal, or plain text for measuring/copying. Claude Code
/// captures stdout and renders ANSI, so the terminal form is what a live statusline emits.</summary>
internal static class StatuslineRenderer
{
    private const string Reset = "\x1b[0m";

    /// <summary>Renders the segments to a printable string. With <paramref name="color"/> true each
    /// non-default run is wrapped in an SGR truecolor pair; false yields plain text (same characters,
    /// no escapes) — handy for width measurement and the "copy as plain" affordance.</summary>
    public static string ToAnsi(IReadOnlyList<StatuslineSegment> segments, bool color = true)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            bool hasFg = seg.Rgb >= 0 || seg.Color != StatusColor.Default;
            bool hasBg = seg.BgRgb >= 0 || seg.Bg != StatusColor.Default;
            if (color && (hasFg || hasBg) && seg.Text.Length > 0)
            {
                sb.Append("\x1b[");
                bool first = true;
                if (hasFg)
                {
                    var (r, g, b) = Resolve(seg.Rgb, seg.Color);
                    sb.Append("38;2;").Append(r).Append(';').Append(g).Append(';').Append(b);
                    first = false;
                }
                if (hasBg)
                {
                    var (r, g, b) = Resolve(seg.BgRgb, seg.Bg);
                    if (!first) sb.Append(';');
                    sb.Append("48;2;").Append(r).Append(';').Append(g).Append(';').Append(b);
                }
                sb.Append('m').Append(seg.Text).Append(Reset);
            }
            else
            {
                sb.Append(seg.Text);
            }
        }
        return sb.ToString();
    }

    // A packed custom RGB (>=0) wins over the named role — same rule for foreground and background.
    private static (byte R, byte G, byte B) Resolve(int rgb, StatusColor role) =>
        rgb >= 0
            ? ((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF))
            : StatusColors.Rgb(role);

    /// <summary>The visible text with all colour stripped.</summary>
    public static string ToPlain(IReadOnlyList<StatuslineSegment> segments) =>
        string.Concat(segments.Select(s => s.Text));
}
