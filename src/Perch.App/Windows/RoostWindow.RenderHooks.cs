using Avalonia;
using Avalonia.Controls;
using Perch.Avalonia.Views;

namespace Perch.Avalonia.Windows;

// HeadlessRenderer hooks: drive the Roost into the states its render shots capture.
internal sealed partial class RoostWindow
{
    /// <summary>HeadlessRenderer hook: "+ New session" clicked.</summary>
    internal void StartNewSessionForRender() => StartNewSession(null);

    /// <summary>HeadlessRenderer hook: a drag of pane <paramref name="key"/> hovering the active tab's region at
    /// <paramref name="slot"/> (the ghost at its centre). <see cref="DropForRender"/> lets go.</summary>
    internal void DragForRender(string key, int slot)
    {
        _press = new Press(key, false, default, this);
        StartGhost(_press.Value);
        MoveDragForRender(slot);
    }

    /// <summary>HeadlessRenderer hook: a drag of pane <paramref name="key"/> (or, with <paramref name="key"/> null, of
    /// tab <paramref name="tabId"/>) hovering tab <paramref name="ontoId"/>'s header.</summary>
    internal void DragOntoTabForRender(string? key, string? tabId, string ontoId)
    {
        _press = new Press(key ?? tabId!, key is null, default, this);
        StartGhost(_press.Value);
        var b = _tabViews[ontoId].Button;
        if (b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), _overlay) is { } at) MoveGhost(at);
    }

    /// <summary>HeadlessRenderer hook: the hover delay running out on the tab the drag is over.</summary>
    internal void HoverSwitchForRender() => HoverSwitch();

    /// <summary>HeadlessRenderer hook: the drag under way moving over the active tab's region at
    /// <paramref name="slot"/>.</summary>
    internal void MoveDragForRender(int slot)
    {
        if (slot < 0 || slot >= _cells.Length || !_cells[slot].Host.IsVisible) return;
        var host = _cells[slot].Host;
        if (host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), _overlay) is { } at) MoveGhost(at);
    }

    internal void DropForRender() => EndDrag(drop: true);

    /// <summary>HeadlessRenderer hook: tab <paramref name="id"/>'s name being edited, with <paramref name="text"/>
    /// typed so far.</summary>
    internal void RenameForRender(string id, string text)
    {
        BeginRename(id);
        if (_tabViews.TryGetValue(id, out var v)) { v.Editor.Text = text; v.Editor.CaretIndex = text.Length; }
    }

    internal void EndRenameForRender(bool commit) => EndRename(commit);

    /// <summary>HeadlessRenderer hook: tab <paramref name="id"/>'s right-click menu.</summary>
    internal void OpenTabMenuForRender(string id)
    {
        if (_tabViews.TryGetValue(id, out var v) && v.Button.ContextFlyout is { } menu) { _openFlyout = menu; menu.ShowAt(v.Button); }
    }

    /// <summary>HeadlessRenderer hook: close a pane as its menu would.</summary>
    internal void ClosePaneForRender(string key) => OnPaneAction(key, RoostPaneAction.Close);

    /// <summary>HeadlessRenderer hook: zoom a pane as a header double-click would.</summary>
    internal void ZoomForRender(string key) => OnPaneAction(key, RoostPaneAction.Zoom);

    /// <summary>HeadlessRenderer hook: open the empty-region picker on slot <paramref name="slot"/>.</summary>
    internal void OpenPickerForRender(int slot) => ShowPicker(slot);

    /// <summary>HeadlessRenderer hook: open the keys cheat-sheet.</summary>
    internal void OpenKeysForRender() => ShowKeys(_hiddenRow.Parent as Control ?? this);

    /// <summary>HeadlessRenderer hook: open the rail footer's Recent flyout on <paramref name="filter"/>.</summary>
    internal void OpenRecentForRender(RecentFilter filter) { _recentFilter = filter; ShowRecentFlyout(); }

    /// <summary>HeadlessRenderer hook: dismiss the picker / cheat-sheet / Recent flyout.</summary>
    internal void CloseFlyoutForRender() { _openFlyout?.Hide(); _openFlyout = null; }
}
