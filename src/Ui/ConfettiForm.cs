namespace Perch.Ui;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

/// <summary>
/// A click-through, non-activating, full-screen layered window that erupts a burst of confetti and
/// lets it flutter down under gravity, then vanishes — the pay-off for an (experimental, opt-in)
/// "confetti finish" armed on a session. Fired once per finish by the owning context via
/// <see cref="Launch"/>; it drives itself from there (animate → empty → hide), so there's nothing to
/// turn off. Purely for fun.
///
/// Like <see cref="GlowForm"/> it paints into a premultiplied 32bpp bitmap pushed with
/// UpdateLayeredWindow (the only way to get per-pixel alpha over the whole desktop). Unlike the glow,
/// every frame is a fresh render: the particles are simulated forward and redrawn each tick. Confetti
/// is drawn opaque with anti-aliasing off, so every pixel is either fully clear or fully solid — which
/// means straight ARGB already equals premultiplied ARGB and no extra premultiply step is needed.
/// </summary>
internal sealed class ConfettiForm : Form
{
    // A single fluttering scrap of paper. Position/velocity are in screen-local pixels per tick.
    private struct Particle
    {
        public float X, Y;          // top-left-ish centre, screen-local
        public float Vx, Vy;        // velocity per tick (Vy grows with gravity)
        public float Rot, RotSpeed; // rotation (radians) and spin per tick
        public float SwayPhase, SwaySpeed, SwayAmp;   // horizontal flutter
        public float W, H;          // half-extents of the scrap
        public Color Color;
    }

    // Festive palette — deliberately loud.
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 92, 92),    // red
        Color.FromArgb(255, 176, 46),   // gold
        Color.FromArgb(255, 236, 92),   // yellow
        Color.FromArgb(92, 214, 122),   // green
        Color.FromArgb(78, 176, 255),   // blue
        Color.FromArgb(178, 120, 255),  // purple
        Color.FromArgb(255, 122, 205),  // pink
        Color.FromArgb(94, 234, 212),   // teal
    };

    private const int   TickMs         = 33;    // ~30 fps
    private const float Gravity        = 0.85f; // px/tick² pulling everything back down
    private const float AirDrag        = 0.992f;// gentle horizontal bleed so streams settle
    private const int   PerPopper      = 150;   // scraps launched from each bottom corner
    private const int   MaxTicks       = 450;   // hard stop (~15s) so a stray particle can't run forever

    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private Bitmap? _frame;      // reused render surface, sized to the current screen
    private Rectangle _bounds;   // screen currently covered
    private int _ticks;

    public ConfettiForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        Visible         = false;

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => Step();
    }

    // Show without stealing focus from the user's terminal.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
                        | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE
                        | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Erupt a fresh burst of confetti across <paramref name="screenBounds"/>. Safe to call
    /// while a previous burst is still falling — it simply tops up the party (and re-targets the screen
    /// if it moved), so two sessions finishing at once celebrate together rather than cutting each
    /// other off.</summary>
    public void Launch(Rectangle screenBounds)
    {
        if (IsDisposed) return;

        _ = Handle;   // force handle creation so the layered style is applied before the first blit

        // Re-target (and rebuild the render surface) if the screen changed or we're starting cold.
        if (_frame == null || screenBounds != _bounds)
        {
            _bounds = screenBounds;
            _frame?.Dispose();
            _frame = new Bitmap(Math.Max(1, screenBounds.Width), Math.Max(1, screenBounds.Height),
                                PixelFormat.Format32bppArgb);
            if (Bounds != screenBounds) Bounds = screenBounds;
        }

        SpawnBurst();
        _ticks = 0;   // a fresh launch renews the hard-stop budget

        if (!Visible) Visible = true;    // ShowWithoutActivation → no-activate show
        if (!_timer.Enabled) _timer.Start();
        RenderAndBlit();
    }

    // Fires two poppers, one from each bottom corner, spraying up and toward the middle with a wide
    // spread — the classic 🎉 fan. Everything else is gravity and flutter.
    private void SpawnBurst()
    {
        int w = _bounds.Width, h = _bounds.Height;
        AddPopper(fromLeft: true,  originX: w * 0.06f, originY: h + 8);
        AddPopper(fromLeft: false, originX: w * 0.94f, originY: h + 8);
    }

    private void AddPopper(bool fromLeft, float originX, float originY)
    {
        for (int i = 0; i < PerPopper; i++)
        {
            // Aim up and inward: left popper fans toward the upper-right, right popper toward upper-left.
            // Angle measured from straight up, spread ±35°, biased inward.
            float spread = ((float)_rng.NextDouble() - 0.5f) * 1.2f;      // ~±0.6 rad
            float inward = fromLeft ? 0.45f : -0.45f;                     // lean toward centre
            float angle  = spread + inward;
            float speed  = 22f + (float)_rng.NextDouble() * 16f;          // initial launch speed

            var p = new Particle
            {
                X         = originX + ((float)_rng.NextDouble() - 0.5f) * 24f,
                Y         = originY,
                Vx        = (float)Math.Sin(angle) * speed,
                Vy        = -(float)Math.Cos(angle) * speed,             // negative = upward
                Rot       = (float)(_rng.NextDouble() * Math.PI * 2),
                RotSpeed  = ((float)_rng.NextDouble() - 0.5f) * 0.5f,
                SwayPhase = (float)(_rng.NextDouble() * Math.PI * 2),
                SwaySpeed = 0.12f + (float)_rng.NextDouble() * 0.14f,
                SwayAmp   = 0.6f + (float)_rng.NextDouble() * 1.4f,
                W         = 3f + (float)_rng.NextDouble() * 3f,
                H         = 5f + (float)_rng.NextDouble() * 5f,
                Color     = Palette[_rng.Next(Palette.Length)],
            };
            _particles.Add(p);
        }
    }

    // Advance the simulation one tick, drop anything that has fallen off the bottom, then repaint.
    // When the last scrap is gone (or the hard-stop budget is spent) the party is over — hide and rest.
    private void Step()
    {
        float floor = _bounds.Height + 40f;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Vy       += Gravity;
            p.Vx       *= AirDrag;
            p.SwayPhase += p.SwaySpeed;
            p.X        += p.Vx + (float)Math.Sin(p.SwayPhase) * p.SwayAmp;
            p.Y        += p.Vy;
            p.Rot      += p.RotSpeed;

            if (p.Y > floor)
                _particles.RemoveAt(i);
            else
                _particles[i] = p;
        }

        if (++_ticks >= MaxTicks || _particles.Count == 0)
        {
            Stop();
            return;
        }

        RenderAndBlit();
    }

    private void Stop()
    {
        _timer.Stop();
        _particles.Clear();
        if (Visible) Visible = false;
    }

    // Draw the current particle set into the reused surface and push it to the screen. Anti-aliasing is
    // off so every drawn pixel is fully opaque (alpha 255) or untouched (alpha 0) — straight ARGB then
    // equals premultiplied ARGB, which is what UpdateLayeredWindow's AC_SRC_ALPHA wants.
    private void RenderAndBlit()
    {
        if (_frame == null || IsDisposed) return;

        using (var g = Graphics.FromImage(_frame))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.CompositingMode = CompositingMode.SourceCopy;   // write pixels verbatim, no blending
            foreach (var p in _particles)
            {
                var state = g.Save();
                g.TranslateTransform(p.X, p.Y);
                g.RotateTransform(p.Rot * 180f / (float)Math.PI);
                using var brush = new SolidBrush(p.Color);
                g.FillRectangle(brush, -p.W, -p.H, p.W * 2, p.H * 2);
                g.Restore(state);
            }
        }

        Blit(_frame);
    }

    private void Blit(Bitmap bmp)
    {
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp             = NativeMethods.AC_SRC_OVER,
            BlendFlags          = 0,
            SourceConstantAlpha = 255,
            AlphaFormat         = NativeMethods.AC_SRC_ALPHA,
        };
        var dst  = new NativeMethods.POINT(_bounds.X, _bounds.Y);
        var src  = new NativeMethods.POINT(0, 0);
        var size = new NativeMethods.SIZE(bmp.Width, bmp.Height);

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc    = NativeMethods.CreateCompatibleDC(screenDc);
        // GetHbitmap does a straight 32bpp copy, so the opaque/clear pixels survive into the GDI bitmap.
        IntPtr hBitmap  = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = NativeMethods.SelectObject(memDc, hBitmap);
        try
        {
            NativeMethods.UpdateLayeredWindow(
                Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, oldBmp);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _frame?.Dispose();
        }
        base.Dispose(disposing);
    }
}
