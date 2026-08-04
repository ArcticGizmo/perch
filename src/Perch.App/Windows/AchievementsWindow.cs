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
/// The "trophy cabinet" window: the whole lifetime achievement set, opened from the tray. Always all-time
/// (badges are lifetime), so there's no scope toolbar — it scans every transcript once on open, evaluates
/// the catalogue off the UI thread (the CLAUDE.md load pattern), and hands the badges to the owner-drawn
/// <see cref="AchievementsDashboard"/>. A search box at the top filters the wall live. Wide enough to lay
/// the tiles three across by default. Escape clears the search (then closes); created lazily and reused via
/// <c>WindowHost</c>.
/// </summary>
internal sealed class AchievementsWindow : Window
{
    private static readonly IBrush BodyBg   = new SolidColorBrush(Color.FromRgb(18, 18, 24));
    private static readonly IBrush SearchBg = new SolidColorBrush(Color.FromRgb(24, 24, 34));

    private readonly AchievementsDashboard _dashboard = new();
    private readonly TextBox _search;
    private readonly bool _showCost;

#if DEBUG
    /// <summary>Debug-only hook (set by the app): invoked with a clicked badge so its unlock reveal can be
    /// played on demand, without having to actually earn it. Wired up only in debug builds.</summary>
    public Action<AchievementUnlock>? PreviewReveal;
#endif

    public AchievementsWindow(AppSettings settings)
    {
        Title = "Achievements";
        Width = 840;   // three 200px tiles across, with room to spare
        Height = 760;
        MinWidth = 460;
        MinHeight = 420;
        Background = BodyBg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _showCost = settings.ShowEstimatedCost;

        _search = new TextBox
        {
            PlaceholderText = "Search achievements…",
            Background = SearchBg, Foreground = Palette.FgBrush,
            BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), FontSize = 14, Padding = new Thickness(10, 8),
            Margin = new Thickness(22, 18, 22, 4),
        };
        _search.TextChanged += (_, _) => _dashboard.SetFilter(_search.Text ?? "");

        var scroll = new ScrollViewer
        {
            Content = _dashboard,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(_search);
        dock.Children.Add(scroll);
        Content = dock;

#if DEBUG
        _dashboard.BadgeActivated += a =>
        {
            string detail = a.Category.Length > 0 ? $"{a.Category} · Lvl {Math.Max(1, a.Level)}" : "";
            // The rung actually reached (or the first, as the goal, when none are) supplies the criteria line.
            string criteria = a.Levels.Count > 0 ? a.Levels[Math.Max(0, a.Level - 1)].Criteria : a.Description;
            PreviewReveal?.Invoke(new AchievementUnlock(a.Name, a.Emoji, detail, criteria, a.Tier));
        };
#endif
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape backs out one step at a time: clear an active search first, then close.
        if (e.Key == Key.Escape)
        {
            if (_search.Text is { Length: > 0 }) _search.Text = "";
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // Computes the all-time report off the UI thread, evaluates the badges, then repaints. Guarded against
    // the window closing mid-scan (the CLAUDE.md off-thread idiom).
    private void Refresh()
    {
        _dashboard.SetLoading();

        bool showCost = _showCost;
        var today = DateOnly.FromDateTime(DateTime.Now);
        System.Threading.Tasks.Task.Run(() =>
        {
            var range = SessionStatsService.ReportAllTime(today);
            var badges = AchievementCatalog.Evaluate(range.Totals, range, showCost);
            return (badges, range.FirstActiveDay);
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible) return;
                var (badges, firstDay) = t.Result;
                string subtitle = firstDay is { } f ? $"your lifetime trophies · since {f:MMM yyyy}" : "your lifetime trophies";
#if DEBUG
                subtitle += "  ·  (debug) click a badge to preview its reveal";
#endif
                _dashboard.SetBadges(badges, subtitle);
            });
        });
    }
}
