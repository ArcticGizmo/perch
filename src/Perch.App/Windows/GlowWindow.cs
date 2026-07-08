using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Shows/hides a soft coloured screen-edge glow that gently breathes — an ambient "a session needs you"
/// cue that catches the eye without a toast. A UI-side seam so the app depends on the behaviour, not the
/// concrete window; Phase 7 can reimplement per-OS if the transparent-window approach needs help.
/// </summary>
internal interface IAmbientGlow
{
    /// <summary>Light the glow around <paramref name="screen"/> in <paramref name="color"/> and pulse it,
    /// (re)rendering only when the screen or colour changed. Safe to call every scan.</summary>
    void ShowGlow(Screen screen, Color color);

    /// <summary>Fade the glow out and stop pulsing (the rendered bitmap is kept for a quick re-show).</summary>
    void HideGlow();
}

/// <summary>
/// The Avalonia port of <c>GlowForm</c>: a click-through, no-activate, transparent, topmost window
/// covering one screen, showing an inward edge-gradient. Where the WinForms one pushed a premultiplied
/// bitmap via UpdateLayeredWindow and pulsed the layered SourceConstantAlpha, here the edge bitmap is
/// rendered once (per screen/colour) into a <see cref="WriteableBitmap"/> the compositor blends, and the
/// breathing pulse is just the window's <see cref="Window.Opacity"/> — no re-render per frame.
/// </summary>
public sealed class GlowWindow : Window, IAmbientGlow
{
    private const int GlowThicknessDip = 52; // soft-glow band width (logical px, DPI-scaled at render)
    private const int PeakAlpha = 210;       // baked-in per-pixel peak opacity at the very edge
    private const double PulseMin = 105 / 255.0, PulseMax = 1.0, PulseStep = 0.10;

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private DispatcherTimer? _pulse;
    private double _phase;

    private PixelRect _renderedBounds;
    private Color _renderedColor;
    private bool _rendered;

    public GlowWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _image;
    }

    public void ShowGlow(Screen screen, Color color)
    {
        var b = screen.Bounds; // physical pixels
        if (!_rendered || b != _renderedBounds || color != _renderedColor)
            Render(b, color, screen.Scaling);

        double scale = screen.Scaling;
        Position = b.Position;
        Width = b.Width / scale;
        Height = b.Height / scale;

        if (!IsVisible) Show();
        MakeClickThrough();

        _pulse ??= CreatePulse();
        if (!_pulse.IsEnabled) _pulse.Start();
    }

    public void HideGlow()
    {
        _pulse?.Stop();
        if (IsVisible) Hide();
    }

    private DispatcherTimer CreatePulse()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        t.Tick += (_, _) =>
        {
            _phase += PulseStep;
            double s = (Math.Sin(_phase) + 1) / 2;              // 0..1
            // Breathe the *content* opacity, not the window's. Avalonia implements Window.Opacity on
            // Windows by toggling WS_EX_LAYERED and rewriting the window's extended styles every time it
            // changes — which strips the WS_EX_TRANSPARENT (click-through) bit we set, so the glow starts
            // eating clicks. The content image's opacity is a compositor-level blend that never touches
            // the HWND styles, so click-through survives.
            _image.Opacity = PulseMin + s * (PulseMax - PulseMin);
        };
        return t;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeClickThroughNoActivate(h.Handle);
    }

    // Builds the edge-glow bitmap: a pixel's alpha falls off quadratically with its distance from the
    // nearest screen edge, so the four edges and their corners read as one continuous frame of light.
    // Straight (un-premultiplied) BGRA — the compositor premultiplies. Mirrors GlowForm.Render.
    private void Render(PixelRect bounds, Color color, double scaling)
    {
        _renderedBounds = bounds;
        _renderedColor = color;
        int thickness = Math.Max(1, (int)(GlowThicknessDip * scaling));
        _image.Source = BuildGlowBitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), thickness, color);
        _rendered = true;
    }

    /// <summary>Builds the edge-glow bitmap: a pixel's alpha falls off quadratically with its distance
    /// from the nearest edge, so the four edges and corners read as one continuous frame of light.
    /// Straight (un-premultiplied) BGRA — the compositor premultiplies. Mirrors GlowForm.Render.
    /// Internal so the headless render harness can eyeball it.</summary>
    internal static WriteableBitmap BuildGlowBitmap(int w, int h, int thickness, Color color)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        var row = new byte[fb.RowBytes];
        for (int y = 0; y < h; y++)
        {
            Array.Clear(row);
            int dyTop = Math.Min(y, h - 1 - y);
            for (int x = 0; x < w; x++)
            {
                int d = Math.Min(Math.Min(x, w - 1 - x), dyTop);
                if (d >= thickness) continue;
                float f = (thickness - d) / (float)thickness; // 1 at the edge → 0 inward
                int a = (int)(PeakAlpha * f * f);
                if (a <= 0) continue;
                int o = x * 4;
                row[o + 0] = color.B;
                row[o + 1] = color.G;
                row[o + 2] = color.R;
                row[o + 3] = (byte)a;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
        }
        return bmp;
    }
}
