using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Views;

/// <summary>
/// The zoom readout for a <see cref="ZoomHost"/>: a drawn magnifying glass and the current level ("110%"), styled as
/// a quiet <see cref="SessionButton"/>. A click opens the levels to pick from; it follows chord and wheel steps too.
/// </summary>
internal sealed class ZoomButton : Border
{
    private readonly ZoomHost _host;
    private readonly TextBlock _label;

    public ZoomButton(SessionPalette p, ZoomHost host)
    {
        _host = host;
        Background = Brushes.Transparent;
        BorderBrush = p.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = SessionPalette.ButtonRadius;
        Padding = new Thickness(9, 6, 10, 6);
        Cursor = new Cursor(StandardCursorType.Hand);
        VerticalAlignment = VerticalAlignment.Center;
        this[ToolTip.TipProperty] = host.ResetOnCtrl0
            ? "Zoom — Ctrl+= / Ctrl+− (or Ctrl+wheel), Ctrl+0 to reset"
            : "Zoom — Ctrl+= / Ctrl+− (or Ctrl+wheel)";

        _label = new TextBlock
        {
            FontSize = 13, FontWeight = FontWeight.SemiBold, FontFamily = p.Body, Foreground = p.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { Magnifier(p.Muted), _label },
        };

        this.HoverWash(p.Raised2).OnLeftClick(ShowMenu);
        host.ZoomApplied += Refresh;
        Refresh();
    }

    private void Refresh() => _label.Text = ViewZoom.Label(_host.Zoom);

    private void ShowMenu()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var step in ViewZoom.Steps.Reverse())   // biggest at the top
        {
            bool current = Math.Abs(step - _host.Zoom) < 0.001;
            var item = new MenuItem
            {
                Header = ViewZoom.Label(step),
                Icon = current ? new TextBlock { Text = "✓", HorizontalAlignment = HorizontalAlignment.Center } : null,
                FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
            };
            var level = step;
            item.Click += (_, _) => _host.ZoomTo(level);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset to 100%", IsEnabled = Math.Abs(_host.Zoom - ViewZoom.Default) >= 0.001 };
        if (_host.ResetOnCtrl0) reset.InputGesture = new KeyGesture(Key.D0, KeyModifiers.Control);
        reset.Click += (_, _) => _host.ZoomTo(ViewZoom.Default);
        flyout.Items.Add(reset);
        flyout.ShowAt(this);
    }

    // A drawn glass (ring + handle) rather than a font glyph, which can come out as a colour emoji or not at all.
    private static Control Magnifier(IBrush stroke) => new Canvas
    {
        Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new Ellipse { Width = 9, Height = 9, Stroke = stroke, StrokeThickness = 1.6 },
            new Line
            {
                StartPoint = new Point(7.6, 7.6), EndPoint = new Point(12, 12), Stroke = stroke,
                StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round,
            },
        },
    };
}
