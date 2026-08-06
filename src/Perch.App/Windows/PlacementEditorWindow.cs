using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Perch.Avalonia.Theming;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// Everything the placement editor needs from the app: the screen to cover, the current saved placements
/// (null = "using the default"), the built-in defaults (for Reset), the real DIP size of each mock so the
/// preview matches what will actually appear, and the save callback. The two <see cref="OverlayPlacement"/>
/// values handed to <see cref="OnSave"/> are the new floating/dense placements (null = reset that mode to
/// its default).
/// </summary>
internal sealed record PlacementEditorContext(
    Screen TargetScreen,
    OverlayPlacement? Floating,
    OverlayPlacement? Dense,
    OverlayPlacement DefaultFloating,
    OverlayPlacement DefaultDense,
    (double W, double H) FloatSizeDip,
    (double W, double H) DenseSizeDip,
    Action<OverlayPlacement?, OverlayPlacement?> OnSave);

/// <summary>
/// The "Set initial placements…" editor: a full-screen, dimmed, always-on-top overlay on one monitor where
/// the user positions a real-size preview of each overlay presentation. A mode toggle swaps between the
/// floating panel and the dense strip so each is placed individually; Reset returns the current mode to its
/// default; Done persists both and applies them, Cancel discards. Placement is stored relative to the
/// nearest corner (see <see cref="PlacementMath"/>), so it survives a resolution change.
/// <para>Phase 2 skeleton: the preview shows at the current/default spot and the toolbar works;
/// drag-to-position, the live distance HUD and edge guides arrive in Phase 3.</para>
/// </summary>
internal sealed class PlacementEditorWindow : Window
{
    private enum EditMode { Floating, Dense }

    private readonly PlacementEditorContext _ctx;

    // Per-mode working values, null meaning "use the default". Seeded from the saved placements.
    private OverlayPlacement? _floating;
    private OverlayPlacement? _dense;
    private EditMode _mode = EditMode.Floating;

    private readonly Canvas _canvas = new();
    private readonly Border _mock;
    private readonly TextBlock _mockLabel = new()
    {
        Foreground = Palette.AccentBrush, FontWeight = FontWeight.Bold, FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private Button _floatingModeBtn = null!;
    private Button _denseModeBtn = null!;

    // The two dashed guides from the preview's anchored corner out to the nearest screen edges, and the
    // pill that reads back the live distances.
    private readonly Line _guideH = MakeGuide();
    private readonly Line _guideV = MakeGuide();
    private readonly Border _hud;
    private readonly TextBlock _hudText = new()
    {
        Foreground = Palette.FgBrush, FontSize = 12, FontWeight = FontWeight.Bold,
    };

    // Drag state: whether a drag is in progress and the pointer's grab offset within the preview (DIP).
    private bool _dragging;
    private Point _grab;

    public PlacementEditorWindow(PlacementEditorContext ctx)
    {
        _ctx = ctx;
        _floating = ctx.Floating?.Clone();
        _dense = ctx.Dense?.Clone();

        Title = "Set initial placements";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Cover the target monitor's full bounds (physical → DIP via its scaling), matching ConfettiWindow.
        var b = ctx.TargetScreen.Bounds;
        double scale = ctx.TargetScreen.Scaling;
        Position = b.Position;
        Width = b.Width / scale;
        Height = b.Height / scale;

        _mock = new Border
        {
            Background = Palette.OverlaySurfaceBrush,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = _mockLabel,
        };
        _mock.PointerPressed += OnMockPressed;
        _mock.PointerMoved += OnMockMoved;
        _mock.PointerReleased += OnMockReleased;

        _hud = new Border
        {
            Background = Palette.ButtonBgBrush, BorderBrush = Palette.BorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4), Child = _hudText, IsHitTestVisible = false,
        };

        // Guides sit under the preview; the preview and HUD over them.
        _canvas.Children.Add(_guideH);
        _canvas.Children.Add(_guideV);
        _canvas.Children.Add(_mock);
        _canvas.Children.Add(_hud);

        Content = BuildContent();
        RefreshMock();
    }

    private static Line MakeGuide() => new()
    {
        Stroke = Palette.AccentBrush, StrokeThickness = 1,
        StrokeDashArray = new AvaloniaList<double> { 4, 3 }, IsHitTestVisible = false,
    };

    private Control BuildContent()
    {
        // The dim veil that signals "you're placing the overlay, not using the app".
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };

        var instructions = new TextBlock
        {
            Text = "Drag the preview to where the overlay should first appear, then choose Done.\n" +
                   "Placement is measured from the nearest corner, so it sticks if your resolution changes.",
            Foreground = Palette.FgBrush, FontSize = 14, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
        };

        // Overlay / Dense is a one-or-the-other choice, so present it as a joined segmented control (the
        // selected half fills with the accent) rather than two independent buttons.
        _floatingModeBtn = MakeSegment("Overlay", EditMode.Floating);
        _denseModeBtn = MakeSegment("Dense", EditMode.Dense);
        var segmented = new Border
        {
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Palette.ButtonBgBrush,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 0,
                Children = { _floatingModeBtn, new Border { Width = 1, Background = Palette.BorderBrush }, _denseModeBtn },
            },
        };

        var resetBtn = MakeButton("Reset to defaults", ResetCurrent);
        var doneBtn = MakeButton("Done", Commit);
        var cancelBtn = MakeButton("Cancel", Close);

        var toolbar = new Border
        {
            Background = Palette.OverlaySurfaceBrush,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 48),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
                Children = { segmented, new Border { Width = 12 }, resetBtn, doneBtn, cancelBtn },
            },
        };

        var root = new Grid { Children = { dim, _canvas, instructions, toolbar } };
        // The window is transparent/layered, so ClearType's subpixel antialiasing fringes text with colour.
        // Force grayscale antialiasing for the whole surface (inherited by every TextBlock below).
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Antialias);
        return root;
    }

    // One half of the Overlay/Dense segmented control: a flat, borderless button; RefreshMock fills the
    // selected one with the accent (StyleSegment).
    private Button MakeSegment(string text, EditMode mode)
    {
        var btn = new Button
        {
            Content = text, Height = 32, MinWidth = 78, Padding = new Thickness(14, 0),
            Background = Brushes.Transparent, Foreground = Palette.FgBrush,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => SetMode(mode);
        return btn;
    }

    private static void StyleSegment(Button btn, bool selected)
    {
        btn.Background = selected ? Palette.AccentBrush : Brushes.Transparent;
        btn.Foreground = selected ? Palette.OnAccentBrush : Palette.FgBrush;
        btn.FontWeight = selected ? FontWeight.Bold : FontWeight.Normal;
    }

    private Button MakeButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text, Height = 32, MinWidth = 72, Padding = new Thickness(12, 0),
            Background = Palette.ButtonBgBrush, Foreground = Palette.AccentBrush,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6), FontSize = 12,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void SetMode(EditMode mode)
    {
        _mode = mode;
        RefreshMock();
    }

    private void ResetCurrent()
    {
        if (_mode == EditMode.Floating) _floating = null; else _dense = null;
        RefreshMock();
    }

    // The placement currently in effect for the active mode (working value, or the default when unset).
    private OverlayPlacement CurrentPlacement() => _mode == EditMode.Floating
        ? _floating ?? _ctx.DefaultFloating
        : _dense ?? _ctx.DefaultDense;

    private (double W, double H) CurrentSizeDip() =>
        _mode == EditMode.Floating ? _ctx.FloatSizeDip : _ctx.DenseSizeDip;

    // Positions and sizes the preview for the current mode, draws the guides to the anchored edges, updates
    // the distance HUD, and reflects the mode in the toolbar. Canvas coordinates are DIP relative to the
    // window's top-left, which maps to the screen's bounds origin.
    private void RefreshMock()
    {
        var screen = _ctx.TargetScreen;
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;
        double scale = screen.Scaling;
        var p = CurrentPlacement();

        var (dipW, dipH) = CurrentSizeDip();
        int physW = Math.Max(1, (int)(dipW * scale));
        int physH = Math.Max(1, (int)(dipH * scale));

        var (px, py) = PlacementMath.ToPosition(p, wa.X, wa.Y, wa.Width, wa.Height, scale, physW, physH);
        double leftDip = (px - bounds.X) / scale;
        double topDip = (py - bounds.Y) / scale;

        _mock.Width = dipW;
        _mock.Height = dipH;
        Canvas.SetLeft(_mock, leftDip);
        Canvas.SetTop(_mock, topDip);
        _mockLabel.Text = _mode == EditMode.Floating ? "Overlay\npreview" : "Dense";

        // Guides run from the preview's anchored corner out to the two nearest work-area edges.
        double cornerX = p.HAnchor == HAnchor.Left ? leftDip : leftDip + dipW;
        double cornerY = p.VAnchor == VAnchor.Top ? topDip : topDip + dipH;
        double edgeX = ((p.HAnchor == HAnchor.Left ? wa.X : wa.X + wa.Width) - bounds.X) / scale;
        double edgeY = ((p.VAnchor == VAnchor.Top ? wa.Y : wa.Y + wa.Height) - bounds.Y) / scale;
        _guideH.StartPoint = new Point(cornerX, cornerY);
        _guideH.EndPoint = new Point(edgeX, cornerY);
        _guideV.StartPoint = new Point(cornerX, cornerY);
        _guideV.EndPoint = new Point(cornerX, edgeY);

        string hLabel = p.HAnchor == HAnchor.Left ? "Left" : "Right";
        string vLabel = p.VAnchor == VAnchor.Top ? "Top" : "Bottom";
        _hudText.Text = _mode == EditMode.Dense
            ? $"{hLabel} edge  ·  {vLabel} {p.OffsetY:0} px"
            : $"{vLabel} {p.OffsetY:0} px  ·  {hLabel} {p.OffsetX:0} px";

        // Park the HUD just below the preview, or above it if that would run off the bottom.
        _hud.Measure(Size.Infinity);
        double hudY = topDip + dipH + 6;
        if (hudY + _hud.DesiredSize.Height > Height) hudY = topDip - _hud.DesiredSize.Height - 6;
        Canvas.SetLeft(_hud, Math.Max(0, leftDip));
        Canvas.SetTop(_hud, Math.Max(0, hudY));

        // Fill the selected half of the segmented control with the accent.
        StyleSegment(_floatingModeBtn, _mode == EditMode.Floating);
        StyleSegment(_denseModeBtn, _mode == EditMode.Dense);
    }

    private void OnMockPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        _grab = new Point(pos.X - Canvas.GetLeft(_mock), pos.Y - Canvas.GetTop(_mock));
        _dragging = true;
        e.Pointer.Capture(_mock);
    }

    private void OnMockMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(_canvas);
        UpdateFromDipTopLeft(pos.X - _grab.X, pos.Y - _grab.Y);
    }

    private void OnMockReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // Turns a dragged DIP top-left into a stored, corner-anchored placement for the current mode: clamp
    // on-screen, snap dense to the nearer edge (its X is edge-locked), then re-derive via PlacementMath and
    // stamp the target monitor. RefreshMock then re-lays everything out from the stored value.
    private void UpdateFromDipTopLeft(double leftDip, double topDip)
    {
        var screen = _ctx.TargetScreen;
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;
        double scale = screen.Scaling;

        var (dipW, dipH) = CurrentSizeDip();
        int physW = Math.Max(1, (int)(dipW * scale));
        int physH = Math.Max(1, (int)(dipH * scale));

        int physX = bounds.X + (int)Math.Round(leftDip * scale);
        int physY = bounds.Y + (int)Math.Round(topDip * scale);
        (physX, physY) = PlacementMath.Clamp(physX, physY, wa.X, wa.Y, wa.Width, wa.Height, physW, physH);

        if (_mode == EditMode.Dense)
        {
            // Dense is edge-docked: snap horizontally to whichever edge is nearer.
            bool left = physX - wa.X <= wa.X + wa.Width - (physX + physW);
            physX = left ? wa.X : wa.X + wa.Width - physW;
        }

        var placement = PlacementMath.FromPosition(physX, physY, wa.X, wa.Y, wa.Width, wa.Height, scale, physW, physH);
        placement.MonitorX = bounds.X;
        placement.MonitorY = bounds.Y;
        placement.MonitorW = bounds.Width;
        placement.MonitorH = bounds.Height;

        if (_mode == EditMode.Floating) _floating = placement;
        else { placement.OffsetX = 0; _dense = placement; }

        RefreshMock();
    }

    private void Commit()
    {
        _ctx.OnSave(_floating, _dense);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
