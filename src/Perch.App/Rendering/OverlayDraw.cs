using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Perch.Data;

namespace Perch.Avalonia.Rendering;

/// <summary>
/// Shared owner-drawing primitives for the overlay — the Avalonia counterpart of the WinForms
/// <c>PaintKit</c>. Centralises rounded-rect / pill drawing and, crucially, the CLAUDE.md rule that
/// text is sized from the font's line height (<see cref="FormattedText.Height"/>), never a magic pixel
/// value, so glyphs never clip on a DPI change.
/// </summary>
internal static class OverlayDraw
{
    // Review fixes CP23: the overlay repaints on a 60ms pulse, a per-frame chase and a per-second tick, and
    // used to build a fresh Typeface + FormattedText (and so re-shape) for every string on every frame. Text is
    // now shaped once and reused from an LRU keyed by what determines its look.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<FontWeight, Typeface> Faces = new();

    /// <summary>The overlay's default typeface. WithInterFont() makes Inter the default family, so
    /// <see cref="Typeface.Default"/> resolves to Inter — matching the rest of the app. Cached per weight.</summary>
    public static Typeface Face(FontWeight weight = FontWeight.Normal) =>
        Faces.GetOrAdd(weight, w => new Typeface(FontFamily.Default, FontStyle.Normal, w));

    // Shaped text by (text, size, weight, colour, emoji face). Keyed by the brush's colour rather than the brush
    // object: callers often pass a brush built that frame, and the palette's cached brushes change colour in
    // place on a theme swap, so a reference key would either never hit or hand back the old colour. The cached
    // FormattedText paints with its own immutable brush of that colour.
    private readonly record struct TextKey(string Text, double Size, FontWeight Weight, Color Color, double Opacity, bool Emoji);
    private static readonly LruCache<TextKey, FormattedText> TextCache = new(1024);

    private static readonly LruCache<(Color, double), IImmutableSolidColorBrush> BrushCache = new(512);

    /// <summary>A shared immutable brush of <paramref name="color"/> — for paint code that would otherwise build
    /// a <see cref="SolidColorBrush"/> per call.</summary>
    public static IBrush Brush(Color color, double opacity = 1.0) =>
        BrushCache.GetOrAdd((color, opacity), k => new ImmutableSolidColorBrush(k.Item1, k.Item2));

    private static readonly LruCache<(Color, double, PenLineCap, PenLineJoin), IPen> SolidPenCache = new(4096);

    /// <summary>A shared immutable pen of one colour and width, for animation paths that stroke hundreds of
    /// segments a frame from a repeating set of colours (the attention chase's comet).</summary>
    public static IPen SolidPen(Color color, double thickness, PenLineCap lineCap = PenLineCap.Flat,
        PenLineJoin lineJoin = PenLineJoin.Miter) =>
        SolidPenCache.GetOrAdd((color, thickness, lineCap, lineJoin),
            k => new ImmutablePen((IImmutableBrush)Brush(k.Item1), k.Item2, null, k.Item3, k.Item4));

    /// <summary>
    /// A pen for one paint call — same parameters as <see cref="global::Avalonia.Media.Pen"/>'s constructor. A
    /// <see cref="global::Avalonia.Media.Pen"/> is a full AvaloniaObject (property store, change notifications),
    /// about a kilobyte each; the attention chase alone built thousands per frame, which was nearly all of the
    /// overlay's per-frame allocation (CP23). An <see cref="ImmutablePen"/> is a plain object a fraction of the
    /// size. Keep <c>new Pen</c> for pens stored in fields.
    /// </summary>
    public static IPen Pen(IBrush? brush, double thickness = 1, IDashStyle? dashStyle = null,
        PenLineCap lineCap = PenLineCap.Flat, PenLineJoin lineJoin = PenLineJoin.Miter, double miterLimit = 10) =>
        new ImmutablePen(
            brush as IImmutableBrush ?? brush?.ToImmutable(),
            thickness,
            dashStyle is null ? null : dashStyle as ImmutableDashStyle ?? new ImmutableDashStyle(dashStyle.Dashes, dashStyle.Offset),
            lineCap, lineJoin, miterLimit);

    /// <summary>Fills (and optionally strokes) a rounded rectangle.</summary>
    public static void Panel(DrawingContext ctx, Rect r, IBrush? fill, IPen? border, double radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double d = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        ctx.DrawRectangle(fill, border, new RoundedRect(r, d));
    }

    /// <summary>Fills a "pill" bar — a rounded rectangle with fully-rounded ends.</summary>
    public static void Pill(DrawingContext ctx, IBrush brush, Rect r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        double radius = Math.Min(r.Height, r.Width) / 2;
        ctx.DrawRectangle(brush, null, new RoundedRect(r, radius));
    }

    /// <summary>A shaped <see cref="FormattedText"/> ready to draw. Read <c>.Height</c>/<c>.Width</c>
    /// to position it — never assume a pixel line height. <b>Shared and cached: never mutate the result</b>
    /// (<c>MaxTextWidth</c>, <c>Trimming</c>, <c>SetForegroundBrush</c>…); use <see cref="NewText"/> for that.</summary>
    public static FormattedText Text(string s, double size, IBrush brush,
        FontWeight weight = FontWeight.Normal) => Shaped(s, size, brush, weight, emoji: false);

    /// <summary>A fresh, uncached <see cref="FormattedText"/> the caller may mutate.</summary>
    public static FormattedText NewText(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face(weight), size, brush);

    // The platform emoji typeface, tried before Inter so an emoji glyph resolves rather than falling to tofu.
    private static readonly Typeface EmojiFace =
        new(new FontFamily("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji, Twemoji Mozilla"));

    /// <summary>A shaped <see cref="FormattedText"/> for an emoji glyph using the platform emoji font, so the
    /// mood/reaction glyphs render (as colour where the toolkit supports it, else a monochrome outline) instead
    /// of falling through Inter to a tofu box. Shared and cached, like <see cref="Text"/>.</summary>
    public static FormattedText Emoji(string s, double size, IBrush brush) =>
        Shaped(s, size, brush, FontWeight.Normal, emoji: true);

    private static FormattedText Shaped(string? s, double size, IBrush brush, FontWeight weight, bool emoji)
    {
        s ??= "";
        // Only a solid colour has a value to key on; anything else (a gradient) is built fresh each time.
        if (brush is not ISolidColorBrush solid)
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                emoji ? EmojiFace : Face(weight), size, brush);
        return TextCache.GetOrAdd(new TextKey(s, size, weight, solid.Color, solid.Opacity, emoji), k =>
            new FormattedText(k.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                k.Emoji ? EmojiFace : Face(k.Weight), k.Size, Brush(k.Color, k.Opacity)));
    }

    /// <summary>Draws an emoji built via <see cref="Emoji"/> vertically centred on <paramref name="midY"/>, with
    /// its left edge at <paramref name="x"/>. Colour-emoji metrics don't give a clean centre: the FormattedText
    /// line box carries a tall ascent, so centring by <c>Height</c> renders the glyph too high, while centring
    /// its em box off the baseline renders it too low — so we split the difference. <paramref name="emSize"/> is
    /// the size passed to <see cref="Emoji"/>.</summary>
    public static void EmojiLeftMid(DrawingContext ctx, FormattedText ft, double x, double midY, double emSize)
        => ctx.DrawText(ft, new Point(x, EmojiTop(ft, midY, emSize)));

    /// <summary>Draws an emoji centred on (<paramref name="cx"/>, <paramref name="cy"/>) — the two-axis
    /// counterpart of <see cref="EmojiLeftMid"/>.</summary>
    public static void EmojiCentered(DrawingContext ctx, FormattedText ft, double cx, double cy, double emSize)
        => ctx.DrawText(ft, new Point(cx - ft.Width / 2, EmojiTop(ft, cy, emSize)));

    // Blend between box-centring (bias 0 → glyph too high) and em-box baseline-centring (bias 1 → too low).
    // Tuned against the live app (headless emoji metrics differ): nudge toward 0 if emoji sit too low, toward 1
    // if too high.
    private const double EmojiCenterBias = 0.4;

    // The top-of-box Y that vertically centres an emoji glyph on midY (see EmojiCenterBias).
    private static double EmojiTop(FormattedText ft, double midY, double emSize)
        => (1 - EmojiCenterBias) * (midY - ft.Height / 2)
           + EmojiCenterBias * (midY + emSize / 2 - ft.Baseline);

    /// <summary>Draws <paramref name="ft"/> left-aligned at <paramref name="x"/>, vertically centred on
    /// <paramref name="midY"/> using its measured line height (the anti-clipping rule).</summary>
    public static void TextLeftMid(DrawingContext ctx, FormattedText ft, double x, double midY)
        => ctx.DrawText(ft, new Point(x, midY - ft.Height / 2));

    /// <summary>Strokes a circular arc using GDI-style angles (0° = east, positive = clockwise in the
    /// y-down screen space), matching the WinForms <c>Graphics.DrawArc</c> calls the glyphs were built
    /// with. <paramref name="cx"/>/<paramref name="cy"/> is the circle centre, <paramref name="r"/> its
    /// radius.</summary>
    public static void Arc(DrawingContext ctx, IPen pen, double cx, double cy, double r,
        double startDeg, double sweepDeg)
    {
        Point At(double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            gc.BeginFigure(At(startDeg), isFilled: false);
            gc.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0,
                Math.Abs(sweepDeg) > 180,
                sweepDeg >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>Measured width of a string at the given size/weight (shaped once, then cached).</summary>
    public static double MeasureWidth(string s, double size, FontWeight weight = FontWeight.Normal)
        => Text(s, size, Brushes.White, weight).Width;

    private static readonly LruCache<(string, double, double, FontWeight), string> TruncateCache = new(512);

    /// <summary>Truncates <paramref name="text"/> with a trailing ellipsis so it fits
    /// <paramref name="maxWidth"/> at the given size/weight — the Avalonia counterpart of the WinForms
    /// TruncateString helper. Cuts only between graphemes (<see cref="TextFit"/>), and remembers the answer, so
    /// a repaint doesn't re-run the binary search's shaping probes.</summary>
    public static string Truncate(string text, double size, double maxWidth, FontWeight weight = FontWeight.Normal)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return TruncateCache.GetOrAdd((text, size, maxWidth, weight),
            k => TextFit.Truncate(k.Item1, k.Item3, s => MeasureWidth(s, k.Item2, k.Item4)));
    }
}
