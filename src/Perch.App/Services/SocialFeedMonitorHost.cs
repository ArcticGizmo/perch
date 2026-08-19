using Avalonia.Threading;
using Perch.Social;

namespace Perch.Avalonia.Services;

/// <summary>
/// Keeps the friends feed live. A <see cref="DispatcherTimer"/> polls on a fixed cadence (the firewall-friendly
/// fallback that always works), and — when the backend supports it — a Realtime subscription nudges an
/// immediate re-poll the instant a friend posts, so the strip updates without waiting for the next tick. The
/// poll stays the source of truth: it applies RLS, resolves author profiles, and is what fires the
/// "@x just posted" notification, so a missed or blocked socket only means "less instant", never "broken".
///
/// The sibling of <see cref="StatusMonitorHost"/>: the timer ticks on the UI thread, the fetch runs off it
/// (<c>GetFeedAsync</c> is awaited; a failure is swallowed so a blip just keeps the last feed), and the result
/// is applied back on the UI thread. Only runs while Social is enabled and you're signed in — the App calls
/// <see cref="SetActive"/> from the auth-change and settings paths. Stopping clears the strip and unsubscribes.
/// </summary>
internal sealed class SocialFeedMonitorHost : IDisposable
{
    private readonly ISocialClient _social;
    private readonly Action<FeedSnapshot?> _onFeed;
    private readonly Action<FeedItem> _onNewFriendPost;
    private readonly DispatcherTimer _timer;

    // Post ids already surfaced, so a re-poll only notifies for genuinely new posts. Primed on the first poll
    // after activation (the backlog is baseline, not news), then diffed on every poll thereafter.
    private readonly HashSet<Guid> _seen = new();
    private bool _primed;

    private IDisposable? _realtime;

    /// <param name="onNewFriendPost">Invoked (on the UI thread) once per newly seen post authored by someone
    /// other than you — the hook for the "@x just posted" notification. Never fires for your own posts or for
    /// the backlog present when polling starts.</param>
    public SocialFeedMonitorHost(ISocialClient social, Action<FeedSnapshot?> onFeed, Action<FeedItem> onNewFriendPost)
    {
        _social = social;
        _onFeed = onFeed;
        _onNewFriendPost = onNewFriendPost;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => _ = Poll();
    }

    /// <summary>Start/stop the feed (call on the UI thread). Starting kicks an immediate poll and opens the
    /// realtime subscription; stopping clears the strip, closes the socket, and forgets what's been seen so a
    /// later restart re-primes rather than replaying the backlog as "new".</summary>
    public void SetActive(bool active)
    {
        if (active == _timer.IsEnabled) return;
        if (active)
        {
            _timer.Start();
            _ = Poll();
            // A live insert only nudges a re-poll — the poll (RLS-correct, profile-resolved) does the real
            // work. The callback arrives off the UI thread, so marshal back before touching the timer.
            _realtime = _social.SubscribeFeed(_ => Dispatcher.UIThread.Post(RefreshSoon));
        }
        else
        {
            _timer.Stop();
            _realtime?.Dispose();
            _realtime = null;
            _seen.Clear();
            _primed = false;
            _onFeed(null);
        }
    }

    /// <summary>Refresh now if active — e.g. right after posting a status, or on a realtime nudge, so it
    /// appears without waiting for the next tick.</summary>
    public void RefreshSoon() { if (_timer.IsEnabled) _ = Poll(); }

    // Ticks on the UI thread; GetFeedAsync runs its IO off it and the continuation resumes here, so the
    // callback (and the repaint it drives) stays on the UI thread.
    private async Task Poll()
    {
        try
        {
            var items = await _social.GetFeedAsync(50);
            _onFeed(new FeedSnapshot(items));
            NotifyNewFriendPosts(items);
        }
        catch { /* best-effort: a failed poll just keeps the last feed on screen */ }
    }

    // Fires a notification for each newly seen post by someone other than me. The first poll after activation
    // only primes the seen-set (the backlog isn't news); overlapping polls are naturally idempotent because a
    // post is added to _seen the first time it's noticed.
    private void NotifyNewFriendPosts(IReadOnlyList<FeedItem> items)
    {
        var meId = _social.Current.Me?.Id;
        var wasPrimed = _primed;
        foreach (var item in items)
        {
            if (!_seen.Add(item.Id)) continue;          // already surfaced
            if (wasPrimed && item.Author.Id != meId)
                _onNewFriendPost(item);
        }
        _primed = true;
    }

    public void Dispose()
    {
        _timer.Stop();
        _realtime?.Dispose();
        _realtime = null;
    }
}
