using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Theming;

/// <summary>
/// The rich session UI's own token sheet — the "Distinct Perch identity" the design mockup
/// (<c>docs/session-ui-mockup.html</c>) locked in: a warm charcoal ground with one accent — the bird's own
/// body pink (the brand red-orange read too close to the error colour) — deliberately unlike the cool chrome
/// elsewhere in the app. Like the Markdown viewer's
/// preview, the session window carries its own palette rather than the active <see cref="Theme"/>; only
/// its light/dark side follows the theme, and the brand + semantic hues come from <see cref="Palette"/> so
/// they stay consistent with the overlay's glyphs. Everything visual the session window uses is declared
/// here, so a sign-off revision is a one-file edit. Instances are built per window open (the brushes are
/// not the mutable shared <c>Palette.*Brush</c> fills).
/// </summary>
internal sealed class SessionPalette
{
    public bool IsDark { get; private init; }

    // Grounds & lines
    public IBrush Ground { get; private init; } = Brushes.Transparent;
    public IBrush Surface { get; private init; } = Brushes.Transparent;
    public IBrush Raised { get; private init; } = Brushes.Transparent;
    public IBrush Raised2 { get; private init; } = Brushes.Transparent;
    public IBrush Border { get; private init; } = Brushes.Transparent;
    public IBrush BorderSoft { get; private init; } = Brushes.Transparent;
    public IBrush Separator { get; private init; } = Brushes.Transparent;
    public IBrush CodeBg { get; private init; } = Brushes.Transparent;
    public IBrush CodeBorder { get; private init; } = Brushes.Transparent;

    // Text
    public IBrush Text { get; private init; } = Brushes.Transparent;
    public IBrush Title { get; private init; } = Brushes.Transparent;
    public IBrush Muted { get; private init; } = Brushes.Transparent;
    public IBrush Faint { get; private init; } = Brushes.Transparent;

    // Brand (the one accent)
    public IBrush Brand { get; private init; } = Brushes.Transparent;
    public IBrush BrandHover { get; private init; } = Brushes.Transparent;
    public IBrush BrandInk { get; private init; } = Brushes.Transparent;
    public IBrush BrandWash { get; private init; } = Brushes.Transparent;
    public IBrush BrandLine { get; private init; } = Brushes.Transparent;

    // Semantic (state, never the accent)
    public IBrush Ok { get; private init; } = Brushes.Transparent;
    public IBrush Await { get; private init; } = Brushes.Transparent;
    public IBrush AwaitWash { get; private init; } = Brushes.Transparent;
    public IBrush AwaitLine { get; private init; } = Brushes.Transparent;
    public IBrush Err { get; private init; } = Brushes.Transparent;
    public IBrush Violet { get; private init; } = Brushes.Transparent;
    public IBrush VioletWash { get; private init; } = Brushes.Transparent;
    public IBrush VioletLine { get; private init; } = Brushes.Transparent;
    public IBrush ThinkWash { get; private init; } = Brushes.Transparent;
    /// <summary>Plan mode's colour in the session UI (pinned blue; see <c>ModeGlyph.Plan</c>).</summary>
    public IBrush Plan { get; private init; } = Brushes.Transparent;

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

    /// <summary>The palette for the active theme's light/dark side.</summary>
    public static SessionPalette Current => For(Palette.Active.IsDark);

    public static SessionPalette For(bool dark)
    {
        // The accent is the bird's body pink (sampled from the icon: #DD9E8E), not the brand red-orange — the
        // red read too close to the error colour in a UI full of status hues. The light side deepens it so it
        // clears contrast on the pale ground.
        Color brand = dark ? Color.Parse("#DD9E8E") : Color.Parse("#B9675A");
        Color brandHover = dark ? Color.Parse("#EBB3A4") : Color.Parse("#A5574B");
        Color ok = Palette.Green, await = Palette.Yellow, err = Palette.Red;
        Color violet = Palette.SubAgentBrush.Color;
        Color muted = dark ? Color.Parse("#A4978D") : Color.Parse("#7D6F64");

        return dark
            ? new SessionPalette
            {
                IsDark = true,
                Ground = B("#161010"), Surface = B("#1E1714"), Raised = B("#26201B"), Raised2 = B("#332A24"),
                Border = B("#3A2F28"), BorderSoft = B("#2C241F"), Separator = B("#2A221D"),
                CodeBg = B("#171210"), CodeBorder = B("#33291F"),
                Text = B("#ECE6E1"), Title = B("#FCF7F3"), Muted = new SolidColorBrush(muted), Faint = B("#7C6F66"),
                Brand = new SolidColorBrush(brand), BrandHover = new SolidColorBrush(brandHover), BrandInk = B("#2A1410"),
                BrandWash = Wash(brand, 0x24), BrandLine = Wash(brand, 0x5C),
                Ok = new SolidColorBrush(ok), Await = new SolidColorBrush(await), Err = new SolidColorBrush(err),
                AwaitWash = Wash(await, 0x1F), AwaitLine = Wash(await, 0x57),
                Violet = new SolidColorBrush(violet), VioletWash = Wash(violet, 0x1F), VioletLine = Wash(violet, 0x4D),
                ThinkWash = Wash(muted, 0x0F), Plan = B("#60A5FA"),
            }
            : new SessionPalette
            {
                IsDark = false,
                Ground = B("#EFE7DF"), Surface = B("#FBF7F3"), Raised = B("#FFFFFF"), Raised2 = B("#F2E9E1"),
                Border = B("#E5DACE"), BorderSoft = B("#EEE5DB"), Separator = B("#ECE2D8"),
                CodeBg = B("#F4EDE5"), CodeBorder = B("#E6DBCF"),
                Text = B("#2C231D"), Title = B("#170F0A"), Muted = new SolidColorBrush(muted), Faint = B("#A3968A"),
                Brand = new SolidColorBrush(brand), BrandHover = new SolidColorBrush(brandHover), BrandInk = B("#FFFFFF"),
                BrandWash = Wash(brand, 0x1A), BrandLine = Wash(brand, 0x47),
                Ok = new SolidColorBrush(ok), Await = new SolidColorBrush(await), Err = new SolidColorBrush(err),
                AwaitWash = Wash(await, 0x1A), AwaitLine = Wash(await, 0x52),
                Violet = new SolidColorBrush(violet), VioletWash = Wash(violet, 0x1F), VioletLine = Wash(violet, 0x4D),
                ThinkWash = Wash(muted, 0x0F), Plan = B("#2563EB"),
            };
    }

    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));
    private static SolidColorBrush Wash(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));
}
