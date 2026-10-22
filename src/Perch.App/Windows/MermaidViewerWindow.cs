using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Perch.Avalonia.Rendering;
using Perch.Avalonia.Views;
using Perch.Data.Mermaid;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The "Fullscreen" view of a Mermaid diagram from the Markdown preview: a large dialog over its owner where the
/// diagram can be zoomed and dragged around (<see cref="MermaidPanZoom"/>), with a floating zoom toolbar, keyboard
/// shortcuts, and F11 for true fullscreen. It paints with the preview's own <see cref="MarkdownStyle"/> on the
/// background the card sat on, so the diagram looks the same as inline, just bigger.
/// </summary>
internal sealed class MermaidViewerWindow : Window
{
    private readonly MermaidPanZoom _view;
    private readonly TextBlock _zoomLabel;
    private readonly MarkdownStyle _s;
    private readonly IBrush _hover;

    internal MermaidViewerWindow(MermaidDiagramView diagram, MarkdownStyle style, IBrush background, string title)
    {
        _s = style;
        _hover = style.Muted is ISolidColorBrush m ? new SolidColorBrush(m.Color, 0.18) : style.TableHeaderBg;
        Title = title;
        Background = background;
        Width = 1100;
        Height = 760;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _view = new MermaidPanZoom(diagram);
        _zoomLabel = new TextBlock
        {
            Foreground = style.Fg, FontSize = 12, MinWidth = 44, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _view.ZoomChanged += z => _zoomLabel.Text = $"{Math.Round(z * 100)}%";

        var toolbar = new Border
        {
            Background = style.CodeBg, BorderBrush = style.TableBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            Margin = new Thickness(0, 12, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = BoxShadows.Parse("0 4 14 0 #33000000"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 2,
                Children =
                {
                    ToolButton("−", "Zoom out (−)", _view.ZoomOut),
                    _zoomLabel,
                    ToolButton("+", "Zoom in (+)", _view.ZoomIn),
                    Divider(),
                    ToolButton("Fit", "Fit the whole diagram (0)", _view.Fit),
                    ToolButton("100%", "Actual size (1)", _view.ActualSize),
                    Divider(),
                    ToolButton("×", "Close (Esc)", Close),
                },
            },
        };

        // On a pill of the window's own background so it reads over the diagram, and gone once the user has the idea.
        var hint = new Border
        {
            Background = background, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 3),
            Margin = new Thickness(10, 0, 0, 10), Opacity = 0.92,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Scroll to zoom · drag to move · double-click to zoom in · 0 fits · F11 fullscreen · Esc closes",
                Foreground = style.Muted, FontSize = 11.5,
            },
        };
        _view.Interacted += () => hint.IsVisible = false;

        Content = new Grid { Children = { _view, toolbar, hint } };
        KeyDown += OnKeyDown;
        Opened += (_, _) => _view.Focus();
    }

    /// <summary>Opens the viewer for a diagram shown at <paramref name="from"/>: modal over that control's window,
    /// sized to most of its screen. A diagram that can't be laid out again does nothing.</summary>
    public static void Open(Control from, MermaidDiagram diagram, MarkdownStyle style, string title)
    {
        if (MermaidDiagramView.TryCreate(diagram, style) is not { } view) return;
        var w = new MermaidViewerWindow(view, style, BackgroundOf(from, style), title);
        var owner = TopLevel.GetTopLevel(from) as Window;
        if ((owner?.Screens.ScreenFromVisual(from) ?? owner?.Screens.Primary) is { } screen)
        {
            var area = screen.WorkingArea.ToRect(screen.Scaling);
            w.Width = Math.Max(w.MinWidth, area.Width * 0.85);
            w.Height = Math.Max(w.MinHeight, area.Height * 0.85);
        }
        if (owner is not null) _ = w.ShowDialog(owner);
        else w.Show();
    }

    // The colour the card was painted on: the nearest ancestor with an opaque background. The diagram's colours were
    // chosen against that surface (node fills, label pills), so the viewer must sit on the same one.
    private static IBrush BackgroundOf(Visual from, MarkdownStyle style)
    {
        foreach (var v in from.GetSelfAndVisualAncestors())
        {
            var bg = v switch
            {
                Border b => b.Background,
                Panel p => p.Background,
                global::Avalonia.Controls.Primitives.TemplatedControl t => t.Background,
                _ => null,
            };
            if (bg is ISolidColorBrush { Color.A: > 200 } solid) return solid;
        }
        // Nothing opaque found: pick a paper that suits the text colour.
        return style.Fg is ISolidColorBrush fg && Luminance(fg.Color) > 0.5
            ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24))
            : Brushes.White;
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        const double step = 60;
        switch (e.Key)
        {
            case Key.Escape when WindowState == WindowState.FullScreen: WindowState = WindowState.Normal; break;
            case Key.Escape: Close(); break;
            case Key.OemPlus or Key.Add: _view.ZoomIn(); break;
            case Key.OemMinus or Key.Subtract: _view.ZoomOut(); break;
            case Key.D0 or Key.NumPad0: _view.Fit(); break;
            case Key.D1 or Key.NumPad1: _view.ActualSize(); break;
            case Key.Left: _view.PanBy(step, 0); break;
            case Key.Right: _view.PanBy(-step, 0); break;
            case Key.Up: _view.PanBy(0, step); break;
            case Key.Down: _view.PanBy(0, -step); break;
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
                break;
            default: return;
        }
        e.Handled = true;
    }

    private Border ToolButton(string text, string tip, Action onClick)
    {
        var label = new TextBlock { Text = text, Foreground = _s.Fg, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 4),
            MinWidth = 28, Cursor = new Cursor(StandardCursorType.Hand), Child = label,
        };
        label.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(b, tip);
        b.PointerEntered += (_, _) => b.Background = _hover;
        b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
        b.PointerPressed += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    private Border Divider() => new()
    {
        Width = 1, Margin = new Thickness(4, 5), Background = _s.TableBorder,
    };

    /// <summary>The render harness's seam: zoom about a point once the window is laid out.</summary>
    internal void ZoomAtForRender(Point at, double factor) => _view.ZoomAtForRender(at, factor);
}
