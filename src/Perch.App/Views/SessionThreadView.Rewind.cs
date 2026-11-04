using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Perch.Avalonia.Theming;
using Perch.Data.Control;

namespace Perch.Avalonia.Views;

// Rewind mode (docs/session-rewind-plan.md, R3): picking a rewind point is scrolling the thread itself. Each prompt
// that can anchor a rewind is a click target, the chosen one carries a line across the column above it, and
// everything from it down is dimmed, because that is what goes. The window owns the mode (its bar, keys and the
// actions); the view only draws it and reports clicks.
internal sealed partial class SessionThreadView
{
    private readonly Canvas _rewindLayer = new() { IsHitTestVisible = false };
    private Border? _rewindLine;
    private UserMessageItem? _rewindSelected;
    private HashSet<UserMessageItem>? _rewindTargets;
    private Border? _rewindHover;

    // What lies past the rewind point: present, but clearly not staying.
    private const double DroppedOpacity = 0.32;

    /// <summary>Prompts show a "rewind to here" hover button. Only for a controlled session (live or dormant);
    /// history and read-only tails leave it off. Set before <see cref="Bind"/>.</summary>
    public bool CanRewind { get; set; }

    /// <summary>A prompt was picked as the rewind point: its hover button outside rewind mode, or a click on it
    /// inside the mode.</summary>
    public event Action<UserMessageItem>? RewindRequested;

    private bool InRewindMode => _rewindTargets is not null;

    private void InitRewind()
    {
        _stack.AddHandler(PointerPressedEvent, OnRewindPress, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _stack.AddHandler(PointerMovedEvent, OnRewindHover, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _stack.PointerExited += (_, _) => SetRewindHover(null);
        LayoutUpdated += (_, _) => { if (InRewindMode) PositionRewindLine(); };
    }

    /// <summary>Shows the thread in rewind mode with <paramref name="selected"/> as the point (null = no point yet),
    /// or leaves the mode when <paramref name="targets"/> is null.</summary>
    public void SetRewindView(UserMessageItem? selected, IReadOnlyCollection<UserMessageItem>? targets)
    {
        _rewindTargets = targets is null ? null : [.. targets];
        _rewindSelected = targets is null ? null : selected;
        SetRewindHover(null);
        ApplyRewindVisuals();
        if (selected is not null && targets is not null) ScrollToRewindPoint(selected);
    }

    // Keeps the rewind point where both what stays (above) and what goes (below) can be seen. Only moves when the
    // point is near an edge or out of view, so a click on a visible prompt doesn't jump the thread under the pointer.
    private void ScrollToRewindPoint(UserMessageItem item)
    {
        FlushBacklog();
        if (!_wraps.TryGetValue(item, out var wrap)) return;
        if (wrap.Child is null) Hydrate(item, wrap);
        _stickToBottom = false;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_rewindSelected, item) || RowTop(wrap) is not { } top) return;
            double y = top - Offset.Y;
            double margin = Math.Min(80, Viewport.Height / 5);
            if (y >= margin && y + wrap.Bounds.Height <= Viewport.Height - margin) return;
            double max = Math.Max(0, Extent.Height - Viewport.Height);
            Offset = new Vector(Offset.X, Math.Clamp(top - Viewport.Height * 0.4, 0, max));
        }, DispatcherPriority.Loaded);
    }

    // Dims what the rewind drops and marks the targets. Wrappers outlive their controls, so this survives
    // virtualization; Bind and AddItem call it again for the wrappers they make.
    private void ApplyRewindVisuals()
    {
        if (_conv is null) return;
        int cut = _rewindSelected is null ? -1 : IndexOfItem(_rewindSelected);
        var items = _conv.Items;
        for (int i = 0; i < items.Count; i++)
            if (_wraps.TryGetValue(items[i], out var wrap)) ApplyRewindVisual(wrap, items[i], dropped: cut >= 0 && i >= cut);
        PositionRewindLine();
    }

    private void ApplyRewindVisual(Border wrap, ConversationItem item, bool dropped)
    {
        bool target = item is UserMessageItem u && _rewindTargets?.Contains(u) == true;
        bool selected = ReferenceEquals(item, _rewindSelected);
        // The selected prompt goes too (its text returns to the composer), but it's the one being pointed at.
        wrap.Opacity = selected ? SelectedOpacity : dropped ? DroppedOpacity : 1;
        wrap.Cursor = target ? new Cursor(StandardCursorType.Hand) : null;
        wrap.BorderBrush = selected ? _p.Brand
            : ReferenceEquals(wrap, _rewindHover) ? _p.BrandLine
            : Brushes.Transparent;
    }

    private const double SelectedOpacity = 0.75;

    private int IndexOfItem(ConversationItem item)
    {
        if (_conv is null) return -1;
        var items = _conv.Items;
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }

    // The line across the column, just above the selected prompt, with its label riding on it.
    private void PositionRewindLine()
    {
        if (_rewindSelected is null || !_wraps.TryGetValue(_rewindSelected, out var wrap) || RowTop(wrap) is not { } top)
        {
            if (_rewindLine is not null) _rewindLine.IsVisible = false;
            return;
        }
        _rewindLine ??= BuildRewindLine();
        _rewindLine.IsVisible = true;
        double y = top - _stack.Spacing / 2;
        double x = _stack.Bounds.X;
        double w = _stack.Bounds.Width;
        if (_rewindLine.Width != w) _rewindLine.Width = w;
        _rewindLine.Measure(new Size(w, double.PositiveInfinity));
        double lineTop = y - _rewindLine.DesiredSize.Height / 2;
        if (Canvas.GetLeft(_rewindLine) != x) Canvas.SetLeft(_rewindLine, x);
        if (Canvas.GetTop(_rewindLine) != lineTop) Canvas.SetTop(_rewindLine, lineTop);
    }

    private Border BuildRewindLine()
    {
        var label = new Border
        {
            Background = _p.Brand, CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(9, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "↺ rewind to here", FontFamily = _p.Mono, FontSize = 11, Foreground = _p.BrandInk,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var rule = new Border
        {
            Height = 2, Background = _p.Brand, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(1),
        };
        var line = new Border { Child = new Grid { Children = { rule, label } } };
        _rewindLayer.Children.Add(line);
        return line;
    }

    // The prompt wrapper under the pointer, when it's a rewind target.
    private Border? TargetWrapAt(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Border { Tag: UserMessageItem u } b && ReferenceEquals(b.GetVisualParent(), _stack))
                return _rewindTargets?.Contains(u) == true ? b : null;
        return null;
    }

    private void OnRewindPress(object? sender, PointerPressedEventArgs e)
    {
        if (!InRewindMode || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (TargetWrapAt(e.Source) is not { Tag: UserMessageItem u }) return;
        e.Handled = true;   // a pick, not the start of a text selection
        RewindRequested?.Invoke(u);
    }

    private void OnRewindHover(object? sender, PointerEventArgs e)
    {
        if (InRewindMode) SetRewindHover(TargetWrapAt(e.Source));
    }

    private void SetRewindHover(Border? wrap)
    {
        if (ReferenceEquals(wrap, _rewindHover)) return;
        var old = _rewindHover;
        _rewindHover = wrap;
        if (old?.Tag is ConversationItem oi) old.BorderBrush = ReferenceEquals(oi, _rewindSelected) ? _p.Brand : Brushes.Transparent;
        if (wrap?.Tag is ConversationItem ni && !ReferenceEquals(ni, _rewindSelected)) wrap.BorderBrush = _p.BrandLine;
    }

    private void RaiseRewindRequested(UserMessageItem u) => RewindRequested?.Invoke(u);

    // The hover button on a prompt bubble that opens rewind mode on it.
    private Border RewindButton(UserMessageItem u)
    {
        var glyph = new TextBlock
        {
            Text = "↺", FontSize = 13, Foreground = _p.Muted,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = _p.Raised2,
            BorderBrush = _p.Border, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = false, VerticalAlignment = VerticalAlignment.Top, Child = glyph,
        };
        ToolTip.SetTip(btn, "Rewind to before this prompt…");
        btn.PointerEntered += (_, _) => btn.Background = _p.Border;
        btn.PointerExited += (_, _) => btn.Background = _p.Raised2;
        btn.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Handled = true;
            RaiseRewindRequested(u);
        };
        return btn;
    }
}
