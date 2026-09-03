using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Theming;

namespace Perch.Avalonia.Theming;

/// <summary>
/// The rich session UI's token sheet — every colour, face and metric the session window paints with,
/// gathered in one place so a sign-off revision is a one-file edit. Instances are built per window open
/// (the brushes are not the mutable shared <c>Palette.*Brush</c> fills).
///
/// <para>The tokens are <em>derived from the active <see cref="Theme"/></em> (see <see cref="From"/>): the
/// window's chrome, text and accent follow whatever theme the user has selected — pick Nord and the session
/// window turns Nord, pick a light theme and it goes light — rather than the fixed warm-charcoal identity the
/// original mockup (<c>docs/session-ui-mockup.html</c>) locked in. The window keeps its own <em>structure</em>
/// (the layered grounds, washes and line tints the design needs) but sources the hues from the theme's roles,
/// so it stays consistent with the overlay and the rest of the app under a theme swap.</para>
/// </summary>
internal sealed class SessionPalette
{
    // The brushes are the palette's own, but every one is a fixed instance whose *colour* is mutated in place
    // by Recolor (mirroring Palette.*Brush) — so a live theme swap re-tints every open session window's chrome
    // and text without rebuilding a single control. The shared instance (Current) is the one that gets swapped;
    // the renderer's fixed-side instances (For/From) are built once and left alone.
    public bool IsDark { get; private set; }

    // Grounds & lines
    public SolidColorBrush Ground { get; } = new(Colors.Transparent);
    public SolidColorBrush Surface { get; } = new(Colors.Transparent);
    public SolidColorBrush Raised { get; } = new(Colors.Transparent);
    public SolidColorBrush Raised2 { get; } = new(Colors.Transparent);
    public SolidColorBrush Border { get; } = new(Colors.Transparent);
    public SolidColorBrush BorderSoft { get; } = new(Colors.Transparent);
    public SolidColorBrush Separator { get; } = new(Colors.Transparent);
    public SolidColorBrush CodeBg { get; } = new(Colors.Transparent);
    public SolidColorBrush CodeBorder { get; } = new(Colors.Transparent);

    // Text
    public SolidColorBrush Text { get; } = new(Colors.Transparent);
    public SolidColorBrush Title { get; } = new(Colors.Transparent);
    public SolidColorBrush Muted { get; } = new(Colors.Transparent);
    public SolidColorBrush Faint { get; } = new(Colors.Transparent);

    // Brand (the one accent)
    public SolidColorBrush Brand { get; } = new(Colors.Transparent);
    public SolidColorBrush BrandHover { get; } = new(Colors.Transparent);
    public SolidColorBrush BrandInk { get; } = new(Colors.Transparent);
    public SolidColorBrush BrandWash { get; } = new(Colors.Transparent);
    public SolidColorBrush BrandLine { get; } = new(Colors.Transparent);

    // Semantic (state, never the accent)
    public SolidColorBrush Ok { get; } = new(Colors.Transparent);
    public SolidColorBrush Await { get; } = new(Colors.Transparent);
    public SolidColorBrush AwaitWash { get; } = new(Colors.Transparent);
    public SolidColorBrush AwaitLine { get; } = new(Colors.Transparent);
    public SolidColorBrush Err { get; } = new(Colors.Transparent);
    public SolidColorBrush Violet { get; } = new(Colors.Transparent);
    public SolidColorBrush VioletWash { get; } = new(Colors.Transparent);
    public SolidColorBrush VioletLine { get; } = new(Colors.Transparent);
    public SolidColorBrush ThinkWash { get; } = new(Colors.Transparent);
    /// <summary>Plan mode's colour in the session UI (pinned blue; see <c>ModeGlyph.Plan</c>).</summary>
    public SolidColorBrush Plan { get; } = new(Colors.Transparent);

    // Type. Each face lists its intended web font first (installed on some machines) then system fallbacks,
    // so the design reads as intended where the fonts exist and degrades to the platform sans elsewhere.
    public FontFamily Display { get; } = new("Bricolage Grotesque, Segoe UI Variable Display, Segoe UI, Inter, sans-serif");
    public FontFamily Body { get; } = new("Hanken Grotesk, Segoe UI Variable Text, Segoe UI, Inter, sans-serif");
    public FontFamily Mono { get; } = new("JetBrains Mono, Cascadia Code, Cascadia Mono, Consolas, Menlo, monospace");

    // Scale
    public const double ProseSize = 15;
    public const double ThreadMaxWidth = 720;
    public static readonly CornerRadius CardRadius = new(12);
    public static readonly CornerRadius ButtonRadius = new(10);
    public static readonly CornerRadius PillRadius = new(999);

    /// <summary>The Markdown style for assistant prose: larger body, the body face, no root inset (the
    /// assistant body already provides its gutter).</summary>
    public MarkdownStyle Prose => new(
        Fg: Text, Muted: Muted, Title: Title, Link: Brand,
        CodeFg: Text, CodeBg: CodeBg, QuoteBar: BrandLine, Rule: Separator,
        TableBorder: Border, TableHeaderBg: Raised2,
        Syntax: IsDark ? CodeSyntax.Dark() : CodeSyntax.Light())
    {
        BodySize = ProseSize,
        BlockGap = 11,
        BodyFont = Body,
        RootMargin = new Thickness(0),
    };

    /// <summary>The shared, live session palette for the active theme — the instance every open session
    /// window paints from. Its brushes are re-tinted in place by <see cref="Apply"/> on a theme swap, so the
    /// windows follow the theme without rebuilding their chrome. Seeded once from the active theme.</summary>
    public static SessionPalette Current => _shared ??= From(Palette.Active);
    private static SessionPalette? _shared;

    /// <summary>Re-tint the shared palette to <paramref name="t"/> in place (mutating every brush's colour), so
    /// each open session window repaints in the new theme. Called from <c>ThemeService.Apply</c>; owner-drawn
    /// surfaces are invalidated there, and the thread rebuilds its markdown so code-syntax follows the new
    /// light/dark side (see <c>SessionThreadView.Restyle</c>).</summary>
    public static void Apply(Theme t) => (_shared ??= From(t)).Recolor(t);

    /// <summary>Kept for the headless renderer's fixed-side snapshots: builds an independent palette from a
    /// built-in preset of the requested side (the active theme when it matches, else Midnight/Daylight) so a
    /// render pass always shows both a dark and a light session window without touching the shared instance.</summary>
    public static SessionPalette For(bool dark) => From(
        dark ? (Palette.Active.IsDark ? Palette.Active : Themes.Midnight)
             : (Palette.Active.IsDark ? Themes.Daylight : Palette.Active));

    /// <summary>Builds an independent session palette from a theme's roles. The window's layered grounds,
    /// washes and line tints are derived from the theme's surfaces, accent and semantic hues, so the session
    /// UI tracks the selected theme instead of a fixed identity.</summary>
    public static SessionPalette From(Theme t)
    {
        var p = new SessionPalette();
        p.Recolor(t);
        return p;
    }

    /// <summary>Re-point every brush's colour at <paramref name="t"/>'s roles (the single colour-mapping site,
    /// shared by <see cref="From"/> and the in-place <see cref="Apply"/>).</summary>
    private void Recolor(Theme t)
    {
        bool dark = t.IsDark;
        IsDark = dark;
        Color surface = t.Surface.ToColor();
        Color accent = t.Accent.ToColor();
        Color muted = t.TextMuted.ToColor();
        Color await = t.StatusAwaiting.ToColor();
        Color violet = t.SubAgent.ToColor();

        // Layered grounds: sunken base → surface → raised card → raised-hover, straight from the theme.
        Ground.Color = t.SurfaceSunken.ToColor();
        Surface.Color = surface;
        Raised.Color = t.SurfaceRaised.ToColor();
        Raised2.Color = t.SurfaceRaisedHover.ToColor();
        Border.Color = t.Border.ToColor();
        BorderSoft.Color = Palette.Blend(t.Border.ToColor(), surface, 0.5f);
        Separator.Color = t.Separator.ToColor();
        // Code blocks sit a touch deeper than the sunken ground so they read as inset on any theme.
        CodeBg.Color = Palette.Blend(t.SurfaceSunken.ToColor(), dark ? Colors.Black : Colors.White, 0.28f);
        CodeBorder.Color = t.Border.ToColor();

        Text.Color = t.TextPrimary.ToColor();
        Title.Color = t.TextTitle.ToColor();
        Muted.Color = muted;
        Faint.Color = Palette.Blend(muted, surface, 0.45f);

        // The one accent is the theme's accent; its ink is whatever reads on it.
        Brand.Color = accent;
        BrandHover.Color = t.AccentHover.ToColor();
        BrandInk.Color = Contrast.BestForeground(t.Accent).ToColor();
        BrandWash.Color = WashColor(accent, dark ? (byte)0x24 : (byte)0x1A);
        BrandLine.Color = WashColor(accent, dark ? (byte)0x5C : (byte)0x47);

        Ok.Color = t.StatusRunning.ToColor();
        Await.Color = await;
        Err.Color = t.StatusError.ToColor();
        AwaitWash.Color = WashColor(await, dark ? (byte)0x1F : (byte)0x1A);
        AwaitLine.Color = WashColor(await, dark ? (byte)0x57 : (byte)0x52);

        Violet.Color = violet;
        VioletWash.Color = WashColor(violet, 0x1F);
        VioletLine.Color = WashColor(violet, 0x4D);
        ThinkWash.Color = WashColor(muted, 0x0F);
        // Plan mode stays a distinct blue (the theme's burn hue), so it never collides with the accent.
        Plan.Color = t.Burn.ToColor();
    }

    private static Color WashColor(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);
}
