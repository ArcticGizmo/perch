namespace Perch.Feeds;

/// <summary>
/// When each subscription is next due (docs/feeds-plan.md §4.1). Pure — the clock is always passed in.
/// <list type="bullet">
///   <item><b>Startup:</b> feed <c>i</c> (in row order) is due at <c>now + <see cref="LaunchDelay"/> + i ×
///   <see cref="Stagger"/></c> — a delayed, staggered refresh, never a burst at launch. Switching the feature on
///   at runtime uses the same stagger without the delay.</item>
///   <item><b>Added feed:</b> due immediately (its priming fetch).</item>
///   <item><b>Success / not modified:</b> due after the interval plus a small per-feed jitter, so feeds that
///   started together drift apart.</item>
///   <item><b>Failure:</b> exponential backoff, 5 min doubling to a 2 h cap, never sooner than the server's
///   <c>Retry-After</c>.</item>
/// </list>
/// </summary>
internal sealed class FeedSchedule
{
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Stagger = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan BackoffStart = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackoffCap = TimeSpan.FromHours(2);

    private sealed class Timing
    {
        public DateTime DueUtc;
        public int Failures;
    }

    private readonly Dictionary<string, Timing> _timings = new(StringComparer.Ordinal);

    /// <summary>Schedules every subscription in row order; <paramref name="atLaunch"/> adds the launch delay.</summary>
    public void Start(IReadOnlyList<string> ids, DateTime nowUtc, bool atLaunch)
    {
        _timings.Clear();
        var first = atLaunch ? nowUtc + LaunchDelay : nowUtc;
        for (int i = 0; i < ids.Count; i++)
            _timings[ids[i]] = new Timing { DueUtc = first + Stagger * i };
    }

    public void Clear() => _timings.Clear();

    /// <summary>A feed added (or re-pointed) while running: fetch it now.</summary>
    public void Add(string id, DateTime nowUtc) => _timings[id] = new Timing { DueUtc = nowUtc };

    public void Remove(string id) => _timings.Remove(id);

    /// <summary>Up to <paramref name="max"/> due ids, most overdue first.</summary>
    public IReadOnlyList<string> Due(DateTime nowUtc, int max) =>
        _timings.Where(kv => kv.Value.DueUtc <= nowUtc).OrderBy(kv => kv.Value.DueUtc).Take(max).Select(kv => kv.Key).ToList();

    public DateTime? DueAt(string id) => _timings.TryGetValue(id, out var t) ? t.DueUtc : null;

    /// <summary>Holds a feed back while its fetch is in flight, so the next tick doesn't start a second one.</summary>
    public void Defer(string id, DateTime untilUtc)
    {
        if (_timings.TryGetValue(id, out var t)) t.DueUtc = untilUtc;
    }

    public void Succeeded(string id, DateTime nowUtc, TimeSpan interval)
    {
        if (!_timings.TryGetValue(id, out var t)) return;
        t.Failures = 0;
        t.DueUtc = nowUtc + interval + Jitter(id, interval);
    }

    public void Failed(string id, DateTime nowUtc, TimeSpan? retryAfter)
    {
        if (!_timings.TryGetValue(id, out var t)) return;
        t.Failures++;
        var backoff = TimeSpan.FromTicks(Math.Min(BackoffCap.Ticks, BackoffStart.Ticks << Math.Min(t.Failures - 1, 10)));
        if (retryAfter is { } ra && ra > backoff) backoff = ra;
        t.DueUtc = nowUtc + backoff;
    }

    /// <summary>Makes a feed due now (Refresh). Clears its backoff.</summary>
    public void Now(string id, DateTime nowUtc)
    {
        if (_timings.TryGetValue(id, out var t)) { t.DueUtc = nowUtc; t.Failures = 0; }
    }

    // Up to 10% of the interval, fixed per feed (a stable hash of its id) so tests and restarts are predictable.
    private static TimeSpan Jitter(string id, TimeSpan interval)
    {
        uint h = 2166136261;
        foreach (char c in id) h = (h ^ c) * 16777619;
        return TimeSpan.FromTicks((long)(interval.Ticks * 0.1 * (h % 1000) / 1000.0));
    }
}
