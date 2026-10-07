namespace Perch.Feeds;

/// <summary>One head in the overlay row: what to draw and what the tooltip says.</summary>
internal sealed record FeedHead(
    string SubId,
    string Title,
    string? SiteUrl,
    string? IconPath,
    int UnreadCount,
    DateTime? NewestUnreadUtc,
    string? LatestTitle,
    DateTime? LatestUtc,
    string? Error,
    bool HasFetched,
    bool InsecureHttp);

/// <summary>Everything the UI needs, in display order (feeds with unread entries first, newest unread first;
/// then the rest in the user's order). <see cref="Stories"/> feeds <see cref="StoryPlan"/>.</summary>
internal sealed record FeedsSnapshot(IReadOnlyList<FeedHead> Heads, IReadOnlyList<StoryFeed> Stories)
{
    public static readonly FeedsSnapshot Empty = new([], []);
    public int TotalUnread => Heads.Sum(h => h.UnreadCount);
}

/// <summary>Entries that arrived in this tick (never on a priming fetch), per feed — for notifications.</summary>
internal sealed record FeedArrivals(string SubId, string FeedTitle, IReadOnlyList<FeedEntry> Entries);

internal sealed record FeedsTickResult(bool Changed, IReadOnlyList<FeedArrivals> Arrivals)
{
    public static readonly FeedsTickResult None = new(false, []);
}

/// <summary>
/// The feeds engine (docs/feeds-plan.md §4.1), UI-free so it's testable end to end with a stub HTTP handler and a
/// temp folder. It owns the subscriptions, the cache, the read state and the schedule; the app's monitor host
/// just calls <see cref="TickAsync"/> on a timer, then <see cref="Snapshot"/> on the UI thread.
///
/// <para>Thread-safety: all state sits behind one lock. Network and file writes happen outside it — a fetch
/// captures what it needs, awaits, then re-checks under the lock that the subscription still exists with the
/// same URL before applying (so a removal or edit mid-fetch drops the stale answer). At most
/// <see cref="MaxConcurrent"/> fetches run at once and never two for the same feed.</para>
/// </summary>
internal sealed class FeedsService : IDisposable
{
    public const int MaxConcurrent = 4;
    public static readonly TimeSpan IconRefresh = TimeSpan.FromDays(7);
    private static readonly TimeSpan InFlightGuard = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly FeedStore _store;
    private readonly FeedFetcher _fetcher;
    private readonly Func<DateTime> _clock;
    private readonly FeedSchedule _schedule = new();
    private readonly Dictionary<string, FeedCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FeedReadRecord> _read;
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private List<FeedSubscription> _subs = [];
    private bool _running, _readDirty;
    private TimeSpan _interval = TimeSpan.FromMinutes(30);

    public FeedsService(FeedStore store, FeedFetcher fetcher, Func<DateTime>? clock = null)
    {
        _store = store;
        _fetcher = fetcher;
        _clock = clock ?? (() => DateTime.UtcNow);
        _read = store.LoadRead();
    }

    /// <summary>The poll interval, clamped to 5–240 minutes. Applies from each feed's next success.</summary>
    public TimeSpan Interval
    {
        get { lock (_gate) return _interval; }
        set { lock (_gate) _interval = TimeSpan.FromMinutes(Math.Clamp(value.TotalMinutes, 5, 240)); }
    }

    public bool IsRunning { get { lock (_gate) return _running; } }

    /// <summary>
    /// Replaces the subscription list (from settings). Only enabled, well-formed entries are kept, in order. A new
    /// or re-enabled feed is fetched straight away while running; a re-pointed one (same id, new URL) loses its
    /// cache and read state and primes afresh; a removed one is forgotten on disk.
    /// </summary>
    public void SetSubscriptions(IEnumerable<FeedSubscription> subscriptions)
    {
        var all = subscriptions.Where(s => s is not null && FeedStore.IsValidId(s.Id)).Select(s => s.Clone()).ToList();
        var toDelete = new List<string>();
        lock (_gate)
        {
            var now = _clock();
            var next = all.Where(s => s.Enabled && FeedUrl.Safe(s.Url, null) is not null)
                          .GroupBy(s => s.Id).Select(g => g.First()).ToList();
            var nextIds = next.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var knownIds = all.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var old in _subs.Where(s => !nextIds.Contains(s.Id)))
            {
                _schedule.Remove(old.Id);
                if (!knownIds.Contains(old.Id))
                {
                    _cache.Remove(old.Id);
                    if (_read.Remove(old.Id)) _readDirty = true;
                    toDelete.Add(old.Id);
                }
            }

            foreach (var sub in next)
            {
                if (!_cache.TryGetValue(sub.Id, out var cache))
                    cache = _cache[sub.Id] = _store.LoadCache(sub.Id) ?? new FeedCacheEntry { SourceUrl = sub.Url };

                bool repointed = cache.SourceUrl.Length > 0 && cache.SourceUrl != sub.Url;
                if (repointed)
                {
                    _cache[sub.Id] = new FeedCacheEntry { SourceUrl = sub.Url };
                    if (_read.Remove(sub.Id)) _readDirty = true;
                    toDelete.Add(sub.Id);
                }
                else if (cache.SourceUrl.Length == 0) cache.SourceUrl = sub.Url;

                if (_running && (repointed || _schedule.DueAt(sub.Id) is null)) _schedule.Add(sub.Id, now);
            }
            _subs = next;
        }
        foreach (var id in toDelete) _store.DeleteFeed(id);
        SaveReadIfDirty();
    }

    /// <summary>Starts scheduling. At launch the first fetches wait <see cref="FeedSchedule.LaunchDelay"/> and are
    /// staggered; switching on at runtime only staggers.</summary>
    public void Start(bool atLaunch)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _schedule.Start(_subs.Select(s => s.Id).ToList(), _clock(), atLaunch);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _schedule.Clear();
        }
    }

    /// <summary>Makes one feed (or all) due now. A no-op while stopped.</summary>
    public void RefreshNow(string? subId = null)
    {
        lock (_gate)
        {
            if (!_running) return;
            var now = _clock();
            foreach (var s in _subs)
                if ((subId is null || s.Id == subId) && !_inFlight.Contains(s.Id)) _schedule.Now(s.Id, now);
        }
    }

    /// <summary>Fetches whatever is due (up to <see cref="MaxConcurrent"/> in flight) and applies the results.</summary>
    public async Task<FeedsTickResult> TickAsync(CancellationToken ct = default)
    {
        List<string> due;
        lock (_gate)
        {
            if (!_running) return FeedsTickResult.None;
            var now = _clock();
            int room = MaxConcurrent - _inFlight.Count;
            if (room <= 0) return FeedsTickResult.None;
            due = _schedule.Due(now, room + _inFlight.Count).Where(id => !_inFlight.Contains(id)).Take(room).ToList();
            foreach (var id in due)
            {
                _inFlight.Add(id);
                _schedule.Defer(id, now + InFlightGuard);
            }
        }
        if (due.Count == 0) return FeedsTickResult.None;

        var results = await Task.WhenAll(due.Select(id => FetchOneAsync(id, ct)));
        SaveReadIfDirty();
        var arrivals = results.Where(a => a is { Entries.Count: > 0 }).Select(a => a!).ToList();
        return new FeedsTickResult(true, arrivals);
    }

    private async Task<FeedArrivals?> FetchOneAsync(string id, CancellationToken ct)
    {
        string url;
        string? etag;
        DateTimeOffset? lastModified;
        lock (_gate)
        {
            var sub = _subs.FirstOrDefault(s => s.Id == id);
            if (sub is null || !_cache.TryGetValue(id, out var c)) { _inFlight.Remove(id); return null; }
            url = sub.Url;
            etag = c.ETag;
            lastModified = c.LastModified;
        }

        FeedFetchResult result;
        try
        {
            result = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? await _fetcher.FetchFeedAsync(uri, etag, lastModified, _clock(), ct)
                : new FeedFetchResult(FeedFetchStatus.Error, Error: "Invalid address");
        }
        catch (Exception)
        {
            result = new FeedFetchResult(FeedFetchStatus.Error, Error: "Couldn't fetch the feed");
        }

        FeedArrivals? arrivals = null;
        string? cacheJson = null;
        Uri? iconCandidate = null;
        string? privateHost = null;
        lock (_gate)
        {
            _inFlight.Remove(id);
            var sub = _subs.FirstOrDefault(s => s.Id == id);
            if (sub is null || sub.Url != url || !_cache.TryGetValue(id, out var cache)) return null;   // changed meanwhile
            var now = _clock();

            switch (result.Status)
            {
                case FeedFetchStatus.Ok:
                {
                    var doc = result.Doc!;
                    var rec = ReadRecord(id);
                    var fresh = FeedReadState.ApplyFetch(rec, cache.Doc, doc, now);
                    _readDirty = true;
                    cache.Doc = doc;
                    cache.ETag = result.ETag;
                    cache.LastModified = result.LastModified;
                    cache.FetchedUtc = now;
                    cache.FinalUrl = result.FinalUrl?.AbsoluteUri;
                    cache.IsPrivate = result.IsPrivate;
                    cache.Error = null;
                    cache.ErrorUtc = null;
                    _schedule.Succeeded(id, now, _interval);
                    if (fresh.Count > 0) arrivals = new FeedArrivals(id, TitleOf(sub, cache), fresh);

                    iconCandidate = IconCandidate(doc, result.FinalUrl);
                    if (iconCandidate is not null && !NeedsIcon(cache, iconCandidate, now)) iconCandidate = null;
                    if (result.IsPrivate && Uri.TryCreate(url, UriKind.Absolute, out var subUri)) privateHost = subUri.IdnHost;
                    break;
                }
                case FeedFetchStatus.NotModified:
                    cache.FetchedUtc = now;
                    cache.Error = null;
                    cache.ErrorUtc = null;
                    _schedule.Succeeded(id, now, _interval);
                    break;
                default:
                    cache.Error = FeedText.Clean(result.Error ?? "Couldn't fetch the feed", 200);
                    cache.ErrorUtc = now;
                    _schedule.Failed(id, now, result.RetryAfter);
                    break;
            }
            cacheJson = FeedStore.SerializeCache(id, cache);
        }
        if (cacheJson is not null) _store.WriteCache(id, cacheJson);

        if (iconCandidate is not null) await FetchIconAsync(id, url, iconCandidate, privateHost, ct);
        return arrivals;
    }

    // The feed's own icon/logo, else the site's /favicon.ico.
    private static Uri? IconCandidate(FeedDoc doc, Uri? finalUrl)
    {
        if (doc.IconUrl is not null) return FeedUrl.Safe(doc.IconUrl, null);
        var site = FeedUrl.Safe(doc.SiteUrl, null) ?? finalUrl;
        return site is null ? null : FeedUrl.Safe("/favicon.ico", site);
    }

    private static bool NeedsIcon(FeedCacheEntry cache, Uri candidate, DateTime now) =>
        cache.IconFetchedUtc is not { } at || now - at > IconRefresh || cache.IconSourceUrl != candidate.AbsoluteUri;

    private async Task FetchIconAsync(string id, string url, Uri icon, string? privateHost, CancellationToken ct)
    {
        byte[]? bytes;
        try { bytes = await _fetcher.FetchIconAsync(icon, privateHost, ct); }
        catch { bytes = null; }

        string? file = bytes is null ? null : _store.SaveIcon(id, bytes);
        string? json = null;
        lock (_gate)
        {
            var sub = _subs.FirstOrDefault(s => s.Id == id);
            if (sub is null || sub.Url != url || !_cache.TryGetValue(id, out var cache)) return;
            cache.IconSourceUrl = icon.AbsoluteUri;
            cache.IconFetchedUtc = _clock();   // a failure waits a week too — no hammering a missing favicon
            if (file is not null) cache.IconFile = file;
            json = FeedStore.SerializeCache(id, cache);
        }
        if (json is not null) _store.WriteCache(id, json);
    }

    // ── Read state ──────────────────────────────────────────────────────────────────────────────────────────

    private FeedReadRecord ReadRecord(string id)
    {
        if (!_read.TryGetValue(id, out var rec)) _read[id] = rec = new FeedReadRecord();
        return rec;
    }

    /// <summary>Marks one entry read (a story card was shown). Returns whether anything changed. Call
    /// <see cref="SaveReadIfDirty"/> (debounced, off the UI thread) to persist.</summary>
    public bool MarkRead(string subId, string entryId)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(subId, out var cache) || cache.Doc is null) return false;
            var rec = ReadRecord(subId);
            if (rec.PrimedUtc is null || rec.Read.ContainsKey(entryId)) return false;
            FeedReadState.MarkRead(rec, entryId, _clock());
            _readDirty = true;
            return true;
        }
    }

    /// <summary>Marks every entry of one feed (or of all feeds) read.</summary>
    public void MarkAllRead(string? subId = null)
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var s in _subs)
            {
                if (subId is not null && s.Id != subId) continue;
                if (_cache.TryGetValue(s.Id, out var cache) && cache.Doc is not null)
                {
                    FeedReadState.MarkAllRead(ReadRecord(s.Id), cache.Doc, now);
                    _readDirty = true;
                }
            }
        }
    }

    public void SaveReadIfDirty()
    {
        string json;
        lock (_gate)
        {
            if (!_readDirty) return;
            _readDirty = false;
            json = FeedStore.SerializeRead(_read);
        }
        _store.WriteRead(json);
    }

    // ── Snapshot ────────────────────────────────────────────────────────────────────────────────────────────

    public FeedsSnapshot Snapshot()
    {
        var rows = new List<(int Order, FeedHead Head, StoryFeed Story)>();
        lock (_gate)
        {
            for (int i = 0; i < _subs.Count; i++)
            {
                var sub = _subs[i];
                _cache.TryGetValue(sub.Id, out var cache);
                var doc = cache?.Doc;
                _read.TryGetValue(sub.Id, out var rec);

                var unread = rec is null || doc is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : doc.Entries.Where(e => FeedReadState.IsUnread(rec, e)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                DateTime? newestUnread = doc?.Entries.Where(e => unread.Contains(e.Id)).Select(e => (DateTime?)e.Updated).Max();
                var latest = doc?.Entries.FirstOrDefault();

                var head = new FeedHead(
                    sub.Id, TitleOf(sub, cache), doc?.SiteUrl,
                    cache is null ? null : _store.IconPath(sub.Id, cache.IconFile),
                    unread.Count, newestUnread, latest?.Title, latest?.Updated,
                    cache?.Error, cache?.Doc is not null,
                    sub.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
                var story = new StoryFeed(sub.Id, doc?.Entries ?? [], unread, cache?.Error);
                rows.Add((i, head, story));
            }
        }

        // Unread first (newest unread first), then everyone else in the user's order. Stable.
        var ordered = rows
            .OrderBy(r => r.Head.UnreadCount > 0 ? 0 : 1)
            .ThenByDescending(r => r.Head.UnreadCount > 0 ? r.Head.NewestUnreadUtc : null)
            .ThenBy(r => r.Order)
            .ToList();
        return new FeedsSnapshot(ordered.Select(r => r.Head).ToList(), ordered.Select(r => r.Story).ToList());
    }

    private static string TitleOf(FeedSubscription sub, FeedCacheEntry? cache)
    {
        var over = FeedText.Clean(sub.TitleOverride, FeedText.FeedTitleMax);
        if (over.Length > 0) return over;
        if (cache?.Doc?.Title is { Length: > 0 } t) return t;
        return Uri.TryCreate(sub.Url, UriKind.Absolute, out var u) ? FeedUrl.DisplayHost(u) : "Feed";
    }

    public void Dispose()
    {
        Stop();
        SaveReadIfDirty();
        _fetcher.Dispose();
    }
}
