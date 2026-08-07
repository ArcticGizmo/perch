using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small speech bubble that floats off the dense strip's perch-logo row to announce a session status
/// change (finished / awaiting input / API error) when the strip is in "bubble" mode
/// (<c>AppSettings.DenseStatusChangeStyle = Bubble</c>). It holds briefly then fades itself out, leaving the
/// strip collapsed — the quieter alternative to popping the whole hover panel open. Transparent, topmost and
/// no-activate so it never steals focus, but (unlike <see cref="DenseDropZoneWindow"/>) it is hit-testable:
/// hovering or clicking it dismisses it early. The owning <see cref="DenseController"/> positions it against
/// the strip; the window owns its own hold-then-fade lifetime and nulls the controller's reference on close.
/// </summary>
internal sealed class DenseBubbleWindow : Window
{
    private const double HoldMs = 2600;   // full opacity …
    private const double FadeMs = 1600;   // … then a linear fade to nothing (≈ four seconds on screen)

    private readonly Action _onClosed;
    private readonly BubbleVisual _visual = new();
    private readonly DispatcherTimer _timer;
    private DateTime _start;

    public DenseBubbleWindow(Action onClosed)
    {
        _onClosed = onClosed;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = _visual;
        // The window is transparent/layered, so ClearType would fringe the text — force grayscale AA.
        TextOptions.SetTextRenderingMode(_visual, TextRenderingMode.Antialias);

        // Hovering or clicking the bubble dismisses it early (it's a passing notice, not something to read).
        _visual.PointerEntered += (_, _) => Close();
        _visual.PointerPressed += (_, _) => Close();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.Now - _start).TotalMilliseconds;
            if (elapsed >= HoldMs + FadeMs) { Close(); return; }
            Opacity = elapsed <= HoldMs ? 1 : Math.Clamp(1 - (elapsed - HoldMs) / FadeMs, 0, 1);
        };
    }

    /// <summary>Lays out the bubble for a status change and sizes the window (DIP) to fit — call before
    /// positioning. <paramref name="side"/> is the strip's docked edge, so the tail points back at it.</summary>
    public void Configure(DenseSide side, Color dot, string label)
    {
        var (w, h) = _visual.Layout(side, dot, label);
        Width = w;
        Height = h;
    }

    /// <summary>Show (if hidden) and restart the hold-then-fade from full opacity.</summary>
    public void Present()
    {
        if (!IsVisible) Show();
        Opacity = 1;
        _start = DateTime.Now;
        _timer.Stop();
        _timer.Start();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // No-activate (never steals focus) but NOT click-through, so it still receives the hover/press that
        // dismisses it.
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(h.Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        _onClosed();
    }

    /// <summary>Builds a standalone, sized bubble control for the headless render harness (the live bubble is
    /// a separate top-level window, so this lets it be eyeballed like the other owner-drawn surfaces).</summary>
    internal static Control CreateForRender(DenseSide side, Color dot, string label)
    {
        var v = new BubbleVisual();
        var (w, h) = v.Layout(side, dot, label);
        v.Width = w;
        v.Height = h;
        return v;
    }

    // Owner-drawn bubble: a rounded body (matching the overlay panel) with a status dot + short label, and a
    // little tail on the strip-facing edge. All DIPs; the window client size is (body + tail) wide.
    private sealed class BubbleVisual : Control
    {
        private const double FontSize = 12;
        private const double PadX = 10, PadY = 6;
        private const double DotD = 8, Gap = 7;
        private const double TailW = 7, TailH = 6;   // tail length + half base-height
        private const double Corner = 8;

        private DenseSide _side;
        private Color _dot;
        private FormattedText? _ft;
        private double _bodyW, _bodyH;

        // Sizes the bubble from its label and returns the total (body + tail) DIP size for the window.
        public (double w, double h) Layout(DenseSide side, Color dot, string label)
        {
            _side = side;
            _dot = dot;
            _ft = OverlayDraw.Text(label, FontSize, new SolidColorBrush(Palette.Fg), FontWeight.SemiBold);
            _bodyW = PadX * 2 + DotD + Gap + _ft.Width;
            _bodyH = PadY * 2 + Math.Max(DotD, _ft.Height);
            InvalidateVisual();
            return (_bodyW + TailW, _bodyH);
        }

        public override void Render(DrawingContext ctx)
        {
            if (_ft is null) return;

            double w = Bounds.Width, h = Bounds.Height, midY = h / 2;
            bool tailRight = _side == DenseSide.Right;
            double bodyLeft = tailRight ? 0 : TailW;

            var fill = new SolidColorBrush(Palette.Active.OverlaySurface.ToColor(240));
            var border = new Pen(new SolidColorBrush(Palette.Border), 1);

            OverlayDraw.Panel(ctx, new Rect(bodyLeft, 0, _bodyW, h), fill, border, Corner);

            // Tail, drawn over the body edge (overlapping 1px) so its base covers the body's border seam.
            double baseX = tailRight ? bodyLeft + _bodyW - 1 : bodyLeft + 1;
            double apexX = tailRight ? w : 0;
            var tail = new StreamGeometry();
            using (var gc = tail.Open())
            {
                gc.BeginFigure(new Point(baseX, midY - TailH), isFilled: true);
                gc.LineTo(new Point(apexX, midY));
                gc.LineTo(new Point(baseX, midY + TailH));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(fill, null, tail);

            double dotCx = bodyLeft + PadX + DotD / 2;
            ctx.DrawEllipse(new SolidColorBrush(_dot), null, new Point(dotCx, midY), DotD / 2, DotD / 2);
            OverlayDraw.TextLeftMid(ctx, _ft, bodyLeft + PadX + DotD + Gap, midY);
        }
    }
}
