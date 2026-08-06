using Avalonia;
using Avalonia.Controls;
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
            Child = _mockLabel,
        };
        _canvas.Children.Add(_mock);

        Content = BuildContent();
        RefreshMock();
    }

    private Control BuildContent()
    {
        // The dim veil that signals "you're placing the overlay, not using the app".
        var dim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) };

        var instructions = new TextBlock
        {
            Text = "Position the preview where the overlay should first appear, then choose Done.\n" +
                   "Placement is measured from the nearest corner, so it sticks if your resolution changes.",
            Foreground = Palette.FgBrush, FontSize = 14, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
        };

        _floatingModeBtn = MakeButton("Overlay", () => SetMode(EditMode.Floating));
        _denseModeBtn = MakeButton("Dense", () => SetMode(EditMode.Dense));
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
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { _floatingModeBtn, _denseModeBtn, new Border { Width = 12 }, resetBtn, doneBtn, cancelBtn },
            },
        };

        return new Grid { Children = { dim, _canvas, instructions, toolbar } };
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

    // Positions and sizes the preview for the current mode, and reflects the mode in the toolbar. The mock's
    // Canvas coordinates are DIP relative to the window's top-left, which maps to the screen's bounds origin.
    private void RefreshMock()
    {
        var screen = _ctx.TargetScreen;
        var wa = screen.WorkingArea;
        var bounds = screen.Bounds;
        double scale = screen.Scaling;

        var (dipW, dipH) = CurrentSizeDip();
        int physW = Math.Max(1, (int)(dipW * scale));
        int physH = Math.Max(1, (int)(dipH * scale));

        var (px, py) = PlacementMath.ToPosition(CurrentPlacement(),
            wa.X, wa.Y, wa.Width, wa.Height, scale, physW, physH);

        _mock.Width = dipW;
        _mock.Height = dipH;
        Canvas.SetLeft(_mock, (px - bounds.X) / scale);
        Canvas.SetTop(_mock, (py - bounds.Y) / scale);
        _mockLabel.Text = _mode == EditMode.Floating ? "Overlay\npreview" : "Dense";

        // Highlight the active mode button.
        _floatingModeBtn.FontWeight = _mode == EditMode.Floating ? FontWeight.Bold : FontWeight.Normal;
        _denseModeBtn.FontWeight = _mode == EditMode.Dense ? FontWeight.Bold : FontWeight.Normal;
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
