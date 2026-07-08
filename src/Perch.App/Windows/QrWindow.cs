using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QRCoder;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A small always-on-top card, centred on screen, showing the remote-control deep-link QR code for one
/// session (the Avalonia port of <c>QrCodeForm</c>). Encodes <c>https://claude.ai/code/{bridge}</c> —
/// scanning it from the Claude mobile app joins the session. The link is clickable (opens the browser)
/// and copyable. Dismissed via the ✕ glyph, the Close button, Esc, or by clicking away (deactivation).
/// </summary>
public sealed class QrWindow : Window
{
    // Palette mirrors the overlay so the popup reads as part of the same app.
    private static readonly IBrush Bg     = new SolidColorBrush(Color.FromRgb(15, 15, 20));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(45, 45, 60));
    private static readonly IBrush Fg     = new SolidColorBrush(Color.FromRgb(225, 225, 235));
    private static readonly IBrush Muted  = new SolidColorBrush(Color.FromRgb(110, 110, 130));
    private static readonly IBrush Remote = new SolidColorBrush(Color.FromRgb(96, 165, 250));
    private static readonly IBrush BtnBg  = new SolidColorBrush(Color.FromRgb(30, 30, 44));

    private const int QrSize = 240, Quiet = 14;

    private readonly string _url;
    private Button? _copyButton;
    private DispatcherTimer? _copiedTimer;

    public QrWindow(string title, string url)
    {
        _url = url;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Clicking elsewhere (the overlay, another window) dismisses the popup.
        Deactivated += (_, _) => Close();

        Content = BuildCard(title, url);
    }

    private Control BuildCard(string title, string url)
    {
        var titleText = new TextBlock
        {
            Text = title, Foreground = Fg, FontWeight = FontWeight.Bold, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        };

        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(4, 0), FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeGlyph.Click += (_, _) => Close();

        var header = new Grid { Children = { titleText, closeGlyph } };

        var qrImage = new Image
        {
            Source = RenderQr(url), Width = QrSize, Height = QrSize,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(qrImage, BitmapInterpolationMode.None);

        var card = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Quiet), Child = qrImage,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var link = new TextBlock
        {
            Text = url, Foreground = Remote, FontSize = 11,
            TextDecorations = TextDecorations.Underline,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        link.PointerPressed += (_, _) => OpenLink();

        _copyButton = MakeButton("Copy link");
        _copyButton.Click += (_, _) => CopyLink();
        var closeButton = MakeButton("Close");
        closeButton.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _copyButton, closeButton },
        };

        var stack = new StackPanel
        {
            Spacing = 14, Margin = new Thickness(22),
            Children = { header, card, link, buttons },
        };

        return new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(12),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = stack,
        };
    }

    private static Button MakeButton(string text) => new()
    {
        Content = text, Width = 96, Height = 32,
        Background = BtnBg, Foreground = Remote, BorderBrush = Stroke, BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(6), FontSize = 12,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    // Renders the QR module matrix to a crisp black-on-white bitmap at ~QrSize (integer module scale, so
    // nearest-neighbour display stays sharp). QRCoder's matrix already carries its 4-module quiet zone.
    private static Bitmap RenderQr(string url)
    {
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var matrix = data.ModuleMatrix;
        int modules = matrix.Count;
        int scale = Math.Max(2, QrSize / modules);
        int size = scale * modules;

        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.FillRectangle(Brushes.White, new Rect(0, 0, size, size));
            for (int y = 0; y < modules; y++)
                for (int x = 0; x < modules; x++)
                    if (matrix[y][x])
                        ctx.FillRectangle(Brushes.Black, new Rect(x * scale, y * scale, scale, scale));
        }
        return rtb;
    }

    private void OpenLink()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch { /* no browser / blocked — stay open */ }
    }

    private void CopyLink()
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _copyButton is null) return;
        clipboard.SetTextAsync(_url);

        _copyButton.Content = "Copied!";
        _copiedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _copiedTimer.Stop();
        _copiedTimer.Tick -= OnCopiedTick;
        _copiedTimer.Tick += OnCopiedTick;
        _copiedTimer.Start();
    }

    private void OnCopiedTick(object? sender, EventArgs e)
    {
        _copiedTimer?.Stop();
        if (_copyButton is not null) _copyButton.Content = "Copy link";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
