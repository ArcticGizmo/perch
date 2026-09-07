using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Perch.Avalonia.Views;

/// <summary>
/// A small indeterminate loading spinner: a rounded 270° arc that rotates. Owner-drawn (the arc geometry is
/// rebuilt each frame at the current angle), and — like <see cref="TypingDots"/> — the animation timer runs
/// only while the control is attached to the visual tree, so a hidden/removed spinner costs nothing. Colour
/// via <see cref="Stroke"/>, ring weight via <see cref="Thickness"/>.
/// </summary>
internal sealed class LoadingSpinner : Control
{
    private const double PeriodMs = 850;             // one full revolution
    private const double SweepDeg = 270;             // arc length; the gap is what reads as "spinning"
    private const int Segments = 32;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    public IBrush Stroke { get; set; } = Brushes.Gray;
    public double Thickness { get; set; } = 2.4;

    public LoadingSpinner()
    {
        Width = Height = 18;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Restart();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _clock.Stop();
    }

    public override void Render(DrawingContext ctx)
    {
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
        double radius = Math.Min(cx, cy) - Thickness;
        if (radius <= 0) return;

        double start = _clock.Elapsed.TotalMilliseconds / PeriodMs * (Math.PI * 2);
        double sweep = SweepDeg * Math.PI / 180;
        var pen = new Pen(Stroke, Thickness) { LineCap = PenLineCap.Round };

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(Point(cx, cy, radius, start), isFilled: false);
            for (int i = 1; i <= Segments; i++)
                g.LineTo(Point(cx, cy, radius, start + sweep * i / Segments));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static Point Point(double cx, double cy, double r, double angle) =>
        new(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
}
