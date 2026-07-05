using Avalonia.Media;
using Perch.Data;

namespace Perch.Avalonia.Theming;

/// <summary>
/// The shared dark palette, ported from the WinForms <c>Theme</c> class (SettingsControls.cs) to
/// Avalonia colours/brushes. The overlay, settings, history and stats surfaces all read from here so
/// they render as one app. Colours are exposed as both <see cref="Color"/> (for owner-drawn
/// <c>DrawingContext</c> work) and cached <see cref="IBrush"/> (fills). Kept in sync with the overlay's
/// own inline palette — same ARGB values as the WinForms original.
/// </summary>
public static class Palette
{
    public static readonly Color FormBg      = Color.FromRgb(24, 24, 32);
    public static readonly Color Fg          = Color.FromRgb(225, 225, 235);
    public static readonly Color Title       = Color.FromRgb(245, 245, 250);
    public static readonly Color Muted       = Color.FromRgb(140, 140, 160);
    public static readonly Color Accent      = Color.FromRgb(96, 165, 250);
    public static readonly Color AccentHover = Color.FromRgb(147, 197, 253);
    public static readonly Color Border      = Color.FromRgb(45, 45, 60);
    public static readonly Color ButtonBg    = Color.FromRgb(45, 45, 60);
    public static readonly Color ButtonHover = Color.FromRgb(60, 60, 80);
    public static readonly Color Danger      = Color.FromRgb(248, 113, 113);

    // The perch-logo red-orange (#ff442d), used to draw attention to the update affordances so they
    // read as one accent.
    public static readonly Color Brand       = Color.FromRgb(255, 68, 45);

    // Usage bar / status palette (same thresholds the overlay uses).
    public static readonly Color Green        = Color.FromRgb(34, 197, 94);
    public static readonly Color Yellow       = Color.FromRgb(250, 204, 21);
    public static readonly Color Orange       = Color.FromRgb(251, 146, 60);
    public static readonly Color Red          = Color.FromRgb(239, 68, 68);
    public static readonly Color Track        = Color.FromRgb(38, 38, 52);
    public static readonly Color ExpectedMark = Color.FromRgb(180, 180, 195);

    // A neutral accent for teammates with no (or an unknown) colour — the overlay's sub-agent purple.
    public static readonly Color TeamDefault  = Color.FromRgb(168, 85, 247);

    public static Color ModeColor(PermissionMode m) => m switch
    {
        PermissionMode.AcceptEdits => Color.FromRgb(167, 139, 250),
        PermissionMode.Plan        => Color.FromRgb(96, 165, 250),
        PermissionMode.Auto        => Color.FromRgb(250, 204, 21),
        PermissionMode.Bypass      => Color.FromRgb(239, 68, 68),
        _                          => Colors.Transparent,
    };

    public static Color UsageColor(double pct) => pct switch
    {
        < 50 => Green,
        < 75 => Yellow,
        < 90 => Orange,
        _    => Red,
    };

    // Maps an Agent-Teams member colour name onto the shared palette so a given teammate is tinted the
    // same way everywhere. Unknown/missing names fall back to the neutral team accent.
    public static Color TeamColor(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "green"                          => Green,
        "yellow"                         => Yellow,
        "orange"                         => Orange,
        "red"                            => Red,
        "blue"                           => Accent,
        "cyan" or "teal"                 => Color.FromRgb(94, 234, 212),
        "magenta" or "pink" or "purple"  => Color.FromRgb(168, 85, 247),
        "gray" or "grey"                 => Color.FromRgb(148, 163, 184),
        _                                => TeamDefault,
    };

    public static Color Blend(Color a, Color b, float t) => Color.FromRgb(
        (byte)(a.R * (1 - t) + b.R * t),
        (byte)(a.G * (1 - t) + b.G * t),
        (byte)(a.B * (1 - t) + b.B * t));

    // ── Cached brushes for the most-used fills (owner-draw + XAML code-behind) ──
    public static readonly IBrush FormBgBrush = new SolidColorBrush(FormBg);
    public static readonly IBrush FgBrush      = new SolidColorBrush(Fg);
    public static readonly IBrush TitleBrush   = new SolidColorBrush(Title);
    public static readonly IBrush MutedBrush    = new SolidColorBrush(Muted);
    public static readonly IBrush AccentBrush   = new SolidColorBrush(Accent);
    public static readonly IBrush BorderBrush   = new SolidColorBrush(Border);
    public static readonly IBrush ButtonBgBrush = new SolidColorBrush(ButtonBg);
    public static readonly IBrush TrackBrush     = new SolidColorBrush(Track);
    public static readonly IBrush BrandBrush     = new SolidColorBrush(Brand);
}
