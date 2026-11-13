using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Views;

/// <summary>
/// A small indeterminate loading spinner: a rounded 270° arc that rotates. Owner-drawn: the arc is built once
/// per size and rotated by a transform each frame. The animation timer runs only while the control is attached
/// <b>and</b> effectively visible — hosts usually hide a spinner by hiding an ancestor, which leaves it in the
/// tree, so attachment alone would keep a hidden spinner repainting at 25 Hz for the life of its window.
/// Colour via <see cref="Stroke"/>, ring weight via <see cref="Thickness"/>.
/// </summary>
internal sealed class LoadingSpinner : Control
{
    private const double PeriodMs = 850;             // one full revolution
    private const double SweepDeg = 270;             // arc length; the gap is what reads as "spinning"
    private const int Segments = 32;

    // Avalonia 12 doesn't expose effective-visibility changes publicly, so any IsVisible change anywhere queues
    // one coalesced re-check of the attached spinners (a handful per open session window).
    private static readonly HashSet<LoadingSpinner> Attached = [];
    private static bool _recheckQueued;

    static LoadingSpinner()
    {
        IsVisibleProperty.Changed.AddClassHandler<Visual>((_, _) =>
        {
            if (Attached.Count == 0 || _recheckQueued) return;
            _recheckQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _recheckQueued = false;
                foreach (var s in Attached.ToArray()) s.SyncTimer();
            }, DispatcherPriority.Background);
        });
    }

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private bool _attached;

    private StreamGeometry? _arc;
    private (Size Size, double Thickness) _arcKey;

    internal bool IsSpinning => _timer.IsEnabled;

    public IBrush Stroke { get; set; } = Brushes.Gray;
    public double Thickness { get; set; } = 2.4;

    public LoadingSpinner()
    {
        Width = Height = 18;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) =>
        {
            if (IsEffectivelyVisible) InvalidateVisual();
            else SyncTimer();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Attached.Add(this);
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Attached.Remove(this);
        SyncTimer();
    }

    private void SyncTimer()
    {
        bool run = _attached && IsEffectivelyVisible;
        if (run == _timer.IsEnabled) return;
        if (run)
        {
            _clock.Restart();
            _timer.Start();
            InvalidateVisual();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double cx = Bounds.Width / 2, cy = Bounds.Height / 2;
        double radius = Math.Min(cx, cy) - Thickness;
        if (radius <= 0) return;

        var key = (Bounds.Size, Thickness);
        if (_arc is null || _arcKey != key)
        {
            _arc = BuildArc(cx, cy, radius);
            _arcKey = key;
        }

        double angle = _clock.Elapsed.TotalMilliseconds / PeriodMs * 360;
        var rotate = Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(angle * Math.PI / 180)
                     * Matrix.CreateTranslation(cx, cy);
        using (ctx.PushTransform(rotate))
            ctx.DrawGeometry(null, OverlayDraw.Pen(Stroke, Thickness, lineCap: PenLineCap.Round), _arc);
    }

    private static StreamGeometry BuildArc(double cx, double cy, double radius)
    {
        double sweep = SweepDeg * Math.PI / 180;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(Point(cx, cy, radius, 0), isFilled: false);
            for (int i = 1; i <= Segments; i++)
                g.LineTo(Point(cx, cy, radius, sweep * i / Segments));
            g.EndFigure(false);
        }
        return geo;
    }

    private static Point Point(double cx, double cy, double r, double angle) =>
        new(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
}
