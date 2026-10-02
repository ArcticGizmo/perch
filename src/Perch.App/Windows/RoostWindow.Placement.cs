using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data.Roost;
using PlacementMode = global::Avalonia.Controls.PlacementMode;

namespace Perch.Avalonia.Windows;

// Putting a session in a region: the empty-region picker, and dragging a rail row or pane header onto a region or a
// tab header (and a tab header onto another, to reorder).
internal sealed partial class RoostWindow
{
    // ── Empty-region picker ──────────────────────────────────────────────────

    // Click an empty region: pick a session for it — those in no tab first (most urgent first), then the ones in
    // other tabs (picking one moves it here) — or start a new session in it.
    private void ShowPicker(int slot)
    {
        if (slot < 0 || slot >= _slotRegions.Count) return;
        var tab = _tabs.Active;
        if (tab.IsFocus) return;
        int region = _slotRegions[slot];
        var flyout = new Flyout { Placement = PlacementMode.Center };
        var list = new StackPanel { Spacing = 1, MinWidth = 260 };
        list.Children.Add(new TextBlock
        {
            Text = "Put a session here", FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, Margin = new Thickness(8, 0, 8, 4),
        });
        var candidates = _tabs.Candidates(tab.Id, _roster.Panes);
        if (candidates.Count == 0)
            list.Children.Add(new TextBlock { Text = "Every live session is already in this tab", FontSize = 12, Foreground = _p.Muted, Margin = new Thickness(8, 4) });
        foreach (var pane in candidates)
        {
            var key = pane.Key;
            list.Children.Add(PickerRow(pane, TabTagFor(key), () => { flyout.Hide(); PlacePane(key, slot); }));
        }
        list.Children.Add(new Border { Height = 1, Background = _p.Border, Margin = new Thickness(4, 4) });
        list.Children.Add(PickerRow(null, null, () => { flyout.Hide(); StartNewSession(new RoostPlacement(tab.Id, region)); }));
        flyout.Content = new ScrollViewer { MaxHeight = 380, Content = list };
        _openFlyout = flyout;
        flyout.ShowAt(_cells[slot].Host);
    }

    private Border PickerRow(RoostPane? pane, string? tag, Action onPick)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (pane is not null)
        {
            dock.Children.Add(new Ellipse
            {
                Width = 8, Height = 8, Fill = _p.PaneDot(pane), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0), [DockPanel.DockProperty] = Dock.Left,
            });
            if (tag is not null)
                dock.Children.Add(new TextBlock
                {
                    Text = tag, FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0), MaxWidth = 90, TextTrimming = TextTrimming.CharacterEllipsis,
                    [DockPanel.DockProperty] = Dock.Right,
                });
        }
        dock.Children.Add(new TextBlock
        {
            Text = pane?.Session.DisplayName ?? "+ New session here", FontFamily = _p.Body, FontSize = 13,
            FontWeight = FontWeight.SemiBold, Foreground = pane is null ? _p.Brand : _p.Text,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var row = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 6), Cursor = new Cursor(StandardCursorType.Hand), Child = dock,
        };
        return row.HoverWash(_p.Raised2).OnLeftClick(onPick, handle: true);
    }

    // The tab a session lives in, as the rail and picker label it — null when it's in none.
    private string? TabTagFor(string key) =>
        _tabs.Locate(key).FirstOrDefault() is { TabId: { } id } ? id == RoostTabSet.FocusId ? "focus" : _tabs.Find(id)?.Name : null;

    // ── Drag to place ─────────────────────────────────────────────────────────

    // Pressing and moving a few pixels starts a drag, and a ghost chip follows the pointer. A session (a pane header
    // or a rail row) marks the region under it — letting go there puts the session in that region (PlacePane) — or
    // a tab header (DropOnTab). A tab (its header) marks another tab's header, and letting go there moves it to that
    // place (Focus stays first).
    private void AttachDragSource(Control source, string id, bool isTab)
    {
        source.PointerPressed += (_, e) =>
        {
            if ((isTab && _renaming == id) || !e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) return;
            _press = new Press(id, isTab, e.GetPosition(_overlay), source);
            e.Pointer.Capture(source);
        };
        source.PointerMoved += (_, e) =>
        {
            if (_press is not { } press || !ReferenceEquals(press.Source, source)) return;
            var at = e.GetPosition(_overlay);
            if (Math.Abs(at.X - press.Start.X) + Math.Abs(at.Y - press.Start.Y) < DragThreshold) return;
            _press = press with { Source = _root };   // first, so the source's capture-lost doesn't end the drag
            e.Pointer.Capture(_root);
            StartGhost(press);
            MoveGhost(at);
        };
        source.PointerReleased += (_, e) =>
        {
            if (_press is not { } press || !ReferenceEquals(press.Source, source)) return;
            if (_ghost is not null) e.Handled = true;
            EndDrag(drop: true);
            e.Pointer.Capture(null);
        };
        source.PointerCaptureLost += (_, _) =>
        {
            if (_press is { } press && ReferenceEquals(press.Source, source)) EndDrag(drop: false);
        };
    }

    // The tab header under the pointer, if any.
    private string? TabAt(Point at)
    {
        foreach (var (id, v) in _tabViews)
        {
            if (!v.Button.IsVisible || v.Button.TranslatePoint(default, _overlay) is not { } origin) continue;
            if (new Rect(origin, v.Button.Bounds.Size).Contains(at)) return id;
        }
        return null;
    }

    private void MarkDrop(Control target)
    {
        if (target.TranslatePoint(default, _overlay) is not { } origin) return;
        Canvas.SetLeft(_dropMark, origin.X);
        Canvas.SetTop(_dropMark, origin.Y);
        _dropMark.Width = target.Bounds.Width;
        _dropMark.Height = target.Bounds.Height;
        _dropMark.CornerRadius = target is Border { CornerRadius: var r } ? r : new CornerRadius(12);
    }

    private void StartGhost(Press press)
    {
        var name = press.IsTab ? _tabs.Find(press.Id)?.Name ?? "" : _roster.Find(press.Id)?.Session.DisplayName ?? press.Id;
        _ghost = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(12, 5), BorderThickness = new Thickness(1),
            BorderBrush = _p.Brand, Background = _p.Raised2, Opacity = 0.95,
            Child = new TextBlock { Text = name, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = _p.Text },
        };
        _overlay.Children.Add(_ghost);
    }

    // Moves the ghost to the pointer and marks what's under it: a session drag targets the active tab's regions or
    // any tab header; a tab drag, another tab header.
    private void MoveGhost(Point at)
    {
        if (_ghost is null) return;
        Canvas.SetLeft(_ghost, at.X + 12);
        Canvas.SetTop(_ghost, at.Y + 8);
        _dropSlot = -1;
        _dropTab = TabAt(at);
        if (PressedTab is { } moving && _dropTab == moving) _dropTab = null;
        if (_dropTab is { } tabId) MarkDrop(_tabViews[tabId].Button);
        else if (PressedSession is not null && _painter is null)   // the painter covers the regions
            for (int i = 0; i < _slotRegions.Count; i++)
            {
                var host = _cells[i].Host;
                if (!host.IsVisible || host.TranslatePoint(default, _overlay) is not { } origin) continue;
                if (!new Rect(origin, host.Bounds.Size).Contains(at)) continue;
                _dropSlot = i;
                MarkDrop(host);
                _dropMark.CornerRadius = new CornerRadius(12);
                break;
            }
        _dropMark.IsVisible = _dropSlot >= 0 || _dropTab is not null;

        // A session held over a background tab's header: arm the switch (moving onto another tab re-arms it).
        var hover = PressedSession is not null && _dropTab != _tabs.ActiveId ? _dropTab : null;
        if (hover == _hoverTab) return;
        _hoverTab = hover;
        _hoverTimer.Stop();
        if (hover is not null) _hoverTimer.Start();
    }

    // The hover delay ran out with the drag still over that tab: switch to it. The drag carries on, so the session
    // can be let go on one of its regions (or right there on its header).
    private void HoverSwitch()
    {
        _hoverTimer.Stop();
        if (PressedSession is null || _ghost is null || _hoverTab is not { } id || _dropTab != id) return;
        _hoverTab = null;
        ActivateTab(id);
    }

    private void EndDrag(bool drop)
    {
        var key = PressedSession;
        var movingTab = PressedTab;
        int slot = _dropSlot;
        var tabId = _dropTab;
        bool dragged = _ghost is not null;
        _press = null;
        if (_ghost is not null) _overlay.Children.Remove(_ghost);
        _ghost = null;
        _dropMark.IsVisible = false;
        _dropSlot = -1;
        _dropTab = null;
        _hoverTab = null;
        _hoverTimer.Stop();
        if (drop && dragged && movingTab is not null && tabId is not null) MoveTabTo(movingTab, tabId);
        else if (drop && dragged && key is not null && tabId is not null) DropOnTab(key, tabId);
        else if (drop && dragged && key is not null && slot >= 0) PlacePane(key, slot);
        else { RefreshRail(); RefreshTabStrip(); }
    }

    // A session dropped on a tab header goes into that tab (its first empty region, else a swap) without switching
    // to it — like dropping a file on a folder. Dropped on the active tab, it's focused there (FocusPane refreshes
    // when that moved anything).
    private void DropOnTab(string key, string tabId)
    {
        if (_roster.Find(key) is null || _tabs.DropOnTab(tabId, key) is null) return;
        _everPlaced.Add(key);
        if (tabId == _tabs.ActiveId) FocusPane(key);
        else Refresh();
    }

    // A tab dragged onto another's header takes its place (onto Focus = first).
    private void MoveTabTo(string tabId, string ontoId)
    {
        int to = ontoId == RoostTabSet.FocusId ? 0 : _tabs.Tabs.ToList().FindIndex(t => t.Id == ontoId);
        if (to >= 0 && _tabs.MoveTab(tabId, to)) Refresh();
        else RefreshTabStrip();
    }
}
