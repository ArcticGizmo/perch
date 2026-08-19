using Avalonia.Threading;
using Perch.Social;

namespace Perch.Avalonia.Services;

/// <summary>
/// Polls the friends feed on a fixed cadence and pumps each snapshot to a callback (the overlay canvas's
/// <c>UpdateFeed</c>), so the feed strip populates with real posts. The sibling of <see cref="StatusMonitorHost"/>:
/// a <see cref="DispatcherTimer"/> ticks on the UI thread, the fetch runs off it (<c>GetFeedAsync</c> is
/// awaited; a failure is swallowed so a blip just keeps the last feed), and the result is applied back on the
/// UI thread. Realtime (M5) will supersede the poll; the poll remains the firewall-friendly fallback.
///
/// Only runs while Social is enabled and you're signed in — the App calls <see cref="SetActive"/> from the
/// auth-change and settings paths. Stopping clears the strip.
/// </summary>
internal sealed class SocialFeedMonitorHost : IDisposable
{
    private readonly ISocialClient _social;
    private readonly Action<FeedSnapshot?> _onFeed;
    private readonly DispatcherTimer _timer;

    public SocialFeedMonitorHost(ISocialClient social, Action<FeedSnapshot?> onFeed)
    {
        _social = social;
        _onFeed = onFeed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Start/stop polling (call on the UI thread). Starting kicks an immediate poll; stopping clears
    /// the feed strip.</summary>
    public void SetActive(bool active)
    {
        if (active == _timer.IsEnabled) return;
        if (active) { _timer.Start(); _ = Poll(); }
        else { _timer.Stop(); _onFeed(null); }
    }

    /// <summary>Refresh now if active — e.g. right after posting a status, so it appears without waiting for
    /// the next tick.</summary>
    public void RefreshSoon() { if (_timer.IsEnabled) _ = Poll(); }

    // Ticks on the UI thread; GetFeedAsync runs its IO off it and the continuation resumes here, so the
    // callback (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        try
        {
            var items = await _social.GetFeedAsync(50);
            _onFeed(new FeedSnapshot(items));
        }
        catch { /* best-effort: a failed poll just keeps the last feed on screen */ }
    }

    public void Dispose() => _timer.Stop();
}
