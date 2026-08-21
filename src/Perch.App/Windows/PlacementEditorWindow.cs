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
    OverlayPlacement? Docked,
    OverlayPlacement DefaultFloating,
    OverlayPlacement DefaultDense,
    OverlayPlacement DefaultDocked,
    (double W, double H) FloatSizeDip,
    (double W, double H) DenseSizeDip,
    (double W, double H) DockSizeDip,
    double MinWidthDip,
    Action<OverlayPlacement?, OverlayPlacement?, OverlayPlacement?, double?, double?> OnSave);

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
    private enum EditMode { Floating, Dense, Docked }

    private readonly PlacementEditorContext _ctx;

    // Per-mode working values, null meaning "use the default". Seeded from the saved placements.
    private OverlayPlacement? _floating;
    private OverlayPlacement? _dense;
    private OverlayPlacement? _docked;
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
    private Button _dockedModeBtn = null!;

    // The two dashed guides from the preview's anchored corner out to the nearest screen edges, and the
    // pill that reads back the live distances.
    private readonly Line _guideH = MakeGuide();
    private readonly Line _guideV = MakeGuide();
    private readonly Border _hud;
    private readonly TextBlock _hudText = new()
    {
        Foreground = Palette.FgBrush, FontSize = 12, FontWeight = FontWeight.Bold,
    };

    // Drag state: whether a move-drag is in progress and the pointer's grab offset within the preview (DIP).
    private bool _dragging;
    private Point _grab;

    // Editable widths (DIP) for the floating panel and docked column — the height stays the illustrative value
    // from the context. Seeded from the current configured widths; the user drags the preview's edge to change
    // them. Dense has no editable width. A ResizeEdge (below) tracks an in-progress edge drag.
    private double _floatWidthDip;
    private double _dockWidthDip;
    private enum ResizeEdge { None, Left, Right }
    private ResizeEdge _resizeEdge;
    private double _resizeFixedEdgeDip;   // the held (opposite) edge's canvas-DIP X while resizing
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private const double EdgeGrabDip = 10;   // how close to an edge counts as a resize grab

    public PlacementEditorWindow(PlacementEditorContext ctx)
    {
        _ctx = ctx;
        _floating = ctx.Floating?.Clone();
        _dense = ctx.Dense?.Clone();
        _docked = ctx.Docked?.Clone();
        _floatWidthDip = Math.Max(ctx.MinWidthDip, ctx.FloatSizeDip.W);
        _dockWidthDip = Math.Max(ctx.MinWidthDip, ctx.DockSizeDip.W);

        Title = "Set initial placements";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Cover the target monitor's full bounds (physical → DIP via its scaling).
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
            Text = "Drag the preview to place it; drag its side edge to set the width, then choose Done.\n" +
                   "Placement is measured from the nearest corner, so it sticks if your resolution changes.",
            Foreground = Palette.FgBrush, FontSize = 14, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
        };

        // Overlay / Dense is a one-or-the-other choice, so present it as a joined segmented control (the
        // selected half fills with the accent) rather than two independent buttons.
        _floatingModeBtn = MakeSegment("Overlay", EditMode.Floating);
        _denseModeBtn = MakeSegment("Dense", EditMode.Dense);
        _dockedModeBtn = MakeSegment("Docked", EditMode.Docked);
        var segmented = new Border
        {
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Palette.ButtonBgBrush,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 0,
                Children =
                {
                    _floatingModeBtn, new Border { Width = 1, Background = Palette.BorderBrush },
                    _denseModeBtn, new Border { Width = 1, Background = Palette.BorderBrush },
                    _dockedModeBtn,
                },
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
        if (_mode == EditMode.Floating) { _floating = null; _floatWidthDip = _ctx.MinWidthDip; }
        else if (_mode == EditMode.Dense) _dense = null;
        else { _docked = null; _dockWidthDip = _ctx.MinWidthDip; }
        RefreshMock();
    }

    // The placement currently in effect for the active mode (working value, or the default when unset).
    private OverlayPlacement CurrentPlacement() => _mode switch
    {
        EditMode.Floating => _floating ?? _ctx.DefaultFloating,
        EditMode.Dense    => _dense ?? _ctx.DefaultDense,
        _                 => _docked ?? _ctx.DefaultDocked,
    };

    private (double W, double H) CurrentSizeDip() => _mode switch
    {
        EditMode.Floating => (_floatWidthDip, _ctx.FloatSizeDip.H),
        EditMode.Dense    => _ctx.DenseSizeDip,
        _                 => (_dockWidthDip, _ctx.DockSizeDip.H),
    };

    // The widest a preview may grow on the target monitor: the work-area width less small margins, so a preview
    // can't exceed the screen. Docked can fill more of the screen than floating, but the same cap is fine.
    private double MaxWidthDip()
    {
        var screen = _ctx.TargetScreen;
        double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        return Math.Max(_ctx.MinWidthDip, screen.WorkingArea.Width / scale - 32);
    }

    // Whether the active mode's width is editable (floating from either edge; docked from its open edge only).
    private bool WidthEditable => _mode is EditMode.Floating or EditMode.Docked;

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
        _mockLabel.Text = _mode switch
        {
            EditMode.Floating => "Overlay\npreview",
            EditMode.Dense    => "Dense",
            _                 => "Docked\ncolumn",
        };

        // Guides run from the preview's anchored point out to the two nearest work-area edges. The vertical
        // anchor is always the mock's top edge (the header) — OffsetY measures from there regardless of
        // Top/Bottom — since the real panel's height is dynamic; only the horizontal uses the near edge.
        double cornerX = p.HAnchor == HAnchor.Left ? leftDip : leftDip + dipW;
        double cornerY = topDip;
        double edgeX = ((p.HAnchor == HAnchor.Left ? wa.X : wa.X + wa.Width) - bounds.X) / scale;
        double edgeY = ((p.VAnchor == VAnchor.Top ? wa.Y : wa.Y + wa.Height) - bounds.Y) / scale;
        _guideH.StartPoint = new Point(cornerX, cornerY);
        _guideH.EndPoint = new Point(edgeX, cornerY);
        _guideV.StartPoint = new Point(cornerX, cornerY);
        _guideV.EndPoint = new Point(cornerX, edgeY);

        string hLabel = p.HAnchor == HAnchor.Left ? "Left" : "Right";
        string vLabel = p.VAnchor == VAnchor.Top ? "Top" : "Bottom";
        _hudText.Text = _mode switch
        {
            EditMode.Dense    => $"{hLabel} edge  ·  {vLabel} {p.OffsetY:0} px",
            EditMode.Docked   => $"{hLabel} edge  ·  {dipW:0} px wide",
            _                 => $"{vLabel} {p.OffsetY:0} px  ·  {hLabel} {p.OffsetX:0} px  ·  {dipW:0} px wide",
        };

        // Park the HUD just below the preview, or above it if that would run off the bottom.
        _hud.Measure(Size.Infinity);
        double hudY = topDip + dipH + 6;
        if (hudY + _hud.DesiredSize.Height > Height) hudY = topDip - _hud.DesiredSize.Height - 6;
        Canvas.SetLeft(_hud, Math.Max(0, leftDip));
        Canvas.SetTop(_hud, Math.Max(0, hudY));

        // Fill the selected segment with the accent.
        StyleSegment(_floatingModeBtn, _mode == EditMode.Floating);
        StyleSegment(_denseModeBtn, _mode == EditMode.Dense);
        StyleSegment(_dockedModeBtn, _mode == EditMode.Docked);
    }

    // Which editable edge (if any) a mock-local X is over: floating from either edge, docked only from its open
    // (inner) edge — the outer edge is locked flush to the screen.
    private ResizeEdge EditableEdgeAt(double localX, double mockWidth)
    {
        if (_mode == EditMode.Floating)
        {
            if (localX <= EdgeGrabDip) return ResizeEdge.Left;
            if (localX >= mockWidth - EdgeGrabDip) return ResizeEdge.Right;
        }
        else if (_mode == EditMode.Docked)
        {
            bool openLeft = CurrentPlacement().HAnchor != HAnchor.Left;   // right-docked → open edge on the left
            if (openLeft && localX <= EdgeGrabDip) return ResizeEdge.Left;
            if (!openLeft && localX >= mockWidth - EdgeGrabDip) return ResizeEdge.Right;
        }
        return ResizeEdge.None;
    }

    private void OnMockPressed(object? sender, PointerPressedEventArgs e)
    {
        var edge = WidthEditable ? EditableEdgeAt(e.GetPosition(_mock).X, _mock.Width) : ResizeEdge.None;
        if (edge != ResizeEdge.None)
        {
            _resizeEdge = edge;
            double leftDip = Canvas.GetLeft(_mock);
            _resizeFixedEdgeDip = edge == ResizeEdge.Left ? leftDip + _mock.Width : leftDip; // hold the opposite edge
            e.Pointer.Capture(_mock);
            return;
        }
        var pos = e.GetPosition(_canvas);
        _grab = new Point(pos.X - Canvas.GetLeft(_mock), pos.Y - Canvas.GetTop(_mock));
        _dragging = true;
        e.Pointer.Capture(_mock);
    }

    private void OnMockMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeEdge != ResizeEdge.None) { DoResize(e.GetPosition(_canvas).X); return; }
        if (_dragging)
        {
            var pos = e.GetPosition(_canvas);
            UpdateFromDipTopLeft(pos.X - _grab.X, pos.Y - _grab.Y);
            return;
        }
        // Idle hover: show a resize cursor over an editable edge, the move cursor elsewhere.
        _mock.Cursor = WidthEditable && EditableEdgeAt(e.GetPosition(_mock).X, _mock.Width) != ResizeEdge.None
            ? ResizeCursor : MoveCursor;
    }

    private void OnMockReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        _resizeEdge = ResizeEdge.None;
        e.Pointer.Capture(null);
    }

    // Applies an edge-resize to the active mode: grow from the grabbed edge, holding the opposite edge fixed.
    // Floating re-derives its placement from the new geometry (so position + width both reflect the drag);
    // docked keeps its side and RefreshMock re-snaps the column flush to the edge at the new width.
    private void DoResize(double canvasX)
    {
        double min = _ctx.MinWidthDip, max = MaxWidthDip();
        double newWidth, leftDip;
        if (_resizeEdge == ResizeEdge.Right)
        {
            leftDip = _resizeFixedEdgeDip;                       // left edge held
            newWidth = Math.Clamp(canvasX - leftDip, min, max);
        }
        else
        {
            double rightDip = _resizeFixedEdgeDip;               // right edge held
            newWidth = Math.Clamp(rightDip - canvasX, min, max);
            leftDip = rightDip - newWidth;
        }

        if (_mode == EditMode.Floating)
        {
            _floatWidthDip = newWidth;
            UpdateFromDipTopLeft(leftDip, Canvas.GetTop(_mock));  // re-derive placement at the new width
        }
        else
        {
            _dockWidthDip = newWidth;
            RefreshMock();                                        // re-snaps flush to the docked edge
        }
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

        if (_mode is EditMode.Dense or EditMode.Docked)
        {
            // Dense/docked are edge-docked: snap horizontally to whichever edge is nearer.
            bool left = physX - wa.X <= wa.X + wa.Width - (physX + physW);
            physX = left ? wa.X : wa.X + wa.Width - physW;
        }

        var placement = PlacementMath.FromPosition(physX, physY, wa.X, wa.Y, wa.Width, wa.Height, scale, physW, physH);
        placement.MonitorX = bounds.X;
        placement.MonitorY = bounds.Y;
        placement.MonitorW = bounds.Width;
        placement.MonitorH = bounds.Height;

        if (_mode == EditMode.Floating) _floating = placement;
        else if (_mode == EditMode.Dense) { placement.OffsetX = 0; _dense = placement; }
        else { placement.OffsetX = 0; placement.OffsetY = 0; _docked = placement; } // docked: only the side matters

        RefreshMock();
    }

    private void Commit()
    {
        // Widths persist only when they differ from the default (null = "use the default"), mirroring how a
        // null placement means "use the computed default".
        double? fw = _floatWidthDip > _ctx.MinWidthDip + 0.5 ? _floatWidthDip : null;
        double? dw = _dockWidthDip > _ctx.MinWidthDip + 0.5 ? _dockWidthDip : null;
        _ctx.OnSave(_floating, _dense, _docked, fw, dw);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
