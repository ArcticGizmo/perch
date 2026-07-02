namespace Perch.Ui;

using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// A click-through, non-activating layered window that paints a soft coloured glow around the edge of
/// a screen and gently breathes it in and out — an ambient "a session needs you" cue that catches the
/// eye without a toast. Off by default (an experimental opt-in). Driven entirely by the owning
/// context: <see cref="Activate"/> while a session needs attention, <see cref="Deactivate"/> when
/// none do.
///
/// The glow is drawn once into a premultiplied 32bpp bitmap and pushed with UpdateLayeredWindow (the
/// only way to get per-pixel alpha; the overlay's 1-bit TransparencyKey can't do a soft edge). The
/// pulse then re-blits that same bitmap with a varying SourceConstantAlpha, so a frame is just a
/// cheap opacity change — no re-render until the screen or colour actually changes.
/// </summary>
internal sealed class GlowForm : Form
{
    // Soft-glow band width in logical pixels (scaled by DPI at render time) and its peak per-pixel
    // opacity at the very edge; the pulse scales the whole window's opacity on top of this.
    private const int GlowThicknessDip = 52;
    private const int PeakAlpha        = 210;

    // Breathing bounds for the pulse's overall opacity (a fraction of PeakAlpha), and how fast it
    // advances per tick. Kept gentle — this is peripheral, not a strobe.
    private const int    PulseMinAlpha = 105;
    private const int    PulseMaxAlpha = 255;
    private const double PulseStep     = 0.10;

    private readonly System.Windows.Forms.Timer _pulse;
    private double _phase;

    // Persistent GDI resources for the current glow bitmap; kept between pulse frames so a frame only
    // re-blits. Torn down on a re-render or on dispose. The screen DC is fetched per blit instead.
    private IntPtr _memDc, _hBitmap, _oldBitmap;
    private Rectangle _bounds;   // the screen bounds currently rendered
    private Color _color;        // the glow colour currently rendered
    private bool _rendered;

    public GlowForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        Visible         = false;

        _pulse = new System.Windows.Forms.Timer { Interval = 40 };
        _pulse.Tick += (_, _) => { _phase += PulseStep; Blit(); };
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

    /// <summary>Show the glow around <paramref name="screenBounds"/> in <paramref name="color"/>,
    /// (re)rendering only when the target screen or colour has changed, and start the pulse. Safe to
    /// call every scan — it no-ops when already showing the same thing.</summary>
    public void ShowGlow(Rectangle screenBounds, Color color)
    {
        if (IsDisposed) return;

        if (!_rendered || screenBounds != _bounds || color != _color)
            Render(screenBounds, color);

        // Keep the window matched to the target screen even when already showing — this is how the
        // glow follows the overlay when it's dragged to another monitor. (Blit re-positions via
        // UpdateLayeredWindow too, but keeping Bounds honest avoids any WinForms confusion.)
        if (Bounds != screenBounds) Bounds = screenBounds;
        if (!Visible) Visible = true;   // ShowWithoutActivation makes this a no-activate show

        if (!_pulse.Enabled) _pulse.Start();
        Blit();
    }

    /// <summary>Hide the glow and stop pulsing. The rendered bitmap is kept for a quick re-show.</summary>
    public void HideGlow()
    {
        _pulse.Stop();
        if (Visible) Visible = false;
    }

    // Builds the premultiplied glow bitmap for the given screen and selects it into a memory DC. The
    // alpha at a pixel falls off with its distance from the nearest screen edge (quadratically, for a
    // soft bloom), so the four edges and their corners read as one continuous frame of light.
    private void Render(Rectangle screenBounds, Color color)
    {
        _ = Handle;   // force handle creation so DeviceDpi is valid and the layered style is applied
        ReleaseGdi();

        _bounds = screenBounds;
        _color  = color;

        int w = Math.Max(1, screenBounds.Width);
        int h = Math.Max(1, screenBounds.Height);
        int thickness = Math.Max(1, (int)(GlowThicknessDip * DeviceDpi / 96f));

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Write one row at a time, reusing a single stride-sized buffer. Interior rows are mostly
            // transparent, so the buffer is cleared each row and only the lit pixels are filled.
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(row);
                int dyTop = Math.Min(y, h - 1 - y);      // vertical distance to nearest top/bottom edge
                for (int x = 0; x < w; x++)
                {
                    int d = Math.Min(Math.Min(x, w - 1 - x), dyTop);   // distance to nearest edge
                    if (d >= thickness) continue;

                    float f = (thickness - d) / (float)thickness;      // 1 at the edge → 0 inward
                    int a = (int)(PeakAlpha * f * f);
                    if (a <= 0) continue;

                    // Premultiplied BGRA (little-endian Format32bppArgb byte order) — what
                    // UpdateLayeredWindow expects with AC_SRC_ALPHA.
                    int o = x * 4;
                    row[o + 0] = (byte)(color.B * a / 255);
                    row[o + 1] = (byte)(color.G * a / 255);
                    row[o + 2] = (byte)(color.R * a / 255);
                    row[o + 3] = (byte)a;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // GetHbitmap does a straight 32bpp copy, so the premultiplied bytes we wrote survive into the
        // GDI bitmap the layered window samples.
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memDc     = NativeMethods.CreateCompatibleDC(screenDc);
            _hBitmap   = bmp.GetHbitmap(Color.FromArgb(0));
            _oldBitmap = NativeMethods.SelectObject(_memDc, _hBitmap);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
        _rendered = true;
    }

    // Pushes the current bitmap to the screen at the pulse's current opacity. A breathing sine drives
    // SourceConstantAlpha between PulseMin/MaxAlpha; the bitmap itself is untouched.
    private void Blit()
    {
        if (!_rendered || _memDc == IntPtr.Zero || IsDisposed) return;

        double s  = (Math.Sin(_phase) + 1) / 2;   // 0..1
        byte   ca = (byte)(PulseMinAlpha + s * (PulseMaxAlpha - PulseMinAlpha));

        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp             = NativeMethods.AC_SRC_OVER,
            BlendFlags          = 0,
            SourceConstantAlpha = ca,
            AlphaFormat         = NativeMethods.AC_SRC_ALPHA,
        };
        var dst  = new NativeMethods.POINT(_bounds.X, _bounds.Y);
        var src  = new NativeMethods.POINT(0, 0);
        var size = new NativeMethods.SIZE(_bounds.Width, _bounds.Height);

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            NativeMethods.UpdateLayeredWindow(
                Handle, screenDc, ref dst, ref size, _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseGdi()
    {
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            NativeMethods.SelectObject(_memDc, _oldBitmap);
        if (_hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(_hBitmap);
        if (_memDc   != IntPtr.Zero) NativeMethods.DeleteDC(_memDc);
        _memDc = _hBitmap = _oldBitmap = IntPtr.Zero;
        _rendered = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulse.Dispose();
            ReleaseGdi();
        }
        base.Dispose(disposing);
    }
}
