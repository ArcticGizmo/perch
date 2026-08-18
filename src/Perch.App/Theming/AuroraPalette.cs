using Avalonia.Media;

namespace Perch.Avalonia.Theming;

/// <summary>
/// The <b>Aurora</b> reading palette — the light/dark colour base shared by Perch's long-form
/// <em>reading</em> windows: the Markdown viewer (<c>MarkdownWindow</c> / <c>MdTheme</c>) and the Git
/// tree with its diff pane (<c>GitTreeWindow.TreePalette</c> / <c>DiffView</c>).
///
/// <para>These windows deliberately carry their own light/dark theme, <em>independent of the app
/// theme</em>. The overlay and dashboards are tuned for glanceability at small size on an always-on-top
/// surface; reading paragraphs of Markdown or lines of diff is a different task that wants paper-like
/// surfaces and maximum text contrast. Pinning both polarities here means a decorative or low-contrast
/// app theme can never bleed into the one place readability matters most. (This is why the full theming
/// engine — <see cref="Palette"/> / <c>ThemeService</c> — is intentionally <b>not</b> wired into these
/// two windows.)</para>
///
/// <para>Values are the ArcticGizmo "Aurora" family (a neutral, blue-accented set that clears WCAG AA):
/// three surface levels (<see cref="Sunken"/> &lt; <see cref="Surface"/> &lt; <see cref="Raised"/>),
/// text (<see cref="Title"/>/<see cref="Text"/>/<see cref="Muted"/>) and the accent. Each window derives
/// its own code / semantic / diff tints on top of this base.</para>
/// </summary>
internal readonly record struct AuroraPalette(
    Color Sunken, Color Surface, Color Raised, Color Border, Color Separator,
    Color Text, Color Title, Color Muted, Color Accent, Color AccentHover)
{
    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>Aurora (Dark): near-black neutral surfaces, near-white text, a bright blue accent.</summary>
    public static readonly AuroraPalette Dark = new(
        Sunken:      C(0x12, 0x12, 0x18),
        Surface:     C(0x18, 0x18, 0x20),
        Raised:      C(0x1F, 0x1F, 0x2A),
        Border:      C(0x2D, 0x2D, 0x3C),
        Separator:   C(0x23, 0x23, 0x2F),
        Text:        C(0xE1, 0xE1, 0xEB),
        Title:       C(0xF5, 0xF5, 0xFA),
        Muted:       C(0x8C, 0x8C, 0xA0),
        Accent:      C(0x60, 0xA5, 0xFA),
        AccentHover: C(0x93, 0xC5, 0xFD));

    /// <summary>Aurora (Light): paper-white surfaces, near-black text, a deep blue accent.</summary>
    public static readonly AuroraPalette Light = new(
        Sunken:      C(0xEE, 0xF0, 0xF4),
        Surface:     C(0xF7, 0xF8, 0xFA),
        Raised:      C(0xFF, 0xFF, 0xFF),
        Border:      C(0xD6, 0xDA, 0xE3),
        Separator:   C(0xE4, 0xE7, 0xEE),
        Text:        C(0x22, 0x25, 0x2B),
        Title:       C(0x0F, 0x11, 0x15),
        Muted:       C(0x58, 0x61, 0x73),
        Accent:      C(0x25, 0x63, 0xEB),
        AccentHover: C(0x1D, 0x4E, 0xD8));

    /// <summary>The Aurora base for the requested polarity.</summary>
    public static AuroraPalette For(bool light) => light ? Light : Dark;
}
