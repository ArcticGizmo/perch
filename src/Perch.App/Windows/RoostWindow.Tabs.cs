using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data.Roost;

namespace Perch.Avalonia.Windows;

// The Roost's tab strip: a button per tab with its light and count, rename in place, close, the tab menu, and the
// layout painter it opens.
internal sealed partial class RoostWindow
{
    // ── Tab strip ─────────────────────────────────────────────────────────────

    private sealed record TabView(Border Button, Ellipse Dot, Ellipse Halo, TextBlock Name, TextBox Editor, Border CountPill, TextBlock Count);

    private readonly Dictionary<string, TabView> _tabViews = new(StringComparer.Ordinal);
    private string? _stripSig;
    // Background tabs whose light is needs-you (awaiting input or an API error): their dot's halo breathes.
    private readonly HashSet<string> _pulsingTabs = new(StringComparer.Ordinal);
    // The tab whose name is being edited in place (double-click, F2, its menu, or a new tab), or null.
    private string? _renaming;

    // The strip is rebuilt only when its tabs change (added, closed, renamed, reordered); every other pass just
    // re-lights the existing buttons. Never mid-drag: that would drop the captured tab.
    private void RefreshTabStrip()
    {
        var sig = string.Join("|", _tabs.All.Select(t => $"{t.Id}:{t.Name}"));
        if (sig != _stripSig && PressedTab is null)
        {
            _stripSig = sig;
            _tabStrip.Children.Clear();
            _tabViews.Clear();
            _renaming = null;
            int n = 0;
            foreach (var tab in _tabs.All)
            {
                var view = TabButton(tab, tab.IsFocus ? 0 : ++n);
                _tabViews[tab.Id] = view;
                _tabStrip.Children.Add(view.Button);
            }
            if (_tabs.Tabs.Count < RoostTabSet.MaxTabs) _tabStrip.Children.Add(NewTabButton());
        }
        _pulsingTabs.Clear();
        foreach (var tab in _tabs.All)
            if (_tabViews.TryGetValue(tab.Id, out var view)) UpdateTabButton(view, tab);
    }

    private TabView TabButton(RoostTab tab, int number)
    {
        var dot = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var halo = new Ellipse { Width = 15, Height = 15, Opacity = 0, IsHitTestVisible = false };
        var name = new TextBlock
        {
            Text = tab.IsFocus ? "◻ Focus" : tab.Name, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var editor = new TextBox
        {
            Text = tab.Name, PlaceholderText = "Tab name", MaxLength = RoostTabSet.MaxNameLength, FontFamily = _p.Body,
            FontWeight = FontWeight.SemiBold, FontSize = 12.5, MinHeight = 0, MinWidth = 90, MaxWidth = 180,
            // The negative margin keeps the editor (taller than the name: its border and padding) from growing the strip.
            Padding = new Thickness(5, 1), Margin = new Thickness(0, -4), VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
        };
        var count = new TextBlock { FontFamily = _p.Mono, FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var countPill = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(6, 0), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Child = count,
        };
        var chord = tab.IsFocus ? RoostKeys.ChordFor(RoostCommand.FocusTab) : number <= 9 ? RoostKeys.ChordFor(RoostCommand.Tab, number) : null;
        var button = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(9, 6, 11, 7), BorderThickness = new Thickness(1, 1, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand), MinHeight = 32,
            [ToolTip.TipProperty] = (tab.IsFocus ? "Focus — sessions in no tab open here" : $"{tab.Name} — double-click to rename")
                + (chord is null ? "" : $"  ·  {chord}"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children =
                {
                    new Panel { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Children = { halo, dot } },
                    name, editor, countPill,
                },
            },
        };
        var id = tab.Id;
        button.HoverWash(_p.Raised2, keep: () => id == _tabs.ActiveId);
        if (!tab.IsFocus)
        {
            AttachDragSource(button, id, isTab: true);   // first, so a reorder marks the release handled before the click sees it
            button.DoubleTapped += (_, e) => { if (_renaming != id) { e.Handled = true; BeginRename(id); } };
            button.ContextFlyout = TabMenu(id);
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; EndRename(commit: true); }
                else if (e.Key == Key.Escape) { e.Handled = true; EndRename(commit: false); }
            };
            editor.LostFocus += (_, _) => { if (_renaming == id) EndRename(commit: true); };
        }
        button.PointerReleased += (_, e) =>
        {
            if (e.Handled || _renaming == id) return;
            if (e.InitialPressMouseButton == MouseButton.Left) ActivateTab(id);
            else if (e.InitialPressMouseButton == MouseButton.Middle && !tab.IsFocus) CloseTab(id);   // as a browser tab
        };
        return new TabView(button, dot, halo, name, editor, countPill, count);
    }

    // A tab's right-click menu. "Edit layout…" joins it with the painter (roost-tabs T6).
    private MenuFlyout TabMenu(string id)
    {
        var rename = new MenuItem { Header = "Rename", InputGesture = new KeyGesture(Key.F2) };
        rename.Click += (_, _) => BeginRename(id);
        var edit = new MenuItem { Header = "Edit layout…" };
        edit.Click += (_, _) => EditLayout(id);
        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.Click += (_, _) => { if (_tabs.DuplicateTab(id) is { } copy) ActivateTab(copy.Id); };
        var close = new MenuItem { Header = "Close tab" };
        close.Click += (_, _) => CloseTab(id);
        var menu = new MenuFlyout { Items = { rename, edit, duplicate, new Separator(), close } };
        // Duplicate needs room for one more tab.
        menu.Opening += (_, _) => duplicate.IsEnabled = _tabs.Tabs.Count < RoostTabSet.MaxTabs;
        return menu;
    }

    private void UpdateTabButton(TabView v, RoostTab tab)
    {
        bool on = tab.Id == _tabs.ActiveId;
        var light = RoostTabStatus.For(tab, _roster.Panes);
        var brush = _p.Light(light.Light);
        v.Button.Background = on ? _p.Ground : v.Button.IsPointerOver ? _p.Raised2 : Brushes.Transparent;
        v.Button.BorderBrush = on ? _p.Border : Brushes.Transparent;
        v.Name.Foreground = on ? _p.Title : _p.Muted;
        ((Control)v.Dot.Parent!).IsVisible = light.Light != RoostLight.None;
        v.Dot.Fill = brush;
        v.Halo.Fill = brush;
        v.CountPill.IsVisible = light.Count > 0;
        v.Count.Text = light.Count.ToString();
        v.Count.Foreground = brush;
        v.CountPill.BorderBrush = brush;
        // Needs-you in a tab you're not looking at breathes, like a pane's ring; the active tab's panes ring themselves.
        if (!on && light.Light >= RoostLight.Awaiting) _pulsingTabs.Add(tab.Id);
        else v.Halo.Opacity = 0;
    }

    // ── Tab rename / close ────────────────────────────────────────────────────

    /// <summary>Edits a tab's name in place (Focus can't be renamed): Enter or a click away keeps it, Esc
    /// cancels.</summary>
    private void BeginRename(string id)
    {
        if (_renaming is not null) EndRename(commit: true);
        if (id == RoostTabSet.FocusId || _tabs.Find(id) is not { } tab || !_tabViews.TryGetValue(id, out var v)) return;
        _renaming = id;
        v.Editor.Text = tab.Name;
        v.Name.IsVisible = false;
        v.Editor.IsVisible = true;
        v.Editor.Focus();
        v.Editor.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (_renaming is not { } id) return;
        _renaming = null;
        if (!_tabViews.TryGetValue(id, out var v)) return;
        bool renamed = commit && _tabs.RenameTab(id, v.Editor.Text ?? "");
        v.Editor.IsVisible = false;
        v.Name.IsVisible = true;
        if (renamed) Refresh();   // the strip rebuilds with the new name (and tooltip)
    }

    private void CloseTab(string id)
    {
        if (_renaming == id) EndRename(commit: false);
        if (_tabs.CloseTab(id)) Refresh();
    }

    // ── Layout painter ────────────────────────────────────────────────────────

    /// <summary>Opens the painter on a tab (its menu's "Edit layout…"): it covers the stage, at the stage's shape,
    /// until Done applies the new layout (sessions follow their regions; ones whose region went go back to the rail)
    /// or Cancel / Esc puts the old one back. Switching tabs cancels it.</summary>
    private void EditLayout(string id)
    {
        if (id == RoostTabSet.FocusId || _tabs.Find(id) is not { } tab) return;
        ClosePainter(null, refresh: false);
        ActivateTab(id);
        double aspect = _stage.Bounds.Height > 0 ? _stage.Bounds.Width / _stage.Bounds.Height : 1.6;
        var painter = new RoostLayoutPainter(_p, tab, _layouts,
            region => tab.At(region) is { } key ? _roster.Find(key)?.Session.DisplayName ?? key : null, aspect);
        painter.Finished += layout => ClosePainter(layout, refresh: true);
        _painter = painter;
        _grid.IsVisible = false;
        _stage.Children.Add(painter);
        // Focus out of whatever pane had it — a hidden composer must not take the Enter meant for Done.
        painter.Focusable = true;
        painter.Focus();
    }

    // Takes the painter down, applying <paramref name="layout"/> to its tab (null = cancelled).
    private void ClosePainter(RoostGridLayout? layout, bool refresh)
    {
        if (_painter is not { } painter) return;
        _painter = null;
        _stage.Children.Remove(painter);
        _grid.IsVisible = true;
        if (layout is not null) _tabs.ApplyLayout(painter.TabId, layout);
        if (refresh) Refresh();
    }

    /// <summary>HeadlessRenderer hook: open the painter on a tab.</summary>
    internal RoostLayoutPainter? EditLayoutForRender(string id)
    {
        EditLayout(id);
        return _painter;
    }

    private Border NewTabButton()
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), Margin = new Thickness(2, 0, 0, 4),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = $"New tab  ·  {RoostKeys.ChordFor(RoostCommand.NewTab)}",
            Child = new TextBlock { Text = "+", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = _p.Muted },
        };
        return b.HoverWash(_p.Raised2).OnLeftClick(AddTab);
    }

    // A new tab opens with its name ready to type over ("Tab 3"; Esc keeps it).
    private void AddTab()
    {
        if (_tabs.AddTab(layout: RoostGridLayout.FromTemplate(RoostSnapTemplate.Columns2)) is not { } tab) return;
        ActivateTab(tab.Id);
        BeginRename(tab.Id);
    }
}
