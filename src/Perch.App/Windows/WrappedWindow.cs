using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// A borderless reveal card that shows a rendered "Perch Wrapped" poster scaled to fit, with buttons to
/// copy it to the clipboard or save it as a PNG (the Avalonia port of the WinForms <c>WrappedForm</c>).
/// Centred on screen, draggable by the card, dismissed with the ✕ glyph, the Close button, or Esc. The
/// full-resolution poster is baked to a bitmap once, up front, and reused for both preview and export.
/// </summary>
internal sealed class WrappedWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.FromRgb(15, 15, 22));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 90));
    private static readonly IBrush Fg = new SolidColorBrush(Color.FromRgb(225, 225, 235));
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(150, 150, 170));
    private static readonly IBrush BtnBg = new SolidColorBrush(Color.FromRgb(30, 30, 44));

    private readonly RenderTargetBitmap _poster;
    private readonly string _suggestedName;
    private Button? _copyButton;
    private DispatcherTimer? _copiedTimer;

    public WrappedWindow(WrappedSummary summary, IImage? icon, string suggestedName)
    {
        _suggestedName = suggestedName;
        _poster = new WrappedPoster(summary, icon).RenderBitmap();

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Content = BuildCard();
    }

    private Control BuildCard()
    {
        // Scale the poster to a comfortable preview height for this screen, preserving its 2:3 aspect.
        double previewH = PreviewHeight();
        double previewW = previewH * WrappedPoster.PosterWidth / WrappedPoster.PosterHeight;

        var image = new Image
        {
            Source = _poster, Width = previewW, Height = previewH,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);

        var posterCard = new Border
        {
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Child = image, HorizontalAlignment = HorizontalAlignment.Center,
        };

        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Muted, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(6, 2), FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeGlyph.Click += (_, _) => Close();

        _copyButton = MakeButton("Copy image", primary: true);
        _copyButton.Click += (_, _) => CopyImage();
        var saveButton = MakeButton("Save PNG…");
        saveButton.Click += async (_, _) => await SaveImageAsync();
        var closeButton = MakeButton("Close");
        closeButton.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _copyButton, saveButton, closeButton },
        };

        var stack = new StackPanel
        {
            Spacing = 16, Margin = new Thickness(22),
            Children = { posterCard, buttons },
        };

        var card = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(16),
            BorderBrush = Stroke, BorderThickness = new Thickness(1.5),
            Child = new Grid { Children = { stack, closeGlyph } },
        };

        // Drag the card to move the borderless window (but not when starting a drag on a button).
        card.PointerPressed += (_, e) =>
        {
            if (e.Source is Control c && c.FindAncestorOfType<Button>() is not null) return;
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        return card;
    }

    // A preview height that fits the current screen's working area with room for the buttons + margins,
    // clamped to a sensible band. Falls back to a laptop-friendly default if the screen isn't known yet.
    private double PreviewHeight()
    {
        double workH = 1040;
        try
        {
            var screen = Screens?.Primary;
            if (screen is not null) workH = screen.WorkingArea.Height / screen.Scaling;
        }
        catch { /* screen not resolvable before show — use the fallback */ }
        return Math.Clamp(workH - 220, 560, WrappedPoster.PosterHeight);
    }

    private Button MakeButton(string text, bool primary = false) => new()
    {
        Content = text, Height = 38, MinWidth = 128, Padding = new Thickness(14, 0),
        Background = primary ? new SolidColorBrush(Theming.Palette.Accent) : BtnBg,
        Foreground = primary ? Brushes.White : Fg,
        BorderBrush = Stroke, BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(8), FontSize = 12.5,
        Cursor = new Cursor(StandardCursorType.Hand),
    };

    private void CopyImage()
    {
        if (_copyButton is null) return;
        bool ok = TryCopyPosterToClipboard();
        // Brief inline confirmation; leaves the label unchanged if the platform copy failed (e.g. macOS
        // stub), so we don't claim success we didn't achieve.
        _copyButton.Content = ok ? "Copied!" : "Copy unavailable";
        _copiedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        _copiedTimer.Stop();
        _copiedTimer.Tick -= OnCopiedTick;
        _copiedTimer.Tick += OnCopiedTick;
        _copiedTimer.Start();
    }

    private bool TryCopyPosterToClipboard()
    {
        try
        {
            int w = _poster.PixelSize.Width, h = _poster.PixelSize.Height;
            int stride = w * 4;
            var buffer = new byte[stride * h];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { _poster.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, stride); }
            finally { handle.Free(); }
            return PlatformServices.ImageClipboard.TryCopyBgra(buffer, w, h, stride);
        }
        catch { return false; }
    }

    private void OnCopiedTick(object? sender, EventArgs e)
    {
        _copiedTimer?.Stop();
        if (_copyButton is not null) _copyButton.Content = "Copy image";
    }

    private async Task SaveImageAsync()
    {
        var top = GetTopLevel(this);
        if (top?.StorageProvider is not { } storage) return;
        try
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save your Perch Wrapped",
                SuggestedFileName = _suggestedName + ".png",
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            _poster.Save(stream);
        }
        catch { /* cancelled / disk full / permission — nothing useful to do but stay open */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _copiedTimer?.Stop();
        _poster.Dispose();
        base.OnClosed(e);
    }
}
