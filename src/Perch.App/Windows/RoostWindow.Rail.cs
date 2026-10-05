using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Roost;
using PlacementMode = global::Avalonia.Controls.PlacementMode;

namespace Perch.Avalonia.Windows;

// The Roost's rail (sessions by status or A-Z, each tagged with its tab), its footer ("N hidden", the keys
// cheat-sheet) and the bottom bar's "not in a tab" / "other tabs" pills.
internal sealed partial class RoostWindow
{
    // ── Pills, hidden panes, keys ─────────────────────────────────────────────

    private (Border, TextBlock, Ellipse) Pill()
    {
        var dot = new Ellipse { Width = 7, Height = 7, Fill = _p.Await, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            CornerRadius = SessionPalette.PillRadius,
            Padding = new Thickness(12, 3), BorderThickness = new Thickness(1), BorderBrush = _p.Border,
            Background = _p.Surface, Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } },
        };
        return (pill, text, dot);
    }

    // "N not in a tab · 1 needs you", and "K need you in other tabs" when another tab is calling.
    private void RefreshPills(IReadOnlyList<RoostPane> all)
    {
        var off = _tabs.Unplaced(all).Where(p => p.IsLive).ToList();
        int needs = off.Count(p => p.Group == RoostGroup.NeedsYou);
        _unplacedPill.IsVisible = off.Count > 0;
        _unplacedText.Text = needs > 0 ? $"{off.Count} not in a tab · {needs} need{(needs == 1 ? "s" : "")} you" : $"{off.Count} not in a tab";
        _unplacedDot.IsVisible = needs > 0;
        _unplacedPill.BorderBrush = needs > 0 ? _p.Await : _p.Border;
        _unplacedText.Foreground = needs > 0 ? _p.Await : _p.Muted;

        var elsewhere = _tabs.Elsewhere(all);
        _elsewherePill.IsVisible = elsewhere.Count > 0;
        _elsewhereText.Text = $"{elsewhere.Count} need{(elsewhere.Count == 1 ? "s" : "")} you in other tabs";
        var brush = _p.Light(elsewhere.Light);
        _elsewhereDot.IsVisible = true;
        _elsewhereDot.Fill = brush;
        _elsewherePill.BorderBrush = brush;
        _elsewhereText.Foreground = brush;
    }

    private (Border Row, TextBlock Text) RailFooterRow(string text, string tip)
    {
        var label = new TextBlock { Text = text, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5), Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = tip, Child = label,
        };
        row.PointerEntered += (_, _) => { row.Background = _p.Raised2; label.Foreground = _p.Text; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; label.Foreground = _p.Muted; };
        return (row, label);
    }

    private void RefreshHiddenRow()
    {
        int n = _roster.ClosedPanes.Count;
        _hiddenRow.IsVisible = n > 0;
        _hiddenText.Text = $"{n} hidden";
    }

    // "N hidden": closed panes whose sessions still run. A click lists them to reopen (one, or all).
    private void ShowHiddenMenu(Control anchor)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedBottom };
        foreach (var pane in _roster.ClosedPanes)
        {
            var item = new MenuItem { Header = $"Reopen {pane.Session.DisplayName}" };
            var key = pane.Key;
            item.Click += (_, _) => Reopen([key]);
            flyout.Items.Add(item);
        }
        if (_roster.ClosedPanes.Count > 1)
        {
            flyout.Items.Add(new Separator());
            var all = new MenuItem { Header = "Reopen all" };
            all.Click += (_, _) => Reopen(_roster.ClosedPanes.Select(p => p.Key).ToList());
            flyout.Items.Add(all);
        }
        flyout.ShowAt(anchor);
    }

    private void Reopen(IReadOnlyList<string> keys)
    {
        bool changed = false;
        foreach (var k in keys) changed |= _roster.Reopen(k);
        if (!changed) return;
        if (keys.Count == 1) FocusPane(keys[0]);
        else Refresh();
    }

    // The cheat-sheet: every RoostKeys row, by section.
    private void ShowKeys(Control anchor)
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 320 };
        foreach (var group in RoostKeys.All.GroupBy(r => r.Group))
        {
            var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4, ColumnSpacing = 14 };
            int r = 0;
            foreach (var key in group)
            {
                rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                rows.Children.Add(new TextBlock { Text = key.Chord, FontFamily = _p.Mono, FontSize = 11.5, Foreground = _p.Text, [Grid.RowProperty] = r });
                rows.Children.Add(new TextBlock
                {
                    Text = key.Description, FontFamily = _p.Body, FontSize = 12, Foreground = _p.Muted, TextWrapping = TextWrapping.Wrap,
                    [Grid.RowProperty] = r, [Grid.ColumnProperty] = 1,
                });
                r++;
            }
            body.Children.Add(new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = group.Key.ToString().ToUpperInvariant(), FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint },
                    rows,
                },
            });
        }
        body.Children.Add(new TextBlock
        {
            Text = "Drag a rail row or a pane header onto a region to place it, or onto a tab (hold it there to switch "
                + "to that tab) · double-click a header to zoom",
            FontFamily = _p.Body, FontSize = 11.5, Foreground = _p.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 340,
        });
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft, Content = body };
        _openFlyout = flyout;
        flyout.ShowAt(anchor);
    }


    // ── Rail ──────────────────────────────────────────────────────────────────

    // One rail row's controls, kept so the 1s clock and each scan update text and colour in place.
    private sealed record RailRowView(Border Row, Border Bar, Ellipse Dot, TextBlock Elapsed, Border NewTag, TextBlock Diamond, TextBlock Tag, TextBlock Name);

    private readonly Dictionary<string, RailRowView> _railRows = new(StringComparer.Ordinal);
    private string? _railSig;

    // The rail's controls are rebuilt only when its shape changes (a pane joins, leaves or changes group, or the
    // sort flips); every other pass, the clock's included, just re-labels the existing rows. Never mid-press:
    // rebuilding would drop the row being pressed or dragged (EndDrag refreshes once it lets go).
    private void RefreshRail()
    {
        var sections = RailSections();
        var sig = $"{(int)_railSort}#" + string.Join("|", sections
            .Select(s => $"{s.Title}:{string.Join(",", s.Panes.Select(p => p.Key))}"));
        if (sig != _railSig && PressedSession is null)
        {
            _railSig = sig;
            RebuildRail(sections);
        }
        foreach (var section in sections)
            foreach (var pane in section.Panes)
                if (_railRows.TryGetValue(pane.Key, out var row)) UpdateRailRow(row, pane);
    }

    // The rail's headed sections for the current sort: the non-empty status groups, or one A–Z list. A dormant pane no
    // tab holds is left to the footer's Recent flyout (InRail).
    private List<(string Title, IReadOnlyList<RoostPane> Panes)> RailSections()
    {
        if (_railSort == RoostRailSort.Alphabetical)
        {
            var all = _roster.RailAlphabetical.Where(InRail).ToList();
            return all.Count > 0 ? [("ALL SESSIONS", all)] : [];
        }
        return _roster.Rail
            .Select(g => (Title: GroupTitle(g.Group), Panes: (IReadOnlyList<RoostPane>)g.Panes.Where(InRail).ToList()))
            .Where(s => s.Panes.Count > 0)
            .ToList();
    }

    private void RebuildRail(IReadOnlyList<(string Title, IReadOnlyList<RoostPane> Panes)> sections)
    {
        _rail.Children.Clear();
        _railRows.Clear();
        if (sections.Count == 0)
        {
            _rail.Children.Add(new TextBlock { Text = "No live sessions", Margin = new Thickness(8, 0), FontSize = 12, Foreground = _p.Faint });
            return;
        }
        foreach (var (title, panes) in sections)
        {
            var col = new StackPanel { Spacing = 2 };
            col.Children.Add(new DockPanel
            {
                Margin = new Thickness(8, 0, 8, 4),
                Children =
                {
                    new TextBlock { Text = panes.Count.ToString(), FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Text = title, FontFamily = _p.Mono, FontSize = 10.5, LetterSpacing = 1.2, Foreground = _p.Faint },
                },
            });
            foreach (var pane in panes)
            {
                var row = RailRow(pane.Key);
                _railRows[pane.Key] = row;
                col.Children.Add(row.Row);
            }
            _rail.Children.Add(col);
        }
    }

    // The rail header's "Status | A–Z" toggle: a small segmented control, docked above the scrolling list.
    private Control RailSortToggle()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        foreach (var (sort, label, tip) in new[]
                 {
                     (RoostRailSort.Status, "Status", "Group sessions by what they need from you"),
                     (RoostRailSort.Alphabetical, "A–Z", "List sessions by name"),
                 })
        {
            var seg = new Border
            {
                Padding = new Thickness(8, 3), Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = _p.Border, BorderThickness = new Thickness(sort == RoostRailSort.Status ? 0 : 1, 0, 0, 0),
                [Grid.ColumnProperty] = (int)sort, [ToolTip.TipProperty] = tip,
                Child = new TextBlock
                {
                    Text = label, FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            seg.OnLeftClick(() => SetRailSort(sort));
            _sortSegments[sort] = seg;
            row.Children.Add(seg);
        }
        RefreshSortSegments();
        return new Border
        {
            Margin = new Thickness(16, 12, 16, 8), CornerRadius = new CornerRadius(8), BorderBrush = _p.Border,
            BorderThickness = new Thickness(1), Background = _p.Surface, ClipToBounds = true,
            [DockPanel.DockProperty] = Dock.Top, Child = row,
        };
    }

    private void RefreshSortSegments()
    {
        foreach (var (sort, seg) in _sortSegments)
        {
            bool on = sort == _railSort;
            seg.Background = on ? _p.BrandWash : Brushes.Transparent;
            ((TextBlock)seg.Child!).Foreground = on ? _p.Text : _p.Muted;
        }
    }

    private static string GroupTitle(RoostGroup g) => g switch
    {
        RoostGroup.NeedsYou => "NEEDS YOU",
        RoostGroup.DoneReview => "DONE · REVIEW",
        RoostGroup.Working => "WORKING",
        RoostGroup.Recent => "NOT RUNNING",   // only the tab-held ones; the rest are behind the footer's "Recent"
        _ => "QUIET",
    };

    // A rail row's controls; UpdateRailRow fills them from the pane.
    private RailRowView RailRow(string key)
    {
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 6, 1), [DockPanel.DockProperty] = Dock.Left };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var elapsed = new TextBlock { FontFamily = _p.Mono, FontSize = 11, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right };
        var tag = new TextBlock
        {
            FontFamily = _p.Mono, FontSize = 10, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0), MaxWidth = 56, TextTrimming = TextTrimming.CharacterEllipsis, [DockPanel.DockProperty] = Dock.Right,
        };
        var newTag = new Border
        {
            CornerRadius = SessionPalette.PillRadius, Padding = new Thickness(6, 0), Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(1), BorderBrush = _p.BrandLine,
            VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right,
            Child = new TextBlock { Text = "new", FontFamily = _p.Body, FontSize = 10, Foreground = _p.Brand },
        };
        var diamond = new TextBlock
        {
            Text = "◆", FontSize = 9, Foreground = _p.Brand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right,
        };
        var name = new TextBlock
        {
            FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(2, 6, 8, 6), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    bar,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, [DockPanel.DockProperty] = Dock.Left, Children = { dot } },
                    elapsed, tag, newTag, diamond, name,
                },
            },
        };
        // Focus moves without a rebuild, so the hover reads it at event time.
        row.PointerEntered += (_, _) => { if (key != _focused) row.Background = _p.Raised2; };
        row.PointerExited += (_, _) => row.Background = key == _focused ? _p.BrandWash : Brushes.Transparent;
        AttachDragSource(row, key, isTab: false);   // first, so a drop marks the release handled before the click sees it
        row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && !e.Handled) FocusPane(key); };
        return new RailRowView(row, bar, dot, elapsed, newTag, diamond, tag, name);
    }

    private void UpdateRailRow(RailRowView v, RoostPane pane)
    {
        var s = pane.Session;
        bool on = pane.Key == _focused;
        var tag = TabTagFor(pane.Key);
        bool inActive = _tabs.Active.Cells.Values.Contains(pane.Key);
        bool fresh = tag is null && pane.IsLive && !_everPlaced.Contains(pane.Key);
        v.Dot.Fill = _p.PaneDot(pane);
        var elapsed = pane.Dormant is { } d ? SessionPane.DormantRailLabel(d)
            : pane.Ended ? "ended"
            : s.Status == SessionStatus.AwaitingInput ? s.AwaitingElapsedLabel() ?? ""
            : s.Status == SessionStatus.Running ? s.RunningElapsedLabel() ?? ""
            : "";
        if (v.Elapsed.Text != elapsed) v.Elapsed.Text = elapsed;
        v.Bar.Background = inActive ? _p.Brand : Brushes.Transparent;
        // Its own tab is marked by the bar; another tab's name is the tag.
        v.Tag.Text = inActive ? "" : tag ?? "";
        v.Tag.IsVisible = !inActive && tag is not null;
        v.NewTag.IsVisible = fresh;
        v.Diamond.IsVisible = s.IsPerchControlled;
        v.Name.Text = s.DisplayName;
        v.Name.Foreground = _p.Text;
        // A flagged ending (interrupted, just before a shutdown) reads in the attention hue.
        v.Elapsed.Foreground = pane.Dormant is { IsFlagged: true } ? _p.Await : _p.Faint;
        v.Row.BorderBrush = on ? _p.BrandLine : Brushes.Transparent;
        if (on || !v.Row.IsPointerOver) v.Row.Background = on ? _p.BrandWash : Brushes.Transparent;
        v.Row.Opacity = pane.Ended ? 0.6 : pane.IsDormant ? 0.85 : 1;
        var where = inActive ? "In this tab" : tag is null
            ? "In no tab — click to look at it in Focus, or drag it onto a region"
            : $"In {tag} — click to go there, or drag it onto a region here";
        ToolTip.SetTip(v.Row, pane.Dormant is { } dormant
            ? $"{SessionPane.DormantPillText(dormant)} · not running — send a message in its pane to resume it\n{where}"
            : where);
    }
}
