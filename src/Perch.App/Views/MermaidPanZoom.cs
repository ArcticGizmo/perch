using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Perch.Data.Mermaid;

namespace Perch.Avalonia.Views;

/// <summary>
/// A clipped viewport onto one diagram that the user can zoom (wheel, about the cursor; double-click zooms in) and
/// drag around. The diagram keeps its natural layout size and is scaled with a render transform, which redraws the
/// vector scene at the new scale (text stays sharp at any zoom). The arithmetic lives in <see cref="PanZoom"/>.
/// Until the user zooms or drags, the view refits whenever the viewport resizes.
/// </summary>
internal sealed class MermaidPanZoom : Panel
{
    private const double FitPadding = 32, MaxFitZoom = 2, Keep = 48, WheelStep = 1.2, ButtonStep = 1.25;

    private readonly Control _content;
    private PanZoom _view = PanZoom.Identity;
    private bool _autoFit = true;
    private Point? _dragFrom;

    public MermaidPanZoom(Control content)
    {
        _content = content;
        _content.RenderTransformOrigin = RelativePoint.TopLeft;
        Children.Add(_content);
        ClipToBounds = true;
        Background = Brushes.Transparent;   // hit-testable everywhere, not just over the diagram's strokes
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
    }

    /// <summary>Raised whenever the zoom level changes, for a readout to follow.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>Raised when the user first moves or zooms the view (not on an automatic refit).</summary>
    public event Action? Interacted;

    public double Zoom => _view.Zoom;

    private MSize ContentSize => new(_content.DesiredSize.Width, _content.DesiredSize.Height);
    private MSize ViewportSize => new(Bounds.Width, Bounds.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        _content.Measure(Size.Infinity);
        return default;   // fill whatever the parent gives; the content's size doesn't drive ours
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _content.Arrange(new Rect(_content.DesiredSize));
        if (_autoFit)
            _view = PanZoom.Fit(ContentSize, new MSize(finalSize.Width, finalSize.Height), FitPadding, MaxFitZoom);
        Apply();
        return finalSize;
    }

    public void Fit()
    {
        _autoFit = true;
        _view = PanZoom.Fit(ContentSize, ViewportSize, FitPadding, MaxFitZoom);
        Apply();
    }

    public void ActualSize() => Set(PanZoom.Centered(1, ContentSize, ViewportSize));

    public void ZoomIn() => Set(_view.ZoomAt(Middle, ButtonStep));

    public void ZoomOut() => Set(_view.ZoomAt(Middle, 1 / ButtonStep));

    public void PanBy(double dx, double dy) => Set(_view.Pan(dx, dy));

    /// <summary>Zooms about a viewport point, for the render harness's zoomed capture.</summary>
    internal void ZoomAtForRender(Point at, double factor) => Set(_view.ZoomAt(new MPoint(at.X, at.Y), factor));

    private MPoint Middle => new(Bounds.Width / 2, Bounds.Height / 2);

    private void Set(PanZoom view)
    {
        _autoFit = false;
        Interacted?.Invoke();
        _view = view.Clamp(ContentSize, ViewportSize, Keep);
        Apply();
    }

    private double _lastZoom = double.NaN;

    private void Apply()
    {
        _content.RenderTransform = new MatrixTransform(new Matrix(_view.Zoom, 0, 0, _view.Zoom, _view.X, _view.Y));
        if (_view.Zoom != _lastZoom)
        {
            _lastZoom = _view.Zoom;
            ZoomChanged?.Invoke(_view.Zoom);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        Set(_view.ZoomAt(new MPoint(p.X, p.Y), Math.Pow(WheelStep, e.Delta.Y)));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            Set(_view.ZoomAt(new MPoint(point.Position.X, point.Position.Y), 2));
            e.Handled = true;
            return;
        }
        _dragFrom = point.Position;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from) return;
        var p = e.GetPosition(this);
        _dragFrom = p;
        Set(_view.Pan(p.X - from.X, p.Y - from.Y));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void EndDrag(IPointer pointer)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Hand);
    }
}
