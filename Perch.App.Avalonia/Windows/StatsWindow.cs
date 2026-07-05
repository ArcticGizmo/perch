using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Theming;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The session-stats dashboard window (the Avalonia port of <c>StatsForm</c>). A scope toolbar
/// (Today / 7 days / 30 days / All time) across the top selects the range; the owner-drawn
/// <see cref="StatsDashboard"/> below it renders the figures, scrolled. Reports come from
/// <see cref="SessionStatsService"/> and are computed off the UI thread (the CLAUDE.md load pattern).
/// </summary>
internal sealed class StatsWindow : Window
{
    private enum Scope { Today, Week, Month, AllTime }

    private readonly StatsDashboard _dashboard;
    private readonly Dictionary<Scope, Button> _scopeButtons = new();
    private Scope _scope = Scope.Today;

    public StatsWindow(AppSettings settings)
    {
        Title = "Session stats";
        Width = 620;
        Height = 720;
        MinWidth = 560;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _dashboard = new StatsDashboard(settings.ShowEstimatedCost);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(12, 9), Height = 46,
        };
        AddScopeButton(toolbar, "Today", Scope.Today);
        AddScopeButton(toolbar, "7 days", Scope.Week);
        AddScopeButton(toolbar, "30 days", Scope.Month);
        AddScopeButton(toolbar, "All time", Scope.AllTime);

        var toolbarPanel = new Border
        {
            Background = Palette.FormBgBrush, Child = toolbar,
            [DockPanel.DockProperty] = Dock.Top,
        };

        var scroll = new ScrollViewer
        {
            Content = _dashboard,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Content = new DockPanel { Children = { toolbarPanel, scroll } };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshStats();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private void AddScopeButton(Panel toolbar, string text, Scope scope)
    {
        var b = new Button
        {
            Content = text, Width = 78, Height = 28,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12, CornerRadius = new CornerRadius(6),
        };
        b.Click += (_, _) => { if (_scope != scope) { _scope = scope; RefreshStats(); } };
        _scopeButtons[scope] = b;
        toolbar.Children.Add(b);
    }

    private void UpdateScopeButtons()
    {
        foreach (var (scope, b) in _scopeButtons)
        {
            bool active = scope == _scope;
            b.Background = active ? new SolidColorBrush(Palette.Accent) : new SolidColorBrush(Palette.ButtonBg);
            b.Foreground = active ? Brushes.White : Palette.FgBrush;
        }
    }

    // Recomputes the current scope's report off the UI thread, then repaints. Safe to call repeatedly.
    private void RefreshStats()
    {
        UpdateScopeButtons();
        _dashboard.SetLoading();

        var today = DateOnly.FromDateTime(DateTime.Now);
        var scope = _scope;
        System.Threading.Tasks.Task.Run(() => LoadScope(scope, today)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                _dashboard.SetReport(t.Result.report, t.Result.range);
            });
        });
    }

    private static (StatsReport report, RangeReport? range) LoadScope(Scope scope, DateOnly today) => scope switch
    {
        Scope.Week => Range(SessionStatsService.ReportForRange(today.AddDays(-6), today, "Last 7 days")),
        Scope.Month => Range(SessionStatsService.ReportForRange(today.AddDays(-29), today, "Last 30 days")),
        Scope.AllTime => Range(SessionStatsService.ReportAllTime(today)),
        _ => (SessionStatsService.ReportForDay(today), null),
    };

    private static (StatsReport, RangeReport?) Range(RangeReport r) => (r.Totals, r);
}
