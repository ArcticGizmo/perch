using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Perch.Data;

namespace Perch.Ui;

/// <summary>
/// Renders a <see cref="WrappedSummary"/> into a shareable "Perch Wrapped" poster — a vibrant,
/// Spotify-Wrapped-style image. Pure: hand it a summary (and optionally the app icon) and it returns a
/// fresh <see cref="Bitmap"/> the caller owns. The poster is drawn at a fixed high resolution
/// (<see cref="PosterWidth"/>×<see cref="PosterHeight"/>) so it stays crisp when copied or saved; the
/// preview window just scales it to fit.
///
/// Layout is a single top-to-bottom pass: header → scope title → persona hero → 2×2 stat grid →
/// tokens banner → top picks → highlight reel → footer. Heights come from the measured text (never a
/// hard-coded pixel value), per the project's text-clipping rule.
/// </summary>
internal static class WrappedRenderer
{
    public const int PosterWidth  = 1080;
    public const int PosterHeight = 1620;

    private const int Margin = 72;
    private static int ContentW => PosterWidth - Margin * 2;

    // ── Palette ──────────────────────────────────────────────────────────────────
    private static readonly Color GradTop   = Color.FromArgb(40, 30, 96);    // deep indigo
    private static readonly Color GradMid    = Color.FromArgb(109, 40, 217);  // violet-600
    private static readonly Color GradBottom = Color.FromArgb(200, 38, 140);  // magenta

    /// <summary>The poster backdrop's gradient stops (indigo → violet → magenta), exposed so UI that
    /// promotes the feature — e.g. the toolbar "Wrapped" button — can wear the same colours.</summary>
    public static IReadOnlyList<Color> BackdropStops { get; } = new[] { GradTop, GradMid, GradBottom };

    private static readonly Color Ink        = Color.FromArgb(248, 246, 255); // near-white
    private static readonly Color Muted       = Color.FromArgb(229, 225, 247);
    private static readonly Color Faint        = Color.FromArgb(208, 202, 232);
    private static readonly Color GlassFill    = Color.FromArgb(30, 255, 255, 255);
    private static readonly Color GlassBorder  = Color.FromArgb(48, 255, 255, 255);

    // A soft drop shadow behind every text run — the cheap, reliable way to keep light text legible
    // over a vivid gradient (and the lightened glass cards) without dimming the palette.
    private static readonly Color ShadowColor = Color.FromArgb(120, 0, 0, 0);
    private const float ShadowDy = 2f;

    private static Color PersonaColor(WrappedPersona p) => p switch
    {
        WrappedPersona.NightOwl          => Color.FromArgb(56, 189, 248),
        WrappedPersona.EarlyBird         => Color.FromArgb(251, 191, 36),
        WrappedPersona.ToolWhisperer     => Color.FromArgb(74, 222, 128),
        WrappedPersona.Marathoner        => Color.FromArgb(251, 146, 60),
        WrappedPersona.AgentWrangler     => Color.FromArgb(167, 139, 250),
        WrappedPersona.TokenTitan        => Color.FromArgb(244, 114, 182),
        WrappedPersona.Conversationalist => Color.FromArgb(96, 165, 250),
        _                                => Color.FromArgb(45, 212, 191),
    };

    /// <summary>Renders the poster. <paramref name="icon"/> (the app bird) is drawn in the header and
    /// footer when supplied; pass null to omit it.</summary>
    public static Bitmap Render(WrappedSummary s, Bitmap? icon = null)
    {
        var bmp = new Bitmap(PosterWidth, PosterHeight);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;

        PaintBackground(g);

        Color accent = PersonaColor(s.Persona);

        using var wordmarkFont = new Font("Segoe UI Semibold", 19f, FontStyle.Regular, GraphicsUnit.Point);
        using var scopeFont    = new Font("Segoe UI", 52f, FontStyle.Bold, GraphicsUnit.Point);
        using var subFont      = new Font("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Point);
        using var emojiFont    = new Font("Segoe UI Emoji", 60f, FontStyle.Regular, GraphicsUnit.Point);
        using var personaFont  = new Font("Segoe UI", 33f, FontStyle.Bold, GraphicsUnit.Point);
        using var taglineFont  = new Font("Segoe UI", 16f, FontStyle.Italic, GraphicsUnit.Point);
        using var statNumFont  = new Font("Segoe UI", 38f, FontStyle.Bold, GraphicsUnit.Point);
        using var statLblFont  = new Font("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Point);
        using var bigNumFont   = new Font("Segoe UI", 46f, FontStyle.Bold, GraphicsUnit.Point);
        using var labelFont    = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point);
        using var pickFont     = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Point);
        using var highlightFont= new Font("Segoe UI", 17f, FontStyle.Regular, GraphicsUnit.Point);
        using var iconFont     = new Font("Segoe UI Emoji", 15f, FontStyle.Regular, GraphicsUnit.Point);
        using var footFont     = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Point);

        float y = Margin - 8;

        // ── Header wordmark (icon + PERCH · WRAPPED), centered ──
        y = DrawHeader(g, icon, wordmarkFont, accent, y) + 26;

        // ── Scope title ──
        y += DrawCentered(g, s.ScopeTitle.ToUpperInvariant(), scopeFont, Ink, y) + 2;
        if (s.Subtitle.Length > 0)
            y += DrawCentered(g, s.Subtitle, subFont, Muted, y);
        y += 24;

        // ── Persona hero: accent disc with emoji, then name + tagline ──
        y = DrawPersona(g, s, accent, emojiFont, personaFont, taglineFont, y) + 24;

        // ── 2×2 stat grid ──
        var cells = new (string value, string label)[]
        {
            (StatsFormat.Duration(s.ActiveTime), "active"),
            (s.Sessions.ToString(),              s.Sessions == 1 ? "session" : "sessions"),
            (s.Prompts.ToString(),               s.Prompts == 1 ? "prompt" : "prompts"),
            (s.ToolCalls.ToString(),             "tool calls"),
        };
        y = DrawStatGrid(g, cells, statNumFont, statLblFont, accent, y) + 16;

        // ── Tokens banner ──
        y = DrawTokensBanner(g, s, bigNumFont, statLblFont, subFont, iconFont, accent, y) + 18;

        // ── Top picks ──
        y = DrawTopPicks(g, s, labelFont, pickFont, accent, y) + 20;

        // ── Highlight reel ──
        DrawHighlights(g, s, highlightFont, iconFont, y);

        // ── Footer pinned to the bottom ──
        DrawFooter(g, icon, footFont);

        return bmp;
    }

    private static void PaintBackground(Graphics g)
    {
        var full = new Rectangle(0, 0, PosterWidth, PosterHeight);
        using (var brush = new LinearGradientBrush(
            new Point(0, 0), new Point(PosterWidth, PosterHeight), GradTop, GradBottom))
        {
            var blend = new ColorBlend(3)
            {
                Colors    = new[] { GradTop, GradMid, GradBottom },
                Positions = new[] { 0f, 0.55f, 1f },
            };
            brush.InterpolationColors = blend;
            g.FillRectangle(brush, full);
        }

        // Two soft blooms for depth — bright, very translucent discs in opposite corners.
        SoftDisc(g, new PointF(PosterWidth * 0.85f, PosterHeight * 0.12f), 360, Color.FromArgb(70, 168, 85, 247));
        SoftDisc(g, new PointF(PosterWidth * 0.12f, PosterHeight * 0.92f), 420, Color.FromArgb(60, 56, 189, 248));
    }

    private static void SoftDisc(Graphics g, PointF center, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor    = color,
            SurroundColors = new[] { Color.FromArgb(0, color) },
            CenterPoint    = center,
        };
        g.FillPath(brush, path);
    }

    private static float DrawHeader(Graphics g, Bitmap? icon, Font font, Color accent, float y)
    {
        const string left = "PERCH";
        const string right = "WRAPPED";
        var size1 = g.MeasureString(left, font);
        var size2 = g.MeasureString(right, font);
        const float sep = 10f;             // gap around the dot separator
        const string dot = "·";
        var sizeDot = g.MeasureString(dot, font);
        int iconSz = icon != null ? 40 : 0;
        float gapIcon = icon != null ? 14f : 0f;

        float total = iconSz + gapIcon + size1.Width + sep + sizeDot.Width + sep + size2.Width;
        float x = (PosterWidth - total) / 2f;
        float lineH = Math.Max(size1.Height, iconSz);
        float midY = y + lineH / 2f;

        if (icon != null)
        {
            g.DrawImage(icon, new RectangleF(x, midY - iconSz / 2f, iconSz, iconSz));
            x += iconSz + gapIcon;
        }
        DrawShadowed(g, left, font, Ink, x, midY - size1.Height / 2f);   x += size1.Width + sep;
        DrawShadowed(g, dot, font, accent, x, midY - sizeDot.Height / 2f); x += sizeDot.Width + sep;
        DrawShadowed(g, right, font, accent, x, midY - size2.Height / 2f);
        return y + lineH;
    }

    private static float DrawPersona(Graphics g, WrappedSummary s, Color accent,
        Font emojiFont, Font nameFont, Font taglineFont, float y)
    {
        const int disc = 144;
        float cx = PosterWidth / 2f;
        var discRect = new RectangleF(cx - disc / 2f, y, disc, disc);

        // Accent disc with a soft radial fill and a bright ring.
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(discRect);
            using var fill = new PathGradientBrush(path)
            {
                CenterColor    = Color.FromArgb(235, accent),
                SurroundColors = new[] { Color.FromArgb(90, accent) },
            };
            g.FillPath(fill, path);
            using var ring = new Pen(Color.FromArgb(180, accent), 3f);
            g.DrawEllipse(ring, discRect);
        }

        // Emoji centred in the disc.
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        using (var br = new SolidBrush(Ink))
            g.DrawString(Emoji(s.PersonaEmoji), emojiFont, br, discRect, fmt);

        float cur = discRect.Bottom + 18;
        cur += DrawCentered(g, s.PersonaName, nameFont, Ink, cur) + 4;
        cur += DrawCentered(g, s.PersonaTagline, taglineFont, Muted, cur);
        return cur;
    }

    private static float DrawStatGrid(Graphics g, (string value, string label)[] cells,
        Font numFont, Font lblFont, Color accent, float y)
    {
        const int gap = 18;
        int cardW = (ContentW - gap) / 2;
        int cardH = 124;
        for (int i = 0; i < cells.Length; i++)
        {
            int col = i % 2, row = i / 2;
            float cx = Margin + col * (cardW + gap);
            float cy = y + row * (cardH + gap);
            var rect = new RectangleF(cx, cy, cardW, cardH);
            FillGlass(g, rect, 22);

            // Number then label, vertically centred as a pair inside the card.
            var numSize = g.MeasureString(cells[i].value, numFont);
            var lblSize = g.MeasureString(cells[i].label, lblFont);
            float blockH = numSize.Height + 2 + lblSize.Height;
            float top = cy + (cardH - blockH) / 2f;
            DrawCentered(g, cells[i].value, numFont, Ink, top, cx, cardW);
            // Small labels use the bright muted tone, not the accent — a blue/violet accent on the
            // purple cards reads poorly; the accent is saved for large, bold elements where it pops.
            DrawCentered(g, cells[i].label, lblFont, Muted, top + numSize.Height + 2, cx, cardW);
        }
        int rows = (cells.Length + 1) / 2;
        return y + rows * cardH + (rows - 1) * gap;
    }

    private static float DrawTokensBanner(Graphics g, WrappedSummary s,
        Font numFont, Font lblFont, Font noteFont, Font iconFont, Color accent, float y)
    {
        string fig = StatsFormat.Tokens(s.TotalTokens);
        var figSize = g.MeasureString(fig, numFont);
        var capSize = g.MeasureString("tokens used", lblFont);
        float iconLineH = Math.Max(g.MeasureString("X", noteFont).Height, g.MeasureString("X", iconFont).Height);

        // Headline figure + caption, then one icon line per equivalence, then the cost note.
        float blockH = figSize.Height + 2 + capSize.Height;
        foreach (var _ in s.Equivalences) blockH += iconLineH + 8;
        if (s.ShowCost) blockH += g.MeasureString("X", noteFont).Height + 8;

        float cardH = blockH + 52;
        var rect = new RectangleF(Margin, y, ContentW, cardH);
        FillGlass(g, rect, 22);

        float cur = y + (cardH - blockH) / 2f;
        cur += DrawCentered(g, fig, numFont, accent, cur) + 2;
        cur += DrawCentered(g, "tokens used", lblFont, Muted, cur) + 8;
        foreach (var item in s.Equivalences)
            cur += DrawIconLine(g, item.Emoji, item.Text, iconFont, noteFont, Muted, cur) + 8;
        if (s.ShowCost)
            cur += DrawCentered(g, $"≈ {StatsFormat.Cost(s.EstimatedCost)} of equivalent API value",
                noteFont, Faint, cur) + 8;

        return y + cardH;
    }

    private static float DrawTopPicks(Graphics g, WrappedSummary s, Font lblFont, Font valFont,
        Color accent, float y)
    {
        y += DrawCentered(g, "YOUR TOP PICKS", lblFont, accent, y) + 14;

        var picks = new (string label, string? value)[]
        {
            ("PROJECT", s.TopProject),
            ("GO-TO TOOL", s.TopTool),
            ("FAVOURITE MODEL", s.TopModel),
        };
        const int gap = 16;
        int cardW = (ContentW - gap * 2) / 3;
        int cardH = 104;
        for (int i = 0; i < picks.Length; i++)
        {
            float cx = Margin + i * (cardW + gap);
            var rect = new RectangleF(cx, y, cardW, cardH);
            FillGlass(g, rect, 18);

            string value = picks[i].value ?? "—";
            var lblSize = g.MeasureString(picks[i].label, lblFont);
            var valSize = g.MeasureString(value, valFont, cardW - 20);
            float blockH = lblSize.Height + 6 + valSize.Height;
            float top = y + (cardH - blockH) / 2f;
            DrawCentered(g, picks[i].label, lblFont, Muted, top, cx, cardW);
            DrawCentered(g, value, valFont, Ink, top + lblSize.Height + 6, cx + 8, cardW - 16, ellipsis: true);
        }
        return y + cardH;
    }

    private static void DrawHighlights(Graphics g, WrappedSummary s, Font font, Font iconFont, float y)
    {
        foreach (var h in s.Highlights)
            y += DrawIconLine(g, h.Emoji, h.Text, iconFont, font, Ink, y) + 12;
    }

    private static void DrawFooter(Graphics g, Bitmap? icon, Font font)
    {
        const string text = "Made with Perch";
        var size = g.MeasureString(text, font);
        float y = PosterHeight - 44 - size.Height;
        if (icon != null)
        {
            int sz = 26;
            float total = sz + 10 + size.Width;
            float x = (PosterWidth - total) / 2f;
            g.DrawImage(icon, new RectangleF(x, y + (size.Height - sz) / 2f, sz, sz));
            DrawShadowed(g, text, font, Faint, x + sz + 10, y);
        }
        else
        {
            DrawCentered(g, text, font, Faint, y);
        }
    }

    // Strips emoji variation selectors (U+FE0F / U+FE0E). GDI+ mis-positions an emoji glyph that
    // carries a trailing FE0F — most visibly at large sizes, where 🛠️ floated above the persona disc.
    // The base codepoint still renders in colour from Segoe UI Emoji.
    private static string Emoji(string s) => new string(s.Where(c => c != (char)0xFE0F && c != (char)0xFE0E).ToArray());

    // ── Drawing helpers ────────────────────────────────────────────────────────────
    private static void FillGlass(Graphics g, RectangleF rect, int radius)
    {
        using var path = RoundedRect(rect, radius);
        using var fill = new SolidBrush(GlassFill);
        g.FillPath(fill, path);
        using var pen = new Pen(GlassBorder, 1.5f);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRect(RectangleF r, int radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Draws a centred [emoji] [text] pair as two runs — the emoji in a colour-emoji font, the text in
    // the body font — because GDI+ renders a mixed string's emoji as a tofu box. Returns the line
    // height so the caller can advance.
    private static float DrawIconLine(Graphics g, string rawEmoji, string text,
        Font emojiFont, Font textFont, Color textColor, float y)
    {
        string emoji = Emoji(rawEmoji);
        var eSize = g.MeasureString(emoji, emojiFont);
        var tSize = g.MeasureString(text, textFont);
        const float gap = 8f;
        float lineH = Math.Max(eSize.Height, tSize.Height);
        float x = (PosterWidth - (eSize.Width + gap + tSize.Width)) / 2f;
        using (var eBr = new SolidBrush(Ink))
            g.DrawString(emoji, emojiFont, eBr, x, y + (lineH - eSize.Height) / 2f);
        DrawShadowed(g, text, textFont, textColor, x + eSize.Width + gap, y + (lineH - tSize.Height) / 2f);
        return lineH;
    }

    // Draws one centred line across the full content width, returning its measured height so the
    // caller can advance. Height is derived from the font (never hard-coded), so glyphs never clip.
    private static float DrawCentered(Graphics g, string text, Font font, Color color, float y) =>
        DrawCentered(g, text, font, color, y, Margin, ContentW);

    private static float DrawCentered(Graphics g, string text, Font font, Color color,
        float y, float x, float width, bool ellipsis = false)
    {
        var size = g.MeasureString(text, font, (int)width);
        using var fmt = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Near,
            Trimming      = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None,
            FormatFlags   = ellipsis ? StringFormatFlags.NoWrap : 0,
        };
        using (var sh = new SolidBrush(ShadowColor))
            g.DrawString(text, font, sh, new RectangleF(x, y + ShadowDy, width, size.Height + 4), fmt);
        using (var br = new SolidBrush(color))
            g.DrawString(text, font, br, new RectangleF(x, y, width, size.Height + 4), fmt);
        return size.Height;
    }

    // Draws a left-aligned text run at a point with the same soft shadow.
    private static void DrawShadowed(Graphics g, string text, Font font, Color color, float x, float y)
    {
        using (var sh = new SolidBrush(ShadowColor))
            g.DrawString(text, font, sh, x, y + ShadowDy);
        using (var br = new SolidBrush(color))
            g.DrawString(text, font, br, x, y);
    }
}
