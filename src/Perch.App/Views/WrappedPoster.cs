using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// Renders a <see cref="WrappedSummary"/> as a shareable "Perch Wrapped" poster — a vibrant,
/// Spotify-Wrapped-style image (the Avalonia port of the WinForms <c>WrappedRenderer</c>). Owner-drawn
/// at a fixed high resolution (<see cref="PosterWidth"/>×<see cref="PosterHeight"/>) so it stays crisp
/// when copied or saved; <c>WrappedWindow</c> scales it to fit for preview, and <see cref="RenderBitmap"/>
/// bakes it to a <see cref="RenderTargetBitmap"/> for the clipboard / PNG export.
///
/// Layout is a single top-to-bottom pass: header → scope title → persona hero → 2×2 stat grid →
/// tokens banner → top picks → highlight reel → footer. Heights come from the measured text
/// (<see cref="FormattedText.Height"/>), never a hard-coded pixel value, per the project's
/// text-clipping rule.
/// </summary>
internal sealed class WrappedPoster : Control
{
    public const int PosterWidth = 1080;
    public const int PosterHeight = 1620;

    private const double Edge = 72;
    private const double ContentW = PosterWidth - Edge * 2;

    // Point → device-independent-pixel conversion, so the DIP sizes below read as the WinForms point
    // sizes they were tuned at (1pt = 96/72 px on a 96-dpi surface).
    private static double Pt(double pt) => pt * 96.0 / 72.0;

    // ── Palette ──────────────────────────────────────────────────────────────────
    private static readonly Color GradTop = Color.FromRgb(40, 30, 96);      // deep indigo
    private static readonly Color GradMid = Color.FromRgb(109, 40, 217);    // violet-600
    private static readonly Color GradBottom = Color.FromRgb(200, 38, 140); // magenta

    /// <summary>The poster backdrop's gradient stops (indigo → violet → magenta), exposed so UI that
    /// promotes the feature — the stats toolbar's "Wrapped" button — can wear the same colours.</summary>
    public static IReadOnlyList<Color> BackdropStops { get; } = new[] { GradTop, GradMid, GradBottom };

    private static readonly Color Ink = Color.FromRgb(248, 246, 255);       // near-white
    private static readonly Color Muted = Color.FromRgb(229, 225, 247);
    private static readonly Color Faint = Color.FromRgb(208, 202, 232);
    private static readonly IBrush InkBrush = new SolidColorBrush(Ink);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Muted);
    private static readonly IBrush FaintBrush = new SolidColorBrush(Faint);
    private static readonly IBrush GlassFill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly IPen GlassBorder = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1.5);

    // A soft drop shadow behind every text run — the cheap, reliable way to keep light text legible over
    // a vivid gradient (and the lightened glass cards) without dimming the palette.
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private const double ShadowDy = 2;

    // Text is drawn in the app's default face (Inter, via WithInterFont); emoji get a colour-emoji
    // typeface so they render in colour rather than as a tofu box, and are kept in a separate run for
    // the same reason the WinForms poster split them.
    private static readonly Typeface EmojiFace =
        new(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"));

    private static Color PersonaColor(WrappedPersona p) => p switch
    {
        WrappedPersona.NightOwl => Color.FromRgb(56, 189, 248),
        WrappedPersona.EarlyBird => Color.FromRgb(251, 191, 36),
        WrappedPersona.ToolWhisperer => Color.FromRgb(74, 222, 128),
        WrappedPersona.Marathoner => Color.FromRgb(251, 146, 60),
        WrappedPersona.AgentWrangler => Color.FromRgb(167, 139, 250),
        WrappedPersona.TokenTitan => Color.FromRgb(244, 114, 182),
        WrappedPersona.Conversationalist => Color.FromRgb(96, 165, 250),
        _ => Color.FromRgb(45, 212, 191),
    };

    private readonly WrappedSummary _s;
    private readonly IImage? _icon;

    public WrappedPoster(WrappedSummary summary, IImage? icon = null)
    {
        _s = summary;
        _icon = icon;
        Width = PosterWidth;
        Height = PosterHeight;
    }

    protected override Size MeasureOverride(Size availableSize) => new(PosterWidth, PosterHeight);

    /// <summary>Bakes the poster to an opaque <see cref="RenderTargetBitmap"/> at 1× (the full
    /// <see cref="PosterWidth"/>×<see cref="PosterHeight"/>), ready for the clipboard or a PNG file.</summary>
    public RenderTargetBitmap RenderBitmap()
    {
        Measure(new Size(PosterWidth, PosterHeight));
        Arrange(new Rect(0, 0, PosterWidth, PosterHeight));
        var rtb = new RenderTargetBitmap(new PixelSize(PosterWidth, PosterHeight), new Vector(96, 96));
        rtb.Render(this);
        return rtb;
    }

    public override void Render(DrawingContext ctx)
    {
        PaintBackground(ctx);
        Color accent = PersonaColor(_s.Persona);

        double y = Edge - 8;

        // ── Header wordmark (icon + PERCH · WRAPPED), centred ──
        y = DrawHeader(ctx, accent, y) + 26;

        // ── Scope title ──
        y += DrawCentered(ctx, _s.ScopeTitle.ToUpperInvariant(), Face(FontWeight.Bold), Pt(52), InkBrush, y) + 2;
        if (_s.Subtitle.Length > 0)
            y += DrawCentered(ctx, _s.Subtitle, Face(), Pt(15), MutedBrush, y);
        y += 24;

        // ── Persona hero: accent disc with emoji, then name + tagline ──
        y = DrawPersona(ctx, accent, y) + 24;

        // ── 2×2 stat grid ──
        var cells = new (string value, string label)[]
        {
            (StatsFormat.Duration(_s.ActiveTime), "active"),
            (_s.Sessions.ToString(),              _s.Sessions == 1 ? "session" : "sessions"),
            (_s.Prompts.ToString(),               _s.Prompts == 1 ? "prompt" : "prompts"),
            (_s.ToolCalls.ToString(),             "tool calls"),
        };
        y = DrawStatGrid(ctx, cells, y) + 16;

        // ── Tokens banner ──
        y = DrawTokensBanner(ctx, accent, y) + 18;

        // ── Top picks ──
        y = DrawTopPicks(ctx, accent, y) + 20;

        // ── Highlight reel ──
        DrawHighlights(ctx, y);

        // ── Footer pinned to the bottom ──
        DrawFooter(ctx);
    }

    private static void PaintBackground(DrawingContext ctx)
    {
        var full = new Rect(0, 0, PosterWidth, PosterHeight);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(GradTop, 0),
                new GradientStop(GradMid, 0.55),
                new GradientStop(GradBottom, 1),
            },
        };
        ctx.FillRectangle(brush, full);

        // Two soft blooms for depth — bright, very translucent discs in opposite corners.
        SoftDisc(ctx, new Point(PosterWidth * 0.85, PosterHeight * 0.12), 360, Color.FromArgb(70, 168, 85, 247));
        SoftDisc(ctx, new Point(PosterWidth * 0.12, PosterHeight * 0.92), 420, Color.FromArgb(60, 56, 189, 248));
    }

    private static void SoftDisc(DrawingContext ctx, Point center, double radius, Color color)
    {
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        ctx.DrawEllipse(brush, null, center, radius, radius);
    }

    private double DrawHeader(DrawingContext ctx, Color accent, double y)
    {
        var accentBrush = new SolidColorBrush(accent);
        var font = Face(FontWeight.SemiBold);
        double size = Pt(19);
        var left = Measure("PERCH", font, size);
        var dot = Measure("·", font, size);
        var right = Measure("WRAPPED", font, size);
        const double sep = 10;
        double iconSz = _icon != null ? 40 : 0;
        double gapIcon = _icon != null ? 14 : 0;

        double total = iconSz + gapIcon + left.Width + sep + dot.Width + sep + right.Width;
        double x = (PosterWidth - total) / 2;
        double lineH = Math.Max(left.Height, iconSz);
        double midY = y + lineH / 2;

        if (_icon != null)
        {
            ctx.DrawImage(_icon, new Rect(x, midY - iconSz / 2, iconSz, iconSz));
            x += iconSz + gapIcon;
        }
        DrawShadowed(ctx, left, InkBrush, x, midY - left.Height / 2); x += left.Width + sep;
        DrawShadowed(ctx, dot, accentBrush, x, midY - dot.Height / 2); x += dot.Width + sep;
        DrawShadowed(ctx, right, accentBrush, x, midY - right.Height / 2);
        return y + lineH;
    }

    private double DrawPersona(DrawingContext ctx, Color accent, double y)
    {
        const double disc = 144;
        double cx = PosterWidth / 2.0;
        var center = new Point(cx, y + disc / 2);

        // Accent disc with a soft radial fill and a bright ring.
        var fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(235, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(90, accent.R, accent.G, accent.B), 1),
            },
        };
        ctx.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb(180, accent.R, accent.G, accent.B)), 3),
            center, disc / 2, disc / 2);

        // Emoji centred in the disc.
        var emoji = Measure(Emoji(_s.PersonaEmoji), EmojiFace, Pt(60), InkBrush);
        ctx.DrawText(emoji, new Point(cx - emoji.Width / 2, center.Y - emoji.Height / 2));

        double cur = center.Y + disc / 2 + 18;
        cur += DrawCentered(ctx, _s.PersonaName, Face(FontWeight.Bold), Pt(33), InkBrush, cur) + 4;
        cur += DrawCentered(ctx, _s.PersonaTagline, Face(FontWeight.Normal, FontStyle.Italic), Pt(16), MutedBrush, cur);
        return cur;
    }

    private double DrawStatGrid(DrawingContext ctx, (string value, string label)[] cells, double y)
    {
        const double gap = 18;
        double cardW = (ContentW - gap) / 2;
        const double cardH = 124;
        var numFont = Face(FontWeight.Bold);
        double numSize = Pt(38), lblSize = Pt(14);
        for (int i = 0; i < cells.Length; i++)
        {
            int col = i % 2, row = i / 2;
            double cx = Edge + col * (cardW + gap);
            double cy = y + row * (cardH + gap);
            Rendering.OverlayDraw.Panel(ctx, new Rect(cx, cy, cardW, cardH), GlassFill, GlassBorder, 22);

            // Number then label, vertically centred as a pair inside the card.
            var num = Measure(cells[i].value, numFont, numSize);
            var lbl = Measure(cells[i].label, Face(), lblSize);
            double blockH = num.Height + 2 + lbl.Height;
            double top = cy + (cardH - blockH) / 2;
            DrawCentered(ctx, cells[i].value, numFont, numSize, InkBrush, top, cx, cardW);
            // Small labels use the bright muted tone, not the accent — a blue/violet accent on the
            // purple cards reads poorly; the accent is saved for large, bold elements where it pops.
            DrawCentered(ctx, cells[i].label, Face(), lblSize, MutedBrush, top + num.Height + 2, cx, cardW);
        }
        int rows = (cells.Length + 1) / 2;
        return y + rows * cardH + (rows - 1) * gap;
    }

    private double DrawTokensBanner(DrawingContext ctx, Color accent, double y)
    {
        var numFont = Face(FontWeight.Bold);
        double numSize = Pt(46), lblSize = Pt(14), noteSize = Pt(15);
        string fig = StatsFormat.Tokens(_s.TotalTokens);
        var figFt = Measure(fig, numFont, numSize);
        var capFt = Measure("tokens used", Face(), lblSize);
        double iconLineH = Math.Max(Measure("X", Face(), noteSize).Height, Measure("X", EmojiFace, noteSize).Height);

        // Headline figure + caption, then one icon line per equivalence, then the cost note.
        double blockH = figFt.Height + 2 + capFt.Height;
        foreach (var _ in _s.Equivalences) blockH += iconLineH + 8;
        if (_s.ShowCost) blockH += Measure("X", Face(), noteSize).Height + 8;

        double cardH = blockH + 52;
        Rendering.OverlayDraw.Panel(ctx, new Rect(Edge, y, ContentW, cardH), GlassFill, GlassBorder, 22);

        double cur = y + (cardH - blockH) / 2;
        cur += DrawCentered(ctx, fig, numFont, numSize, new SolidColorBrush(accent), cur) + 2;
        cur += DrawCentered(ctx, "tokens used", Face(), lblSize, MutedBrush, cur) + 8;
        foreach (var item in _s.Equivalences)
            cur += DrawIconLine(ctx, item.Emoji, item.Text, noteSize, MutedBrush, cur) + 8;
        if (_s.ShowCost)
            cur += DrawCentered(ctx, $"≈ {StatsFormat.Cost(_s.EstimatedCost)} of equivalent API value",
                Face(), noteSize, FaintBrush, cur) + 8;

        return y + cardH;
    }

    private double DrawTopPicks(DrawingContext ctx, Color accent, double y)
    {
        double lblSize = Pt(13), valSize = Pt(19);
        y += DrawCentered(ctx, "YOUR TOP PICKS", Face(FontWeight.SemiBold), lblSize, new SolidColorBrush(accent), y) + 14;

        var picks = new (string label, string? value)[]
        {
            ("PROJECT", _s.TopProject),
            ("GO-TO TOOL", _s.TopTool),
            ("FAVOURITE MODEL", _s.TopModel),
        };
        const double gap = 16;
        double cardW = (ContentW - gap * 2) / 3;
        const double cardH = 104;
        var valFont = Face(FontWeight.Bold);
        for (int i = 0; i < picks.Length; i++)
        {
            double cx = Edge + i * (cardW + gap);
            Rendering.OverlayDraw.Panel(ctx, new Rect(cx, y, cardW, cardH), GlassFill, GlassBorder, 18);

            string value = picks[i].value ?? "—";
            var lbl = Measure(picks[i].label, Face(FontWeight.SemiBold), lblSize);
            var val = Measure(value, valFont, valSize);
            double blockH = lbl.Height + 6 + val.Height;
            double top = y + (cardH - blockH) / 2;
            DrawCentered(ctx, picks[i].label, Face(FontWeight.SemiBold), lblSize, MutedBrush, top, cx, cardW);
            DrawCentered(ctx, value, valFont, valSize, InkBrush, top + lbl.Height + 6, cx + 8, cardW - 16, ellipsis: true);
        }
        return y + cardH;
    }

    private void DrawHighlights(DrawingContext ctx, double y)
    {
        double size = Pt(17);
        foreach (var h in _s.Highlights)
            y += DrawIconLine(ctx, h.Emoji, h.Text, size, InkBrush, y) + 12;
    }

    private void DrawFooter(DrawingContext ctx)
    {
        double size = Pt(13);
        var text = Measure("Made with Perch", Face(), size, FaintBrush);
        double y = PosterHeight - 44 - text.Height;
        if (_icon != null)
        {
            const double sz = 26;
            double total = sz + 10 + text.Width;
            double x = (PosterWidth - total) / 2;
            ctx.DrawImage(_icon, new Rect(x, y + (text.Height - sz) / 2, sz, sz));
            DrawShadowed(ctx, text, FaintBrush, x + sz + 10, y);
        }
        else
        {
            DrawCentered(ctx, "Made with Perch", Face(), size, FaintBrush, y);
        }
    }

    // Strips emoji variation selectors (U+FE0F / U+FE0E), which nudge glyph positioning off at large
    // sizes; the base codepoint still renders in colour from the emoji font.
    private static string Emoji(string s) =>
        new(s.Where(c => c != (char)0xFE0F && c != (char)0xFE0E).ToArray());

    // ── Drawing helpers ────────────────────────────────────────────────────────────
    private static Typeface Face(FontWeight weight = FontWeight.Normal, FontStyle style = FontStyle.Normal) =>
        new(FontFamily.Default, style, weight);

    private static FormattedText Measure(string s, Typeface face, double size, IBrush? brush = null) =>
        new(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush ?? InkBrush);

    // Draws a pre-measured run left-aligned at a point, with the soft drop shadow.
    private static void DrawShadowed(DrawingContext ctx, FormattedText ft, IBrush brush, double x, double y)
    {
        ft.SetForegroundBrush(ShadowBrush);
        ctx.DrawText(ft, new Point(x, y + ShadowDy));
        ft.SetForegroundBrush(brush);
        ctx.DrawText(ft, new Point(x, y));
    }

    // Draws one centred line across the full content width, returning its measured height so the caller
    // can advance. Height is derived from the font, so glyphs never clip on a DPI change.
    private static double DrawCentered(DrawingContext ctx, string text, Typeface face, double size, IBrush brush, double y) =>
        DrawCentered(ctx, text, face, size, brush, y, Edge, ContentW);

    private static double DrawCentered(DrawingContext ctx, string text, Typeface face, double size, IBrush brush,
        double y, double x, double width, bool ellipsis = false)
    {
        var ft = Measure(text, face, size, brush);
        ft.TextAlignment = TextAlignment.Center;
        ft.MaxTextWidth = width;
        if (ellipsis) { ft.Trimming = TextTrimming.CharacterEllipsis; ft.MaxLineCount = 1; }
        ft.SetForegroundBrush(ShadowBrush);
        ctx.DrawText(ft, new Point(x, y + ShadowDy));
        ft.SetForegroundBrush(brush);
        ctx.DrawText(ft, new Point(x, y));
        return ft.Height;
    }

    // Draws a centred [emoji] [text] pair as two runs — the emoji in the colour-emoji font, the text in
    // the body font — because a single mixed run renders the emoji as a tofu box. Returns the line
    // height so the caller can advance.
    private static double DrawIconLine(DrawingContext ctx, string rawEmoji, string text, double size, IBrush textColor, double y)
    {
        var emoji = Measure(Emoji(rawEmoji), EmojiFace, size, InkBrush);
        var body = Measure(text, Face(), size, textColor);
        const double gap = 8;
        double lineH = Math.Max(emoji.Height, body.Height);
        double x = (PosterWidth - (emoji.Width + gap + body.Width)) / 2;
        ctx.DrawText(emoji, new Point(x, y + (lineH - emoji.Height) / 2));
        DrawShadowed(ctx, body, textColor, x + emoji.Width + gap, y + (lineH - body.Height) / 2);
        return lineH;
    }
}
