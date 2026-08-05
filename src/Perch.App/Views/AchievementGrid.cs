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
    private static readonly IBrush TitleBrush = new SolidColorBrush(Palette.Title);

    private static readonly IBrush BronzeBg = new SolidColorBrush(Color.FromRgb(58, 42, 30));
    private static readonly IBrush SilverBg = new SolidColorBrush(Color.FromRgb(44, 48, 58));
    private static readonly IBrush GoldBg   = new SolidColorBrush(Color.FromRgb(64, 54, 26));
    private static readonly IBrush BronzeInk = new SolidColorBrush(Color.FromRgb(214, 158, 110));
    private static readonly IBrush SilverInk = new SolidColorBrush(Color.FromRgb(200, 208, 222));
    private static readonly IBrush GoldInk   = new SolidColorBrush(Color.FromRgb(240, 200, 96));
    // Progress bar (locked quota badges only): a dark track with a tier-ink fill showing how close you are.
    private static readonly IBrush ProgressTrack = new SolidColorBrush(Color.FromArgb(150, 90, 90, 110));

    private const double NameSize = 12, DescSize = 11, CatSize = 10, TileGap = 10, TilePadH = 10, TilePadV = 12, IconGap = 5, BarH = 4;
    // Grouped variant only: the section-header line, the gap below it before its tiles, and the gap between
    // one group's tiles and the next group's header.
    private const double GroupHeadSize = 14, GroupHeadGap = 8, GroupGap = 20;

    /// <summary>"37 / 62 unlocked" — counts levels reached across every family, not just tiles.</summary>
    public static string Tally(IReadOnlyList<Achievement> badges) =>
        $"{badges.Sum(b => b.Level)} / {badges.Sum(b => b.MaxLevel)} unlocked";

    /// <summary>Draws the trophy grid at <paramref name="x"/>,<paramref name="y"/> across
    /// <paramref name="innerW"/>, returning the y just below it. Columns flow to the width (target tile
    /// width <paramref name="targetTileW"/>); <paramref name="emojiSize"/> sizes the icon, and
    /// <paramref name="showDescription"/> adds each badge's criteria line (the roomy window variant) or
    /// omits it (the compact dashboard section). With <paramref name="grouped"/> the tiles are split into
    /// themed sections (Tokens, Activity, …), each under its own header; otherwise they flow as one
    /// tier-sorted wall (the compact dashboard).</summary>
    public static double Draw(DrawingContext? ctx, IReadOnlyList<Achievement> badges,
        double x, double y, double innerW, double targetTileW, double emojiSize, bool showDescription,
        bool grouped = false)
    {
        if (badges.Count == 0) return y;

        var g = Compute(badges, innerW, targetTileW, emojiSize, showDescription, grouped);
        if (ctx != null)
        {
            foreach (var h in g.Headers)
                DrawHeader(ctx, h, x, y + h.Y, innerW);
            foreach (var t in g.Tiles)
                DrawTile(ctx, t.Badge, t.Rect.Translate(new Vector(x, y)), emojiSize,
                    g.EmojiH, g.NameH, g.CatH, g.DescLineH, showDescription);
        }
        return y + g.TotalHeight;
    }

    /// <summary>Maps a point (in the same space <see cref="Draw"/> was given <paramref name="x"/>/
    /// <paramref name="y"/>) to the badge whose tile it lands on, or null. Uses the exact layout
    /// <see cref="Draw"/> does, so a click can never target a tile other than the one drawn.</summary>
    public static Achievement? HitTest(IReadOnlyList<Achievement> badges, Point p,
        double x, double y, double innerW, double targetTileW, double emojiSize, bool showDescription,
        bool grouped = false)
    {
        if (badges.Count == 0) return null;
        var g = Compute(badges, innerW, targetTileW, emojiSize, showDescription, grouped);
        foreach (var t in g.Tiles)
            if (t.Rect.Translate(new Vector(x, y)).Contains(p)) return t.Badge;
        return null;
    }

    // One tile, placed at its rect relative to the grid origin (Draw/HitTest translate by x,y).
    private readonly record struct Placed(Achievement Badge, Rect Rect);
    // One section header: its theme name, the earned/total tally for the section, and its top-left y
    // (relative to the grid origin). Empty in the ungrouped variant.
    private readonly record struct Header(string Name, int Earned, int Total, double Y);

    // The layout both Draw and HitTest share: every placed tile, the section headers, the total height, and
    // the measured text heights that set a tile's (uniform) height. Kept in one place so paint and hit-test
    // can never disagree about where a tile is.
    private readonly record struct GridLayout(
        IReadOnlyList<Placed> Tiles, IReadOnlyList<Header> Headers, double TotalHeight,
        double EmojiH, double NameH, double CatH, double DescLineH);

    private static GridLayout Compute(IReadOnlyList<Achievement> badges, double innerW,
        double targetTileW, double emojiSize, bool showDescription, bool grouped)
    {
        int cols = Math.Max(2, (int)((innerW + TileGap) / (targetTileW + TileGap)));
        double tileW = (innerW - TileGap * (cols - 1)) / cols;

        double emojiH = OverlayDraw.Text("X", emojiSize, FgBrush).Height;
        double nameH = OverlayDraw.Text("X", NameSize, FgBrush, FontWeight.SemiBold).Height;
        double catH = OverlayDraw.Text("X", CatSize, MutedBrush).Height;
        double descLineH = OverlayDraw.Text("X", DescSize, MutedBrush).Height;
        double descH = showDescription ? descLineH * 2 + 2 : 0;   // up to two wrapped lines
        // A category-label row (grey "Tokens · Lvl 3/5", blank on uncategorised one-offs) and a reserved
        // bar row at the bottom, so every tile is the same height whichever kind of badge it is.
        double tileH = TilePadV + emojiH + IconGap + nameH + 2 + catH + (showDescription ? IconGap + descH : 0)
                     + IconGap + BarH + TilePadV;
        double headH = OverlayDraw.Text("X", GroupHeadSize, TitleBrush, FontWeight.SemiBold).Height + GroupHeadGap;

        // Grouped → one section per theme, in the catalogue's declared order (GroupBy preserves first-seen
        // order). Ungrouped → a single unnamed section holding every badge. Within a section, earned first,
        // shiniest tier first, then stable by name — so each block leads with its wins.
        IEnumerable<IGrouping<string, Achievement>> sections = grouped
            ? badges.GroupBy(b => b.Group)
            : badges.GroupBy(_ => "");

        var tiles = new List<Placed>(badges.Count);
        var headers = new List<Header>();
        double cy = 0;

        foreach (var section in sections)
        {
            var ordered = section
                .OrderByDescending(b => b.Earned)
                .ThenByDescending(b => (int)b.Tier)
                .ThenBy(b => b.Name, StringComparer.Ordinal)
                .ToList();
            if (ordered.Count == 0) continue;

            if (grouped)
            {
                headers.Add(new Header(section.Key, ordered.Count(b => b.Earned), ordered.Count, cy));
                cy += headH;
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                int col = i % cols, row = i / cols;
                tiles.Add(new Placed(ordered[i],
                    new Rect(col * (tileW + TileGap), cy + row * (tileH + TileGap), tileW, tileH)));
            }

            int rows = (ordered.Count + cols - 1) / cols;
            cy += rows * tileH + (rows - 1) * TileGap + (grouped ? GroupGap : 0);
        }
        if (grouped && headers.Count > 0) cy -= GroupGap;   // no trailing gap after the last section

        return new GridLayout(tiles, headers, cy, emojiH, nameH, catH, descLineH);
    }

    // A section header: the theme name in title ink on the left, its "earned/total" tally muted on the
    // right — a quiet ruler between blocks so related trophies read as a set.
    private static void DrawHeader(DrawingContext ctx, Header h, double x, double y, double innerW)
    {
        var name = OverlayDraw.Text(h.Name, GroupHeadSize, TitleBrush, FontWeight.SemiBold);
        ctx.DrawText(name, new Point(x, y));
        var tally = OverlayDraw.Text($"{h.Earned}/{h.Total}", CatSize + 1, MutedBrush);
        ctx.DrawText(tally, new Point(x + innerW - tally.Width, y + (name.Height - tally.Height) / 2));
    }

    private static void DrawTile(DrawingContext ctx, Achievement b, Rect r, double emojiSize,
        double emojiH, double nameH, double catH, double descLineH, bool showDescription)
    {
        var (bg, ink) = b.Tier switch
        {
            AchievementTier.Gold   => (GoldBg, GoldInk),
            AchievementTier.Silver => (SilverBg, SilverInk),
            _                      => (BronzeBg, BronzeInk),
        };
        OverlayDraw.Panel(ctx, r, bg, null, 10);

        // A locked secret stays a mystery: a "❓" box named "???", with only its cryptic hint (already in
        // Description) to go on. Earning it reveals the real emoji and name.
        bool masked = b is { Secret: true, Earned: false };
        string emojiText = masked ? "❓" : b.Emoji;
        string nameText = masked ? "???" : b.Name;

        double cy = r.Y + TilePadV;

        // Emoji centred near the top, in the colour-emoji face (variation selectors stripped so the base
        // codepoint renders in colour rather than nudging its metrics).
        var emoji = new FormattedText(StripVariation(emojiText), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, EmojiFace, emojiSize, FgBrush);
        ctx.DrawText(emoji, new Point(r.X + (r.Width - emoji.Width) / 2, cy));
        cy += emojiH + IconGap;

        // Name, tinted to the tier, single line, truncated to the tile.
        var name = OverlayDraw.Text(OverlayDraw.Truncate(nameText, NameSize, r.Width - 2 * TilePadH, FontWeight.SemiBold),
            NameSize, ink, FontWeight.SemiBold);
        ctx.DrawText(name, new Point(r.X + (r.Width - name.Width) / 2, cy));
        cy += nameH + 2;

        // The grey category label — what a levelled family compares ("Tokens · Lvl 3/5", or "· MAX" when
        // topped out). Blank for uncategorised one-offs, which just leave the reserved row empty.
        if (b.Category.Length > 0)
        {
            string cat = b.Level >= b.MaxLevel ? $"{b.Category} · MAX" : $"{b.Category} · Lvl {b.Level}/{b.MaxLevel}";
            var catText = OverlayDraw.Text(OverlayDraw.Truncate(cat, CatSize, r.Width - 2 * TilePadH), CatSize, MutedBrush);
            ctx.DrawText(catText, new Point(r.X + (r.Width - catText.Width) / 2, cy));
        }
        cy += catH;

        // Criteria / next-target line(s), centred and wrapped to at most two lines (roomy variant only).
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

        // Locked (no level reached) → a scrim over the whole tile so even the colour emoji reads as dimmed.
        if (!b.Earned)
            OverlayDraw.Panel(ctx, r, LockedScrim, null, 10);

        // Completion bar showing progress toward the next level — for a locked tile (drawn over the scrim so
        // it stays bright) and an earned-but-climbing one alike. Null progress (maxed / conditional) = none.
        if (b.Progress is { } p)
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
