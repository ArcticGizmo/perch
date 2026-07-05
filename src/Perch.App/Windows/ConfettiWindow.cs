using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A click-through, non-activating, transparent, topmost window covering one screen that erupts a burst
/// of confetti and lets it flutter down under gravity, then hides itself — the pay-off for an
/// (experimental, opt-in) "confetti finish" armed on a session. The Avalonia port of <c>ConfettiForm</c>:
/// where the WinForms one pushed premultiplied frames via UpdateLayeredWindow, here a transparent window
/// hosts an owner-drawn <see cref="ConfettiCanvas"/> that the compositor alpha-blends for us.
/// Fired once per finish via <see cref="Launch"/>; it drives itself (animate → empty → hide).
/// </summary>
public sealed class ConfettiWindow : Window
{
    private readonly ConfettiCanvas _canvas = new();

    public ConfettiWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = _canvas;

        _canvas.Finished += () => Hide();
    }

    /// <summary>Erupt a fresh burst across <paramref name="screen"/>. Safe to call while a previous burst
    /// is still falling — it tops up the party (and re-targets the screen if it moved).</summary>
    public void Launch(Screen screen)
    {
        var b = screen.Bounds;              // physical pixels
        double scale = screen.Scaling;
        Position = b.Position;
        Width = b.Width / scale;            // cover the screen in DIPs (the canvas simulates in DIPs)
        Height = b.Height / scale;

        if (!IsVisible) Show();
        MakeClickThrough();
        _canvas.Launch();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        if (OperatingSystem.IsWindows() && TryGetPlatformHandle() is { } h)
            Perch.Platform.Windows.OverlayNativeChrome.MakeClickThroughNoActivate(h.Handle);
    }
}

/// <summary>
/// The owner-drawn confetti simulation: two corner poppers spray scraps up-and-inward, gravity + flutter
/// bring them down, and each frame redraws the live set. Raises <see cref="Finished"/> when the last
/// scrap is gone (or the ~15s hard-stop is hit). Ported straight from <c>ConfettiForm</c>'s sim.
/// </summary>
internal sealed class ConfettiCanvas : Control
{
    private struct Particle
    {
        public double X, Y, Vx, Vy, Rot, RotSpeed, SwayPhase, SwaySpeed, SwayAmp, W, H;
        public int Color;
    }

    private static readonly ImmutableSolidColorBrush[] Palette =
    [
        new(Color.FromRgb(255, 92, 92)),   // red
        new(Color.FromRgb(255, 176, 46)),  // gold
        new(Color.FromRgb(255, 236, 92)),  // yellow
        new(Color.FromRgb(92, 214, 122)),  // green
        new(Color.FromRgb(78, 176, 255)),  // blue
        new(Color.FromRgb(178, 120, 255)), // purple
        new(Color.FromRgb(255, 122, 205)), // pink
        new(Color.FromRgb(94, 234, 212)),  // teal
    ];

    private const double Gravity = 0.85, AirDrag = 0.992;
    private const int PerPopper = 150, MaxTicks = 450, TickMs = 33;

    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private DispatcherTimer? _timer;
    private int _ticks;

    /// <summary>Raised (on the UI thread) when the party is over so the host window can hide.</summary>
    public event Action? Finished;

    public void Launch()
    {
        SpawnBurst();
        _ticks = 0;
        _timer ??= CreateTimer();
        if (!_timer.IsEnabled) _timer.Start();
        InvalidateVisual();
    }

    private DispatcherTimer CreateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        t.Tick += (_, _) => Step();
        return t;
    }

    // Fires two poppers, one from each bottom corner, spraying up and toward the middle — the classic 🎉.
    private void SpawnBurst()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        AddPopper(fromLeft: true,  originX: w * 0.06, originY: h + 8);
        AddPopper(fromLeft: false, originX: w * 0.94, originY: h + 8);
    }

    private void AddPopper(bool fromLeft, double originX, double originY)
    {
        for (int i = 0; i < PerPopper; i++)
        {
            double spread = (_rng.NextDouble() - 0.5) * 1.2;      // ~±0.6 rad
            double inward = fromLeft ? 0.45 : -0.45;              // lean toward centre
            double angle  = spread + inward;
            double speed  = 22 + _rng.NextDouble() * 16;

            _particles.Add(new Particle
            {
                X = originX + (_rng.NextDouble() - 0.5) * 24,
                Y = originY,
                Vx = Math.Sin(angle) * speed,
                Vy = -Math.Cos(angle) * speed,   // negative = upward
                Rot = _rng.NextDouble() * Math.PI * 2,
                RotSpeed = (_rng.NextDouble() - 0.5) * 0.5,
                SwayPhase = _rng.NextDouble() * Math.PI * 2,
                SwaySpeed = 0.12 + _rng.NextDouble() * 0.14,
                SwayAmp = 0.6 + _rng.NextDouble() * 1.4,
                W = 3 + _rng.NextDouble() * 3,
                H = 5 + _rng.NextDouble() * 5,
                Color = _rng.Next(Palette.Length),
            });
        }
    }

    private void Step()
    {
        double floor = Bounds.Height + 40;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Vy += Gravity;
            p.Vx *= AirDrag;
            p.SwayPhase += p.SwaySpeed;
            p.X += p.Vx + Math.Sin(p.SwayPhase) * p.SwayAmp;
            p.Y += p.Vy;
            p.Rot += p.RotSpeed;
            if (p.Y > floor) _particles.RemoveAt(i);
            else _particles[i] = p;
        }

        if (++_ticks >= MaxTicks || _particles.Count == 0)
        {
            _timer?.Stop();
            _particles.Clear();
            InvalidateVisual();
            Finished?.Invoke();
            return;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        foreach (var p in _particles)
        {
            var m = Matrix.CreateRotation(p.Rot) * Matrix.CreateTranslation(p.X, p.Y);
            using (ctx.PushTransform(m))
                ctx.FillRectangle(Palette[p.Color], new Rect(-p.W, -p.H, p.W * 2, p.H * 2));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
