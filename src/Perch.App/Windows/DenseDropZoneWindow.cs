using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A translucent column shown on the left or right edge of a monitor while the dense strip is being
/// dragged, marking where it can be pinned — the Avalonia port of <c>DenseDropZoneForm</c>. Purely
/// visual: it never takes focus or input (click-through, no-activate), so the overlay detects "pointer is
/// over this zone" via screen coordinates rather than pointer events.
/// </summary>
internal sealed class DenseDropZoneWindow : Window
{
    private const double ColumnWidth = 56; // DIP

    private static readonly Color IdleFill   = Color.FromRgb(56,  189, 248); // calm accent blue
    private static readonly Color ActiveFill = Color.FromRgb(34,  197, 94);  // running green

    private readonly DropZoneVisual _visual;

    public Screen TargetScreen { get; }
    public DenseSide Side { get; }

    public DenseDropZoneWindow(Screen screen, DenseSide side)
    {
        TargetScreen = screen;
        Side = side;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0.40;

        _visual = new DropZoneVisual(side, IdleFill);
        Content = _visual;

        var wa = screen.WorkingArea;   // physical
        double scale = screen.Scaling;
        int physW = (int)(ColumnWidth * scale);
        Position = new PixelPoint(side == DenseSide.Left ? wa.X : wa.X + wa.Width - physW, wa.Y);
        Width = ColumnWidth;
        Height = wa.Height / scale;
    }

    /// <summary>Physical-pixel bounds of the lane, for the overlay's "cursor is over this zone" test.</summary>
    public bool ContainsScreenPoint(PixelPoint screenPt)
    {
        var wa = TargetScreen.WorkingArea;
        double scale = TargetScreen.Scaling;
        int physW = (int)(ColumnWidth * scale);
        int x = Side == DenseSide.Left ? wa.X : wa.X + wa.Width - physW;
        return screenPt.X >= x && screenPt.X < x + physW && screenPt.Y >= wa.Y && screenPt.Y < wa.Y + wa.Height;
    }

    public void SetActive(bool active)
    {
        Opacity = active ? 0.65 : 0.40;
        _visual.SetActive(active, active ? ActiveFill : IdleFill);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (TryGetPlatformHandle() is { } h)
            PlatformServices.WindowChrome.MakeClickThroughNoActivate(h.Handle);
    }

    // Owner-drawn lane: a dashed inner border plus a centred pin glyph (arrow into a pipe) hinting the
    // strip docks to this edge — "→|" for the right edge, mirrored "|←" for the left.
    private sealed class DropZoneVisual : Control
    {
        private readonly DenseSide _side;
        private Color _accent;

        public DropZoneVisual(DenseSide side, Color accent)
        {
            _side = side;
            _accent = accent;
        }

        public void SetActive(bool _, Color accent)
        {
            _accent = accent;
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(new SolidColorBrush(_accent), new Rect(0, 0, w, h));

            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(220, _accent.R, _accent.G, _accent.B)), 2)
            { DashStyle = DashStyle.Dash };
            ctx.DrawRectangle(null, borderPen, new Rect(2, 2, Math.Max(0, w - 4), Math.Max(0, h - 4)));

            double cx = w / 2, cy = h / 2;
            var glyph = new Pen(Brushes.White, 2.4, lineCap: PenLineCap.Round);
            if (_side == DenseSide.Right)
            {
                double pipeX = cx + 9, shaftEnd = pipeX - 3;
                ctx.DrawLine(glyph, new Point(cx - 11, cy), new Point(shaftEnd, cy));            // shaft
                ctx.DrawLine(glyph, new Point(shaftEnd - 6, cy - 6), new Point(shaftEnd, cy));   // arrowhead
                ctx.DrawLine(glyph, new Point(shaftEnd - 6, cy + 6), new Point(shaftEnd, cy));
                ctx.DrawLine(glyph, new Point(pipeX, cy - 10), new Point(pipeX, cy + 10));        // pipe
            }
            else
            {
                double pipeX = cx - 9, shaftEnd = pipeX + 3;
                ctx.DrawLine(glyph, new Point(cx + 11, cy), new Point(shaftEnd, cy));
                ctx.DrawLine(glyph, new Point(shaftEnd + 6, cy - 6), new Point(shaftEnd, cy));
                ctx.DrawLine(glyph, new Point(shaftEnd + 6, cy + 6), new Point(shaftEnd, cy));
                ctx.DrawLine(glyph, new Point(pipeX, cy - 10), new Point(pipeX, cy + 10));
            }
        }
    }
}
