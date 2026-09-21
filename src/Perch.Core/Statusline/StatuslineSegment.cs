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
/// live inside <see cref="Text"/> so a multi-line template renders as multiple terminal rows.</summary>
internal readonly record struct StatuslineSegment(string Text, StatusColor Color);

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
            if (color && seg.Color != StatusColor.Default && seg.Text.Length > 0)
            {
                var (r, g, b) = StatusColors.Rgb(seg.Color);
                sb.Append("\x1b[38;2;").Append(r).Append(';').Append(g).Append(';').Append(b).Append('m');
                sb.Append(seg.Text);
                sb.Append(Reset);
            }
            else
            {
                sb.Append(seg.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>The visible text with all colour stripped.</summary>
    public static string ToPlain(IReadOnlyList<StatuslineSegment> segments) =>
        string.Concat(segments.Select(s => s.Text));
}
