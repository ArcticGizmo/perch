using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Perch.Avalonia.Theming;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

/// <summary>
/// A single attachment as a small chip — a dropped/pasted image (a thumbnail that opens on click and shows a
/// larger preview on hover) or a plain file (a mono name chip that opens on click). Used both in the
/// composer's pending-attachment tray (with a remove "×") and in a sent user message's bubble (read-only).
/// </summary>
internal sealed class AttachmentChip : Border
{
    private readonly SessionPalette _p;
    private readonly MessageAttachment _a;
    private Popup? _preview;

    public AttachmentChip(SessionPalette p, MessageAttachment a, bool removable = false, Action? onRemove = null)
    {
        _p = p;
        _a = a;
        Background = p.Raised2;
        BorderBrush = p.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = SessionPalette.ButtonRadius;
        Margin = new Thickness(0, 6, 6, 0);
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;

        var content = a.Kind == AttachmentKind.Image ? BuildImage() : BuildFile();
        Child = removable ? WithRemove(content, onRemove) : content;

        PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Open(); };
    }

    private Control BuildImage()
    {
        var bmp = TryLoad(320);   // the little inline thumbnail; the hover preview loads full-res separately
        Control thumb = bmp is not null
            ? new Image { Source = bmp, Height = 46, MaxWidth = 120, Stretch = Stretch.UniformToFill }
            : new TextBlock { Text = "🖼", FontSize = 20, Margin = new Thickness(10, 8) };
        var name = new TextBlock
        {
            Text = _a.DisplayName, FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Muted,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 160, Margin = new Thickness(9, 0, 11, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumb, name } };
        if (bmp is not null) WirePreview();
        ToolTip.SetTip(this, "Click to open");
        return row;
    }

    private Control BuildFile()
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(11, 7),
            Children =
            {
                new TextBlock { Text = "📄", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = _a.DisplayName, FontFamily = _p.Mono, FontSize = 12, Foreground = _p.Muted,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 220,
                },
            },
        };
        ToolTip.SetTip(this, _a.Path + "  ·  click to open");
        return chip;
    }

    // The chip with a small "×" remove button on its trailing edge (composer tray only).
    private Control WithRemove(Control content, Action? onRemove)
    {
        var close = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = _p.Raised,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "✕", FontSize = 10, Foreground = _p.Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.PointerEntered += (_, _) => close.Background = _p.Border;
        close.PointerExited += (_, _) => close.Background = _p.Raised;
        close.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;   // don't also fire the chip's open
            onRemove?.Invoke();
        };
        var dock = new DockPanel { LastChildFill = true };
        close[DockPanel.DockProperty] = Dock.Right;
        dock.Children.Add(close);
        dock.Children.Add(content);
        return dock;
    }

    // A hover preview: a larger, full-resolution copy of the image floating above the chip. Loaded lazily on
    // the first hover so a long thread doesn't decode every image up front — and at native resolution (not the
    // upscaled thumbnail), which is what kept the old preview looking soft.
    private Bitmap? _previewBmp;
    private Image? _previewImage;

    private void WirePreview()
    {
        _previewImage = new Image { MaxWidth = 560, MaxHeight = 560, Stretch = Stretch.Uniform };
        _preview = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Top, VerticalOffset = -6,
            IsLightDismissEnabled = false, IsHitTestVisible = false,
            Child = new Border
            {
                Background = _p.Surface, BorderBrush = _p.Border, BorderThickness = new Thickness(1),
                CornerRadius = SessionPalette.CardRadius, Padding = new Thickness(6),
                BoxShadow = BoxShadows.Parse("0 10 30 0 #66000000"),
                Child = _previewImage,
            },
        };
        LogicalChildren.Add(_preview);
        PointerEntered += (_, _) =>
        {
            if (_previewBmp is null && _previewImage is { } img)
            {
                _previewBmp = TryLoadFull();
                if (_previewBmp is not null) img.Source = _previewBmp;
            }
            if (_previewBmp is not null && _preview is { } pv) pv.IsOpen = true;
        };
        PointerExited += (_, _) => { if (_preview is { } pv) pv.IsOpen = false; };
    }

    private Bitmap? TryLoad(int decodeWidth)
    {
        try
        {
            using var fs = File.OpenRead(_a.Path);
            return Bitmap.DecodeToWidth(fs, decodeWidth);
        }
        catch { return null; }
    }

    // Full native resolution, for the hover preview and the in-app viewer (no decode-to-width recompression).
    private Bitmap? TryLoadFull()
    {
        try { return new Bitmap(_a.Path); }
        catch { return null; }
    }

    // Images open in the in-app viewer (which offers reveal / open-with); a plain file uses the OS handler.
    private void Open()
    {
        if (_a.Kind == AttachmentKind.Image) { Windows.ImageViewerWindow.ShowFor(_a.Path); return; }
        try { PlatformServices.UrlOpener.Open(_a.Path); } catch { }
    }
}
