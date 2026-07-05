using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Perch.Avalonia.Rendering;

namespace Perch.Avalonia.Views;

/// <summary>
/// A minimal borderless, non-activating, top-most tooltip window — the Avalonia counterpart of the
/// WinForms <c>HintTooltipForm</c> / <c>UsageTooltipForm</c>. It owner-draws a dark rounded panel and a
/// list of styled text lines (a single line for the glyph hints; several, with a bold header and a muted
/// footer, for the usage breakdown), sizes itself to that content, and clamps onto the anchor's screen.
/// One instance is reused for every hint so hovering never churns windows.
/// </summary>
internal sealed class OverlayTooltip : Window
{
    public readonly record struct Line(string Text, Color Color, bool Bold);

    private static readonly Color BgColor     = Color.FromRgb(20, 20, 28);
    private static readonly Color BorderColor = Color.FromRgb(60, 60, 80);
    public static readonly Color FgColor      = Color.FromRgb(225, 225, 235);
    public static readonly Color MutedColor   = Color.FromRgb(150, 150, 170);

    private const double HorizPad = 9;
    private const double VertPad  = 6;
    private const double LineGap  = 3;
    private const double Corner   = 6;

    private readonly Body _body = new();

    public OverlayTooltip()
    {
        WindowDecorations     = WindowDecorations.None;
        Background            = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowActivated         = false;   // never steal focus from the terminal
        Topmost               = true;
        ShowInTaskbar         = false;
        CanResize             = false;
        SizeToContent         = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible      = false;   // a hint, not a target
        Content               = _body;
    }

    /// <summary>Shows a single line anchored with its top-left at <paramref name="anchor"/> (screen px).</summary>
    public void ShowText(string text, PixelPoint anchor)
        => ShowLines([new Line(text, FgColor, false)], anchor);

    /// <summary>Shows the given styled lines, clamped onto the anchor point's screen working area. With
    /// <paramref name="placeLeft"/>, the panel's right edge sits at the anchor (used for the usage panel
    /// so it opens to the left of the overlay rather than covering it).</summary>
    public void ShowLines(IReadOnlyList<Line> lines, PixelPoint anchor, bool placeLeft = false)
    {
        _body.Lines = lines;
        _body.InvalidateMeasure();
        _body.InvalidateVisual();
        _body.Measure(Size.Infinity);
        var size = _body.DesiredSize;

        double scale = (this.Screens?.ScreenFromPoint(anchor)?.Scaling) ?? 1.0;
        int w = (int)Math.Ceiling(size.Width * scale);
        int h = (int)Math.Ceiling(size.Height * scale);

        int x = placeLeft ? anchor.X - w - 6 : anchor.X;
        int y = anchor.Y;
        if (placeLeft && x < 0) x = anchor.X + 6; // no room on the left — fall back to the right

        var area = this.Screens?.ScreenFromPoint(anchor)?.WorkingArea;
        if (area is { } wa)
        {
            x = Math.Clamp(x, wa.X + 2, wa.X + wa.Width  - w - 2);
            y = Math.Clamp(y, wa.Y + 2, wa.Y + wa.Height - h - 2);
        }
        Position = new PixelPoint(x, y);
        Show();
    }

    public void HideTip()
    {
        if (IsVisible) Hide();
    }

    // The owner-drawn content: rounded panel + one DrawText per line, sized from the font line heights.
    private sealed class Body : Control
    {
        private IReadOnlyList<Line> _lines = [];
        public IReadOnlyList<Line> Lines { set => _lines = value; }

        private static FormattedText Ft(Line l) =>
            OverlayDraw.Text(l.Text, l.Bold ? 12 : 11.5, new SolidColorBrush(l.Color),
                l.Bold ? FontWeight.Bold : FontWeight.Normal);

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = 0, h = VertPad * 2;
            foreach (var l in _lines)
            {
                var ft = Ft(l);
                w = Math.Max(w, ft.Width);
                h += ft.Height + LineGap;
            }
            if (_lines.Count > 0) h -= LineGap;
            return new Size(w + HorizPad * 2, h);
        }

        public override void Render(DrawingContext ctx)
        {
            var r = new Rect(0.75, 0.75, Bounds.Width - 1.5, Bounds.Height - 1.5);
            OverlayDraw.Panel(ctx, r, new SolidColorBrush(BgColor),
                new Pen(new SolidColorBrush(BorderColor), 1.5), Corner);

            double y = VertPad;
            foreach (var l in _lines)
            {
                var ft = Ft(l);
                ctx.DrawText(ft, new Point(HorizPad, y));
                y += ft.Height + LineGap;
            }
        }
    }
}
