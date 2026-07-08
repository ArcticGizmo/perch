using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private readonly GradientButton _wrappedButton;
    private readonly bool _showCost;
    private Scope _scope = Scope.Today;

    private StatsReport? _report;
    private RangeReport? _range;
    private WrappedWindow? _wrapped;

    public StatsWindow(AppSettings settings)
    {
        Title = "Session stats";
        Width = 620;
        Height = 720;
        MinWidth = 560;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _showCost = settings.ShowEstimatedCost;
        _dashboard = new StatsDashboard(_showCost);

        var scopes = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AddScopeButton(scopes, "Today", Scope.Today);
        AddScopeButton(scopes, "7 days", Scope.Week);
        AddScopeButton(scopes, "30 days", Scope.Month);
        AddScopeButton(scopes, "All time", Scope.AllTime);

        // The fun bit: generates a shareable "Wrapped" poster from the current scope. Sits at the far
        // right of the toolbar, wearing the poster's own gradient so it draws the eye; enabled only once
        // a report with sessions has loaded.
        _wrappedButton = new GradientButton("✨", "Wrapped")
        {
            Width = 108, Enabled = false, VerticalAlignment = VerticalAlignment.Center,
            [DockPanel.DockProperty] = Dock.Right,
        };
        _wrappedButton.Click += OpenWrapped;

        var toolbar = new DockPanel
        {
            Margin = new Thickness(12, 9), Height = 46, LastChildFill = true,
            Children = { _wrappedButton, scopes },
        };

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
        _wrappedButton.Enabled = false;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var scope = _scope;
        System.Threading.Tasks.Task.Run(() => LoadScope(scope, today)).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                _report = t.Result.report;
                _range = t.Result.range;
                _dashboard.SetReport(t.Result.report, t.Result.range);
                _wrappedButton.Enabled = _report.SessionCount > 0;
            });
        });
    }

    // Builds a Wrapped poster from the current scope's report and shows it in a reveal card. Guarded so
    // an accidental click while loading (or with no sessions) is a no-op; only one card is shown at once.
    private void OpenWrapped()
    {
        if (_report is not { SessionCount: > 0 } report) return;
        var summary = WrappedSummary.Build(report, _range, ScopeTitle(), StatsDashboard.SubtitleFor(_range), _showCost);
        _wrapped?.Close();
        _wrapped = new WrappedWindow(summary, AppIcon(), SuggestedFileName());
        _wrapped.Closed += (_, _) => _wrapped = null;
        _wrapped.Show();
        _wrapped.Activate();
    }

    private string ScopeTitle() => _scope switch
    {
        Scope.Week => "This Week",
        Scope.Month => "This Month",
        Scope.AllTime => "All Time",
        _ => "Today",
    };

    private string SuggestedFileName()
    {
        string scope = _scope switch
        {
            Scope.Week => "week",
            Scope.Month => "month",
            Scope.AllTime => "all-time",
            _ => "today",
        };
        return $"perch-wrapped-{scope}-{DateTime.Now:yyyy-MM-dd}";
    }

    // The bundled brand bird, drawn into the poster's header and footer. Loaded lazily and cached; best
    // effort — a missing asset just yields a poster without the icon.
    private static Bitmap? _appIcon;
    private static bool _appIconTried;
    private static Bitmap? AppIcon()
    {
        if (_appIconTried) return _appIcon;
        _appIconTried = true;
        try { _appIcon = new Bitmap(AssetLoader.Open(new Uri("avares://perch/Assets/icon.png"))); }
        catch { _appIcon = null; }
        return _appIcon;
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
