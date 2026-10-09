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
        // The little inline thumbnail, decoded off the UI thread: the chip shows the placeholder glyph until it
        // lands (and keeps it if the image can't be decoded). The hover preview loads separately, on first hover.
        var thumbHost = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "🖼", FontSize = 20, Margin = new Thickness(10, 8) },
        };
        var name = new TextBlock
        {
            Text = _a.ChipLabel, FontFamily = _p.Mono, FontSize = 11.5, Foreground = _a.Marker is null ? _p.Muted : _p.Code,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200, Margin = new Thickness(9, 0, 11, 0),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumbHost, name } };
        ToolTip.SetTip(this, _a.Pasted ? "Pasted image  ·  click to open" : _a.DisplayName + "  ·  click to open");
        LoadThumbnail(thumbHost);
        return row;
    }

    private async void LoadThumbnail(Border host)
    {
        var bmp = await BoundedBitmap.LoadAsync(_a.Path, 320);
        if (bmp is null) return;
        host.Child = new Image { Source = bmp, Height = 46, MaxWidth = 120, Stretch = Stretch.UniformToFill };
        WirePreview();
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

    // A hover preview: a larger copy of the image floating above the chip. Loaded lazily (off the UI thread) on
    // the first hover so a long thread doesn't decode every image up front — at up to twice its 560 DIP display
    // size (BoundedBitmap.PreviewWidth), not the upscaled thumbnail, which is what kept the old preview soft.
    private Bitmap? _previewBmp;
    private Image? _previewImage;
    private bool _previewLoading, _hovered;

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
        PointerEntered += async (_, _) =>
        {
            _hovered = true;
            if (_previewBmp is null && !_previewLoading && _previewImage is { } img)
            {
                _previewLoading = true;
                _previewBmp = await BoundedBitmap.LoadAsync(_a.Path, BoundedBitmap.PreviewWidth);
                _previewLoading = false;
                if (_previewBmp is not null) img.Source = _previewBmp;
            }
            if (_hovered && _previewBmp is not null && _preview is { } pv) pv.IsOpen = true;   // still over the chip
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            if (_preview is { } pv) pv.IsOpen = false;
        };
    }

    // Images open in the in-app viewer (which offers reveal / open-with); a plain file uses the OS handler when
    // it's a view-only type, else it's revealed in the file manager.
    private void Open()
    {
        if (_a.Kind == AttachmentKind.Image) { Windows.ImageViewerWindow.ShowFor(_a.Path); return; }
        PlatformServices.FileRevealer.OpenWithDefault(_a.Path);
    }
}
