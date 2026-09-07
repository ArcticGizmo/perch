using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Perch.Avalonia.Theming;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small in-app image viewer for the images attached to a session (a pasted/dropped image, or one recovered
/// from a resumed transcript). Shows the picture at full resolution and offers the two "take it elsewhere"
/// actions — reveal in the OS file manager, and the OS "Open with…" chooser — so a click on a thumbnail or an
/// inline <c>[Image #N]</c> reference lands here rather than firing the default handler straight away. A single
/// instance is reused and retargeted via <see cref="ShowFor"/>. Colours mirror the overlay palette.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private static readonly IBrush Bg     = Palette.OverlaySurfaceBrush;
    private static readonly IBrush Stroke = Palette.BorderBrush;
    private static readonly IBrush Fg     = Palette.FgBrush;
    private static readonly IBrush Muted  = Palette.MutedBrush;
    private static readonly IBrush BtnBg  = Palette.ButtonBgBrush;

    private static ImageViewerWindow? _instance;

    private readonly Image _image;
    private readonly TextBlock _caption;
    private string _path = "";

    private ImageViewerWindow()
    {
        Title = "Image";
        Width = 760;
        Height = 620;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Uniform + DownOnly: a big image scales down to fit the window (never overflowing), a small one shows
        // at its native size — so the whole picture always stays in frame.
        _image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var canvas = new Border { Padding = new Thickness(16), Child = _image };

        _caption = new TextBlock
        {
            Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.PrefixCharacterEllipsis, MaxWidth = 380,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                ToolbarButton("Reveal in Explorer", () => PlatformServices.FileRevealer.RevealInFileManager(_path)),
                ToolbarButton("Open with…", () => PlatformServices.FileRevealer.OpenWith(_path)),
                ToolbarButton("Open", () => { try { PlatformServices.UrlOpener.Open(_path); } catch { } }),
            },
        };
        var bar = new DockPanel
        {
            Margin = new Thickness(14, 10, 14, 12),
            Children = { actions, _caption },
        };
        actions[DockPanel.DockProperty] = Dock.Right;

        var footer = new Border
        {
            Background = Bg, BorderBrush = Stroke, BorderThickness = new Thickness(0, 1, 0, 0),
            [DockPanel.DockProperty] = Dock.Bottom, Child = bar,
        };

        Content = new DockPanel { Children = { footer, canvas } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { if (ReferenceEquals(_instance, this)) _instance = null; };
    }

    /// <summary>Opens (or reuses) the viewer on <paramref name="path"/>. Best-effort — a file that can't be
    /// decoded just shows nothing.</summary>
    public static void ShowFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _instance ??= new ImageViewerWindow();
        _instance.Load(path);
        _instance.Show();
        _instance.Activate();
    }

    private void Load(string path)
    {
        _path = path;
        Title = System.IO.Path.GetFileName(path);
        _caption.Text = path;
        try { _image.Source = new Bitmap(path); }   // full resolution — the viewer is where crispness matters
        catch { _image.Source = null; }
    }

    private Button ToolbarButton(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label, Foreground = Fg, Background = BtnBg, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(7), Cursor = new Cursor(StandardCursorType.Hand),
            FontSize = 12.5,
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
