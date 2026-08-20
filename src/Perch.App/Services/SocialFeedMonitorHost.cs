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
    private readonly Action<RosterSnapshot?> _onRoster;
    private readonly Action<FeedItem> _onNewFriendPost;
    private readonly Action<string> _onReactionToMyPost;
    private readonly DispatcherTimer _timer;

    // Post ids already surfaced, so a re-poll only notifies for genuinely new posts. Primed on the first poll
    // after activation (the backlog is baseline, not news), then diffed on every poll thereafter.
    private readonly HashSet<Guid> _seen = new();
    private bool _primed;

    // Baseline reaction counts on your OWN latest post, keyed by the post id they belong to, so we can fire
    // once per genuinely-new reaction (someone else reacting to you). Re-baselined when your latest post
    // changes (a new status resets the count), which also primes the first observation so existing reactions
    // don't replay.
    private Guid? _myReactionPostId;
    private readonly Dictionary<string, int> _myReactionCounts = new();

    private IDisposable? _realtime;

    /// <param name="onNewFriendPost">Invoked (on the UI thread) once per newly seen post authored by someone
    /// other than you — the hook for the "@x just posted" notification. Never fires for your own posts or for
    /// the backlog present when polling starts.</param>
    /// <param name="onReactionToMyPost">Invoked (on the UI thread) once per newly-seen reaction on your own
    /// latest status, with the emoji — the hook for the "big reactions" bubbles. Never fires for reactions
    /// already present when polling starts, nor when you post a new status.</param>
    public SocialFeedMonitorHost(ISocialClient social, Action<RosterSnapshot?> onRoster,
        Action<FeedItem> onNewFriendPost, Action<string> onReactionToMyPost)
    {
        _social = social;
        _onRoster = onRoster;
        _onNewFriendPost = onNewFriendPost;
        _onReactionToMyPost = onReactionToMyPost;
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
            _myReactionPostId = null;
            _myReactionCounts.Clear();
            _onRoster(null);
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
            var roster = await _social.GetRosterAsync();
            _onRoster(roster);
            NotifyNewFriendPosts(roster);
            NotifyReactionsToMe(roster);
        }
        catch { /* best-effort: a failed poll just keeps the last roster on screen */ }
    }

    /// <summary>Raised (UI thread) with a human-readable line each poll describing the reaction state on your
    /// own post and any transition — a diagnostic sink for the debug tool.</summary>
    public event Action<string>? Diagnostic;

    // Fires once per genuinely-new reaction on your own latest status. The baseline is re-seeded (and firing
    // suppressed) whenever the tracked post changes — so the reactions already sitting on a post when polling
    // starts, and any that carry over when you post a new status, don't replay as "new".
    private void NotifyReactionsToMe(RosterSnapshot roster)
    {
        var post = roster.MyLatest;
        if (post is null)
        {
            if (_myReactionPostId is not null) Diagnostic?.Invoke("poll: you have no current status now — nothing to react to.");
            _myReactionPostId = null; _myReactionCounts.Clear();
            return;
        }

        string nowDesc = Describe(roster.MyReactions);
        string shortId = post.Id.ToString()[..8];

        bool samePost = _myReactionPostId == post.Id;
        if (!samePost)
        {
            // New (or first-seen) post: take the current reactions as the baseline, don't fire for them.
            _myReactionPostId = post.Id;
            _myReactionCounts.Clear();
            foreach (var g in roster.MyReactions) _myReactionCounts[g.Emoji] = g.Count;
            Diagnostic?.Invoke($"poll: now tracking post {shortId}; baseline reactions {nowDesc} (won't re-fire the baseline).");
            return;
        }

        string prevDesc = Describe(_myReactionCounts);
        int fired = 0;
        foreach (var g in roster.MyReactions)
        {
            int grew = g.Count - _myReactionCounts.GetValueOrDefault(g.Emoji, 0);
            for (int i = 0; i < grew; i++) { _onReactionToMyPost(g.Emoji); fired++; }   // one bubble per net-new reaction
        }
        _myReactionCounts.Clear();
        foreach (var g in roster.MyReactions) _myReactionCounts[g.Emoji] = g.Count;

        if (prevDesc != nowDesc || fired > 0)
            Diagnostic?.Invoke($"poll: post {shortId} reactions {prevDesc} -> {nowDesc}; new reactions detected: {fired}"
                               + (fired > 0 ? " (handler called — see gate line for whether a bubble showed)." : "."));
        else
            Diagnostic?.Invoke($"poll: post {shortId} reactions unchanged at {nowDesc} — no bubble (nothing new).");
    }

    private static string Describe(IReadOnlyList<ReactionGroup> groups) =>
        groups.Count == 0 ? "(none)"
            : string.Join(", ", groups.OrderBy(g => g.Emoji, StringComparer.Ordinal).Select(g => $"{g.Emoji}x{g.Count}"));

    private static string Describe(Dictionary<string, int> counts) =>
        counts.Count == 0 ? "(none)"
            : string.Join(", ", counts.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}x{kv.Value}"));

    // Fires a notification for each friend whose latest status is one we haven't surfaced yet. The first poll
    // after activation only primes the seen-set (the backlog isn't news); the roster is friends-only, so every
    // entry is someone other than me. Idempotent: a post is added to _seen the first time it's noticed.
    private void NotifyNewFriendPosts(RosterSnapshot roster)
    {
        var wasPrimed = _primed;
        foreach (var f in roster.Friends)
        {
            if (f.Latest is not { } latest) continue;
            if (!_seen.Add(latest.Id)) continue;        // already surfaced
            if (wasPrimed) _onNewFriendPost(latest);
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
