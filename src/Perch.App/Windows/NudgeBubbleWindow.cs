using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small "your turn" speech bubble that floats off the side of an online Connect 4 board (or the overlay, if
/// the board isn't open) when your opponent nudges you. Modelled on <see cref="DenseBubbleWindow"/>: transparent,
/// topmost, no-activate (never steals focus) but hit-testable so hovering/clicking dismisses it early; it holds
/// briefly then fades itself out. The App positions it against the anchor and nulls its reference on close.
/// </summary>
internal sealed class NudgeBubbleWindow : Window
{
    private const double HoldMs = 3200;   // full opacity …
    private const double FadeMs = 1600;   // … then a linear fade (≈ five seconds on screen — a nudge is worth reading)

    private readonly Action _onClosed;
    private readonly BubbleVisual _visual = new();
    private readonly DispatcherTimer _timer;
    private DateTime _start;

    public NudgeBubbleWindow(Action onClosed)
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
        TextOptions.SetTextRenderingMode(_visual, TextRenderingMode.Antialias);

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

    /// <summary>Lays out the bubble and sizes the window (DIP). <paramref name="tailRight"/> points the tail back
    /// toward the anchor on its right (so the bubble sits to the anchor's left) — otherwise the tail points left.</summary>
    public void Configure(bool tailRight, string label)
    {
        var (w, h) = _visual.Layout(tailRight, Palette.Active.StatusAttention.ToColor(), label);
        Width = w;
        Height = h;
    }

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
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeToolWindowNoActivate(h.Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        _onClosed();
    }

    internal static Control CreateForRender(bool tailRight, string label)
    {
        var v = new BubbleVisual();
        var (w, h) = v.Layout(tailRight, Palette.Active.StatusAttention.ToColor(), label);
        v.Width = w;
        v.Height = h;
        return v;
    }

    // Owner-drawn bubble: a rounded body (matching the overlay panel) with a bell + short label, and a little
    // tail on the anchor-facing edge. Mirrors DenseBubbleWindow.BubbleVisual; all DIPs.
    private sealed class BubbleVisual : Control
    {
        private const double FontSize = 12.5;
        private const double PadX = 11, PadY = 7;
        private const double DotD = 8, Gap = 8;
        private const double TailW = 7, TailH = 6;
        private const double Corner = 8;

        private bool _tailRight;
        private Color _dot;
        private FormattedText? _ft;
        private double _bodyW, _bodyH;

        public (double w, double h) Layout(bool tailRight, Color dot, string label)
        {
            _tailRight = tailRight;
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
            double bodyLeft = _tailRight ? 0 : TailW;

            var fill = new SolidColorBrush(Palette.Active.OverlaySurface.ToColor(240));
            var border = new Pen(new SolidColorBrush(Palette.Border), 1);

            OverlayDraw.Panel(ctx, new Rect(bodyLeft, 0, _bodyW, h), fill, border, Corner);

            double baseX = _tailRight ? bodyLeft + _bodyW - 1 : bodyLeft + 1;
            double apexX = _tailRight ? w : 0;
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
