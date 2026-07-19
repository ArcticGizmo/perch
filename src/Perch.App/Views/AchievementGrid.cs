using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The one owner-drawn renderer for the achievement trophy grid, shared by the compact section in
/// <see cref="StatsDashboard"/> and the roomier <see cref="AchievementsDashboard"/> window — so the two
/// can never drift. Follows the measure-or-paint idiom (no-op when the context is null, but advances the
/// y cursor identically), and draws each badge's emoji in a colour-emoji font as its own run (a mixed run
/// renders the emoji as a tofu box). Earned tiles are tier-tinted; locked tiles are dimmed under a scrim.
/// </summary>
internal static class AchievementGrid
{
    private static readonly Typeface EmojiFace =
        new(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji"));
    private static readonly IBrush LockedScrim = new SolidColorBrush(Color.FromArgb(176, 18, 18, 24));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Palette.Muted);
    private static readonly IBrush FgBrush = new SolidColorBrush(Palette.Fg);

    private static readonly IBrush BronzeBg = new SolidColorBrush(Color.FromRgb(58, 42, 30));
    private static readonly IBrush SilverBg = new SolidColorBrush(Color.FromRgb(44, 48, 58));
    private static readonly IBrush GoldBg   = new SolidColorBrush(Color.FromRgb(64, 54, 26));
    private static readonly IBrush BronzeInk = new SolidColorBrush(Color.FromRgb(214, 158, 110));
    private static readonly IBrush SilverInk = new SolidColorBrush(Color.FromRgb(200, 208, 222));
    private static readonly IBrush GoldInk   = new SolidColorBrush(Color.FromRgb(240, 200, 96));
    // Progress bar (locked quota badges only): a dark track with a tier-ink fill showing how close you are.
    private static readonly IBrush ProgressTrack = new SolidColorBrush(Color.FromArgb(150, 90, 90, 110));

    private const double NameSize = 12, DescSize = 11, TileGap = 10, TilePadH = 10, TilePadV = 12, IconGap = 5, BarH = 4;

    /// <summary>"29 / 41 unlocked".</summary>
    public static string Tally(IReadOnlyList<Achievement> badges) =>
        $"{badges.Count(b => b.Earned)} / {badges.Count} unlocked";

    /// <summary>Draws the trophy grid at <paramref name="x"/>,<paramref name="y"/> across
    /// <paramref name="innerW"/>, returning the y just below it. Columns flow to the width (target tile
    /// width <paramref name="targetTileW"/>); <paramref name="emojiSize"/> sizes the icon, and
    /// <paramref name="showDescription"/> adds each badge's criteria line (the roomy window variant) or
    /// omits it (the compact dashboard section).</summary>
    public static double Draw(DrawingContext? ctx, IReadOnlyList<Achievement> badges,
        double x, double y, double innerW, double targetTileW, double emojiSize, bool showDescription)
    {
        if (badges.Count == 0) return y;

        // Earned first, shiniest tier first, then stable by name — so the wall leads with wins.
        var ordered = badges
            .OrderByDescending(b => b.Earned)
            .ThenByDescending(b => (int)b.Tier)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();

        int cols = Math.Max(2, (int)((innerW + TileGap) / (targetTileW + TileGap)));
        double tileW = (innerW - TileGap * (cols - 1)) / cols;

        double emojiH = OverlayDraw.Text("X", emojiSize, FgBrush).Height;
        double nameH = OverlayDraw.Text("X", NameSize, FgBrush, FontWeight.SemiBold).Height;
        double descLineH = OverlayDraw.Text("X", DescSize, MutedBrush).Height;
        double descH = showDescription ? descLineH * 2 + 2 : 0;   // up to two wrapped lines
        // A reserved bar row at the bottom (drawn only for unearned quota badges, blank otherwise) so every
        // tile is the same height whether or not it shows progress.
        double tileH = TilePadV + emojiH + IconGap + nameH + (showDescription ? IconGap + descH : 0)
                     + IconGap + BarH + TilePadV;

        for (int i = 0; i < ordered.Count; i++)
        {
            int col = i % cols, row = i / cols;
            var r = new Rect(x + col * (tileW + TileGap), y + row * (tileH + TileGap), tileW, tileH);
            if (ctx != null) DrawTile(ctx, ordered[i], r, emojiSize, emojiH, nameH, descLineH, showDescription);
        }

        int rows = (ordered.Count + cols - 1) / cols;
        return y + rows * tileH + (rows - 1) * TileGap;
    }

    private static void DrawTile(DrawingContext ctx, Achievement b, Rect r, double emojiSize,
        double emojiH, double nameH, double descLineH, bool showDescription)
    {
        var (bg, ink) = b.Tier switch
        {
            AchievementTier.Gold   => (GoldBg, GoldInk),
            AchievementTier.Silver => (SilverBg, SilverInk),
            _                      => (BronzeBg, BronzeInk),
        };
        OverlayDraw.Panel(ctx, r, bg, null, 10);

        double cy = r.Y + TilePadV;

        // Emoji centred near the top, in the colour-emoji face (variation selectors stripped so the base
        // codepoint renders in colour rather than nudging its metrics).
        var emoji = new FormattedText(StripVariation(b.Emoji), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, EmojiFace, emojiSize, FgBrush);
        ctx.DrawText(emoji, new Point(r.X + (r.Width - emoji.Width) / 2, cy));
        cy += emojiH + IconGap;

        // Name, tinted to the tier, single line, truncated to the tile.
        var name = OverlayDraw.Text(OverlayDraw.Truncate(b.Name, NameSize, r.Width - 2 * TilePadH, FontWeight.SemiBold),
            NameSize, ink, FontWeight.SemiBold);
        ctx.DrawText(name, new Point(r.X + (r.Width - name.Width) / 2, cy));
        cy += nameH;

        // Criteria line(s), centred and wrapped to at most two lines — tells you how to earn a locked one.
        if (showDescription)
        {
            cy += IconGap;
            var desc = new FormattedText(b.Description, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                OverlayDraw.Face(), DescSize, MutedBrush)
            {
                MaxTextWidth = r.Width - 2 * TilePadH,
                MaxTextHeight = descLineH * 2 + 1,
                TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.WordEllipsis,
            };
            ctx.DrawText(desc, new Point(r.X + TilePadH, cy));
        }

        // Locked → a scrim over the whole tile so even the colour emoji reads as dimmed.
        if (!b.Earned)
            OverlayDraw.Panel(ctx, r, LockedScrim, null, 10);

        // Completion bar for an unearned quota badge — drawn over the scrim so it stays bright and legible,
        // since it's the actionable "how close am I" cue. Sits in the reserved bottom row.
        if (!b.Earned && b.Progress is { } p)
        {
            double barY = r.Bottom - TilePadV - BarH;
            var track = new Rect(r.X + TilePadH, barY, r.Width - 2 * TilePadH, BarH);
            OverlayDraw.Pill(ctx, ProgressTrack, track);
            double fillW = track.Width * Math.Clamp(p, 0, 1);
            if (fillW >= BarH)
                OverlayDraw.Pill(ctx, ink, new Rect(track.X, barY, fillW, BarH));
        }
    }

    // Emoji variation selectors (U+FE0F / U+FE0E) nudge glyph metrics off; drop them and the base
    // codepoint still renders in colour. Mirrors the Wrapped poster's Emoji() helper.
    private static string StripVariation(string s) =>
        string.Concat(s.Where(ch => ch != '️' && ch != '︎'));
}
