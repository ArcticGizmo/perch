using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Perch.Avalonia.Views;

/// <summary>
/// Three dots that bounce in a staggered wave — the session UI's "Claude is working" indicator, the visual
/// twin of a chat app's typing dots. Owner-drawn (a raised-sine hop per dot, each a beat behind the last);
/// the animation timer runs <em>only</em> while the control is attached to the visual tree, so a hidden or
/// removed indicator costs nothing. Colour is set via <see cref="Fill"/> (the session palette's accent).
/// </summary>
internal sealed class TypingDots : Control
{
    private const int DotCount = 3;
    private const double DotRadius = 3.2;
    private const double Gap = 6.5;
    private const double BounceHeight = 4.5;
    private const double Stagger = 0.16;                 // each dot trails the previous by this fraction of a cycle
    private const double PeriodMs = 1050;               // one full bounce
    private static readonly double TwoPi = Math.PI * 2;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    /// <summary>The dot colour.</summary>
    public IBrush Fill { get; set; } = Brushes.Gray;

    public TypingDots()
    {
        Width = DotCount * (DotRadius * 2) + (DotCount - 1) * Gap;
        Height = DotRadius * 2 + BounceHeight;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };   // ~25fps is plenty for three dots
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
        double t = _clock.Elapsed.TotalMilliseconds / PeriodMs;   // cycles elapsed (continuous)
        double baseY = Height - DotRadius;                        // dots rest along the bottom, hop upward
        for (int i = 0; i < DotCount; i++)
        {
            // max(0, sin) → a hop up from the baseline with a rest between beats, the classic bouncing-dots look.
            double lift = Math.Max(0, Math.Sin((t - i * Stagger) * TwoPi));
            double cx = DotRadius + i * (DotRadius * 2 + Gap);
            double cy = baseY - lift * BounceHeight;
            ctx.DrawEllipse(Fill, null, new Point(cx, cy), DotRadius, DotRadius);
        }
    }
}
