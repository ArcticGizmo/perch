using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Perch.Avalonia.Views;
using Perch.Data;

namespace Perch.Avalonia.Windows;

/// <summary>
/// The "trophy cabinet" window: the whole lifetime achievement set, opened from the tray. Always all-time
/// (badges are lifetime), so there's no scope toolbar — it scans every transcript once on open, evaluates
/// the catalogue off the UI thread (the CLAUDE.md load pattern), and hands the badges to the owner-drawn
/// <see cref="AchievementsDashboard"/>. Escape closes; created lazily and reused via <c>WindowHost</c>.
/// </summary>
internal sealed class AchievementsWindow : Window
{
    private readonly AchievementsDashboard _dashboard = new();
    private readonly bool _showCost;

#if DEBUG
    /// <summary>Debug-only hook (set by the app): invoked with a clicked badge so its unlock reveal can be
    /// played on demand, without having to actually earn it. Wired up only in debug builds.</summary>
    public Action<AchievementUnlock>? PreviewReveal;
#endif

    public AchievementsWindow(AppSettings settings)
    {
        Title = "Achievements";
        Width = 640;
        Height = 720;
        MinWidth = 460;
        MinHeight = 420;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 24));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _showCost = settings.ShowEstimatedCost;
        Content = new ScrollViewer { Content = _dashboard, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

#if DEBUG
        _dashboard.BadgeActivated += a =>
        {
            string detail = a.Category.Length > 0 ? $"{a.Category} · Lvl {Math.Max(1, a.Level)}" : a.Description;
            PreviewReveal?.Invoke(new AchievementUnlock(a.Name, a.Emoji, detail, a.Tier));
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
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
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
