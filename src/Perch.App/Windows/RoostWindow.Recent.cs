using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Perch.Avalonia.Views;
using Perch.Data;
using Perch.Data.Roost;
using PlacementMode = global::Avalonia.Controls.PlacementMode;

namespace Perch.Avalonia.Windows;

// The rail footer's "Recent" row (docs/session-recovery-plan.md): the dormant panes — sessions with no process that can be
// picked back up — behind a filterable flyout (RecentListView, the overlay's Recent button's list), so a long Recent
// group doesn't push the live sessions off the rail. Only a dormant pane a tab holds stays in the rail itself (it's part
// of the layout, and the rail is how you get around that); every dormant pane is in the flyout.
internal sealed partial class RoostWindow
{
    private Border _recentRow = null!;
    private TextBlock _recentCount = null!;
    private Ellipse _recentDot = null!;
    private RecentListView? _recentView;   // the open flyout's list, which follows each refresh while it's up
    private RecentFilter _recentFilter = RecentFilter.All;
    private readonly RecentSeen _recentSeen = new();

    // "◷ Recent", its count on the right, and a dot while there's a badged line not yet seen.
    private Border RecentFooterRow()
    {
        var label = new TextBlock { Text = "◷  Recent", FontFamily = _p.Body, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = _p.Muted };
        _recentDot = new Ellipse { Width = 7, Height = 7, Fill = _p.Await, IsVisible = false, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _recentCount = new TextBlock { FontFamily = _p.Mono, FontSize = 10.5, Foreground = _p.Faint, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right };
        var row = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5), Cursor = new Cursor(StandardCursorType.Hand), IsVisible = false,
            [ToolTip.TipProperty] = "Sessions that ended lately — click to pick one back up",
            Child = new DockPanel
            {
                Children =
                {
                    _recentCount,
                    new StackPanel { Orientation = Orientation.Horizontal, Children = { label, _recentDot } },
                },
            },
        };
        row.PointerEntered += (_, _) => { row.Background = _p.Raised2; label.Foreground = _p.Text; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; label.Foreground = _p.Muted; };
        row.OnLeftClick(ShowRecentFlyout);
        _recentRow = row;
        return row;
    }

    // Every dormant pane, in the Recent group's order (the app's: what Perch had open first, then the rest).
    private IReadOnlyList<RoostPane> DormantPanes() =>
        _roster.Rail.FirstOrDefault(g => g.Group == RoostGroup.Recent)?.Panes ?? [];

    // A pane stays in the rail unless it's dormant and no tab (Focus included) holds it — then it's only in the flyout.
    private bool InRail(RoostPane pane) => !pane.IsDormant || _tabs.IsPlaced(pane.Key);

    private void RefreshRecentRow()
    {
        var lines = RecentLines();
        _recentRow.IsVisible = lines.Count > 0;
        _recentCount.Text = lines.Count.ToString();
        _recentDot.IsVisible = _recentSeen.HasUnseen(lines);
        _recentView?.SetLines(lines);
    }

    private void ShowRecentFlyout()
    {
        var lines = RecentLines();
        var view = new RecentListView(_recentFilter, RecentListLook.For(_p), showAll: false);
        view.SetLines(lines);
        view.FilterChanged += f => _recentFilter = f;
        view.ResumeRequested += (id, _) => { _openFlyout?.Hide(); FocusPane(RoostToken.DormantKey(id)); };
        view.TerminalRequested += (id, _) =>
        {
            _openFlyout?.Hide();
            if (_roster.Find(RoostToken.DormantKey(id)) is { IsDormant: true } pane) ResumeInTerminalRequested?.Invoke(pane);
        };
        // Stays open: the app's refold drops the line, and the refresh hands the list its new lines.
        view.DismissRequested += id =>
        {
            if (_roster.Find(RoostToken.DormantKey(id)) is { IsDormant: true } pane) DismissRequested?.Invoke(pane);
        };

        var flyout = new Flyout { Placement = PlacementMode.RightEdgeAlignedBottom, Content = view };
        flyout.Closed += (_, _) => { if (ReferenceEquals(_recentView, view)) _recentView = null; };
        _recentView = view;
        _openFlyout = flyout;
        _recentSeen.MarkSeen(lines);
        _recentDot.IsVisible = false;
        flyout.ShowAt(_recentRow);
    }

    private List<RecentLine> RecentLines()
    {
        var now = Clock.Now;
        return DormantPanes().Select(p => RecentLine.From(p.Dormant!, now)).ToList();
    }
}
