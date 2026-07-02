namespace Perch.Ui;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Perch.Data;

/// <summary>
/// The perch bird's aggregate "mood", derived from every live session. Purely cosmetic: it drives the
/// little expression the tray/overlay bird wears, nothing else. Priority runs worst-first — one stuck
/// session panics the whole flock — so the bird always reflects the most-pressing thing on the perch.
/// </summary>
internal enum BirdMood
{
    /// <summary>Nothing running — the bird dozes (faded, a trail of "z"s).</summary>
    Dozing = 0,
    /// <summary>At least one session working away — the bird is up and alert (plain, energetic).</summary>
    Working = 1,
    /// <summary>Something needs you (done / awaiting input) — the bird flags it with a "!" badge.</summary>
    Attention = 2,
    /// <summary>A session looks stuck — the bird visibly panics (red "!" and flying sweat).</summary>
    Panic = 3,
}

/// <summary>
/// Renders the perch bird wearing each <see cref="BirdMood"/> — a subtle tint plus a hand-drawn emblem
/// composited over the base logo — and caches the results. Two products per mood: an overlay
/// <see cref="Bitmap"/> (for the header / dense-strip logo) and a tray <see cref="Icon"/>.
///
/// Everything is drawn as vector primitives at the exact target size (rather than scaled from one
/// raster), so the emblems stay crisp all the way down to the 16px tray. Instances own every bitmap and
/// icon they hand out; callers must not dispose them. Cheap enough to build lazily on first use of each
/// mood and hold for the process lifetime.
/// </summary>
internal sealed class BirdMoodArt : IDisposable
{
    // The base bird artwork (the same 256px logo the About box and quick-strip use). Null only if the
    // embedded resource can't be loaded, in which case every render degrades to "just the emblem".
    private readonly Bitmap? _base;

    // Rendered-once caches, keyed by mood.
    private readonly Dictionary<BirdMood, Bitmap> _overlayCache = new();
    private readonly Dictionary<BirdMood, Icon> _iconCache = new();

    // Side of the overlay logo bitmap. Drawn at ~18-22px, so 64px gives clean downscaling headroom.
    private const int OverlaySize = 64;

    // Tray-icon frames baked into the in-memory .ico so Windows never downscales at runtime.
    private static readonly int[] IconSizes = { 16, 20, 24, 32, 48 };

    public BirdMoodArt() => _base = EmbeddedResources.LoadBitmap("Perch.icon.png");

    /// <summary>The most-pressing mood across all live sessions (worst wins): a stuck session panics,
    /// otherwise anything needing you flags attention, otherwise any running session is "working",
    /// otherwise the flock dozes.</summary>
    public static BirdMood MoodFor(IReadOnlyList<ClaudeSession> sessions)
    {
        if (sessions.Any(s => s.IsStuck)) return BirdMood.Panic;
        if (sessions.Any(s => s.Status is SessionStatus.NeedsAttention or SessionStatus.AwaitingInput))
            return BirdMood.Attention;
        if (sessions.Any(s => s.Status == SessionStatus.Running)) return BirdMood.Working;
        return BirdMood.Dozing;
    }

    /// <summary>The overlay logo bitmap for a mood (cached). Never dispose the result.</summary>
    public Bitmap OverlayBitmap(BirdMood mood)
    {
        if (_overlayCache.TryGetValue(mood, out var bmp)) return bmp;
        bmp = RenderFrame(mood, OverlaySize);
        _overlayCache[mood] = bmp;
        return bmp;
    }

    /// <summary>The tray icon for a mood (cached). Never dispose the result.</summary>
    public Icon TrayIcon(BirdMood mood)
    {
        if (_iconCache.TryGetValue(mood, out var icon)) return icon;
        icon = BuildIcon(mood);
        _iconCache[mood] = icon;
        return icon;
    }

    // ── Rendering ──────────────────────────────────────────────────────────────
    // Paints the base bird (tinted for the mood) plus the mood emblem into a fresh transparent square
    // of the given side. All emblem geometry is expressed as fractions of the side, so it scales.
    private Bitmap RenderFrame(BirdMood mood, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96, 96);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        DrawBase(g, mood, size);

        switch (mood)
        {
            case BirdMood.Dozing:    DrawSnooze(g, size); break;
            case BirdMood.Attention: DrawBang(g, size, Theme.Orange); break;
            case BirdMood.Panic:     DrawSweat(g, size); DrawBang(g, size, Theme.Brand); break;
            case BirdMood.Working:   /* plain, alert bird — no emblem */ break;
        }

        return bmp;
    }

    // Draws the base bird to fill the frame, faded for the dozing mood (a sleepy bird is a dim bird)
    // and untouched otherwise.
    private void DrawBase(Graphics g, BirdMood mood, int size)
    {
        if (_base == null) return;
        var dest = new Rectangle(0, 0, size, size);

        if (mood == BirdMood.Dozing)
        {
            // Knock the bird back to ~70% opacity so it reads as "resting" against both the taskbar
            // and the dark overlay.
            var m = new ColorMatrix { Matrix33 = 0.70f };
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(m);
            g.DrawImage(_base, dest, 0, 0, _base.Width, _base.Height, GraphicsUnit.Pixel, attrs);
        }
        else
        {
            g.DrawImage(_base, dest);
        }
    }

    // A filled "!" badge in the top-right corner: the notification bang. The "!" is drawn as vector
    // primitives (a rounded stem + a dot) rather than a glyph so it stays legible at 16px. A thin light
    // ring separates the badge from the bird body underneath.
    private static void DrawBang(Graphics g, int size, Color badge)
    {
        float bx = size * 0.74f, by = size * 0.27f, r = size * 0.25f;

        using (var ring = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            g.FillEllipse(ring, bx - r, by - r, r * 2, r * 2);
        float ir = r * 0.82f;
        using (var fill = new SolidBrush(badge))
            g.FillEllipse(fill, bx - ir, by - ir, ir * 2, ir * 2);

        // The bang itself, white, centred in the badge.
        using var white = new SolidBrush(Color.White);
        float stemW = ir * 0.30f;
        var stem = new RectangleF(bx - stemW / 2, by - ir * 0.55f, stemW, ir * 0.72f);
        FillRoundedRect(g, stem, stemW / 2, white);
        float dr = ir * 0.17f;
        g.FillEllipse(white, bx - dr, by + ir * 0.34f, dr * 2, dr * 2);
    }

    // A trail of three "z"s rising to the top-right — the universal "asleep" cue. Each z is a stroked
    // zig-zag (top bar, diagonal, bottom bar), drawn dark-then-light so it reads over any background.
    private static void DrawSnooze(Graphics g, int size)
    {
        DrawZ(g, size * 0.62f, size * 0.42f, size * 0.24f);
        DrawZ(g, size * 0.78f, size * 0.22f, size * 0.16f);
        DrawZ(g, size * 0.90f, size * 0.07f, size * 0.11f);
    }

    // One "z" whose glyph box has top-left (x,y) and the given side.
    private static void DrawZ(Graphics g, float x, float y, float s)
    {
        var pts = new[]
        {
            new PointF(x,       y),
            new PointF(x + s,   y),
            new PointF(x,       y + s),
            new PointF(x + s,   y + s),
        };
        float w = Math.Max(1.4f, s * 0.20f);
        // Dark halo first for contrast against the pale bird, then the soft slate-blue z on top.
        using (var halo = new Pen(Color.FromArgb(150, 20, 24, 40), w * 1.9f)
               { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(halo, pts);
        using (var pen = new Pen(Color.FromArgb(235, 175, 196, 232), w)
               { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLines(pen, pts);
    }

    // Two little teardrops flying off the bird's head — the flustered "sweating bullets" look that,
    // with the red bang, sells panic. Each drop is a round-bottomed, pointed-top path with a glint.
    private static void DrawSweat(Graphics g, int size)
    {
        DrawDrop(g, size * 0.30f, size * 0.20f, size * 0.085f);
        DrawDrop(g, size * 0.15f, size * 0.34f, size * 0.065f);
    }

    private static void DrawDrop(Graphics g, float cx, float cy, float r)
    {
        using var path = new GraphicsPath();
        // Round bottom bulb with a tip drawn up and to the left (as if slung off a shaking head).
        path.AddArc(cx - r, cy - r, r * 2, r * 2, 20, 300);
        path.AddLine(cx - r * 0.94f, cy - r * 0.34f, cx - r * 1.7f, cy - r * 2.0f);
        path.CloseFigure();

        using (var fill = new SolidBrush(Color.FromArgb(240, 96, 165, 250)))
            g.FillPath(fill, path);
        using (var edge = new Pen(Color.FromArgb(180, 219, 234, 254), Math.Max(1f, r * 0.18f)))
            g.DrawPath(edge, path);
        // A small highlight glint.
        using var glint = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
        g.FillEllipse(glint, cx - r * 0.15f, cy - r * 0.35f, r * 0.5f, r * 0.5f);
    }

    private static void FillRoundedRect(Graphics g, RectangleF r, float radius, Brush brush)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.Left,        r.Top,          d, d, 180, 90);
        path.AddArc(r.Right - d,   r.Top,          d, d, 270, 90);
        path.AddArc(r.Right - d,   r.Bottom - d,   d, d,   0, 90);
        path.AddArc(r.Left,        r.Bottom - d,   d, d,  90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // ── Tray icon packing ────────────────────────────────────────────────────────
    // Builds a multi-resolution .ico (PNG-compressed frames, one per IconSizes entry) in memory and
    // returns it as an Icon, so the tray gets a true frame at its DPI instead of a runtime downscale.
    // Mirrors tools/IconGen's writer, kept local so the app has no dependency on that tool.
    private Icon BuildIcon(BirdMood mood)
    {
        var frames = new List<byte[]>();
        foreach (var s in IconSizes)
        {
            using var bmp = RenderFrame(mood, s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            frames.Add(ms.ToArray());
        }

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);                  // reserved
            w.Write((ushort)1);                  // type = icon
            w.Write((ushort)IconSizes.Length);   // image count

            int offset = 6 + IconSizes.Length * 16;
            for (int i = 0; i < IconSizes.Length; i++)
            {
                int s = IconSizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); // width  (0 = 256)
                w.Write((byte)(s >= 256 ? 0 : s)); // height (0 = 256)
                w.Write((byte)0);                  // palette count
                w.Write((byte)0);                  // reserved
                w.Write((ushort)1);                // colour planes
                w.Write((ushort)32);               // bits per pixel
                w.Write(frames[i].Length);         // bytes of image data
                w.Write(offset);                   // offset of image data
                offset += frames[i].Length;
            }
            foreach (var frame in frames)
                w.Write(frame);
        }

        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        foreach (var b in _overlayCache.Values) b.Dispose();
        foreach (var i in _iconCache.Values) i.Dispose();
        _overlayCache.Clear();
        _iconCache.Clear();
        _base?.Dispose();
    }
}
