using Avalonia.Threading;
using Perch.Data;
using Perch.Feeds;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Drives the UI-free <see cref="FeedsService"/> from the app (docs/feeds-plan.md §4.1). A
/// <see cref="DispatcherTimer"/> ticks every few seconds on the UI thread and, when nothing is in flight and the
/// desktop isn't locked, runs <see cref="FeedsService.TickAsync"/> on the thread pool — the service decides what
/// is actually due (the delayed, staggered startup; the interval; backoff), so most ticks do nothing. Results
/// marshal back to the UI thread, refresh <see cref="Current"/> and raise <see cref="Changed"/> (and
/// <see cref="Arrived"/> for new entries).
///
/// <para><see cref="Apply"/> is idempotent and called from <c>ApplyDisplaySettings</c>: feeds off means the
/// service is stopped and nothing is fetched at all. Read markers are saved debounced, off the UI thread.</para>
/// </summary>
internal sealed class FeedsMonitorHost : IDisposable
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromMinutes(1);

    private readonly FeedsService _service;
    private readonly ISessionLock? _lock;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _saveRead;
    private readonly DateTime _createdUtc = DateTime.UtcNow;
    private bool _ticking, _disposed, _seeded;

    /// <summary>The latest view of every enabled subscription (UI thread).</summary>
    public FeedsSnapshot Current { get; private set; } = FeedsSnapshot.Empty;

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on the UI thread with the entries that arrived in a check (never a priming fetch).</summary>
    public event Action<IReadOnlyList<FeedArrivals>>? Arrived;

    public FeedsMonitorHost(FeedsService service, ISessionLock? sessionLock)
    {
        _service = service;
        _lock = sessionLock;
        _timer = new DispatcherTimer { Interval = TickEvery };
        _timer.Tick += (_, _) => Tick();
        _saveRead = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveRead.Tick += (_, _) => { _saveRead.Stop(); Task.Run(_service.SaveReadIfDirty); };
    }

    /// <summary>Pushes the settings into the engine: subscriptions, interval, on/off. Idempotent.</summary>
    public void Apply(AppSettings s)
    {
        if (_disposed || _seeded) return;
        _service.SetSubscriptions(s.Feeds ?? []);
        _service.Interval = TimeSpan.FromMinutes(s.FeedsIntervalMinutes);

        if (s.ShowFeeds && !_service.IsRunning)
        {
            // Within the first minute of the app's life this is the launch: wait, then stagger.
            _service.Start(atLaunch: DateTime.UtcNow - _createdUtc < LaunchWindow);
            _timer.Start();
        }
        else if (!s.ShowFeeds && _service.IsRunning)
        {
            _service.Stop();
            _timer.Stop();
        }
        Publish();
        if (_service.IsRunning) Tick();   // a newly added feed is due now
    }

    /// <summary>Checks one feed (or all) now — the row's "Refresh" and the story card's Retry.</summary>
    public void RefreshNow(string? subId = null)
    {
        if (_seeded) return;
        _service.RefreshNow(subId);
        Tick();
    }

    /// <summary>Marks an entry read (a story card was shown); persisted shortly after, off the UI thread.</summary>
    public void MarkRead(string subId, string entryId)
    {
        if (_seeded || !_service.MarkRead(subId, entryId)) return;
        Publish();
        ScheduleSave();
    }

    public void MarkAllRead(string? subId = null)
    {
        if (_seeded) return;
        _service.MarkAllRead(subId);
        Publish();
        ScheduleSave();
    }

    public FeedHead? Head(string subId) => Current.Heads.FirstOrDefault(h => h.SubId == subId);

    /// <summary>Headless-render seam: show a fixed snapshot. The engine is never started, and reads, refreshes and
    /// settings are ignored from here on, so a render touches no network and writes no read state.</summary>
    internal void SeedForRender(FeedsSnapshot snapshot)
    {
        _seeded = true;
        Current = snapshot;
        Changed?.Invoke();
    }

    private void ScheduleSave()
    {
        _saveRead.Stop();
        _saveRead.Start();
    }

    private void Tick()
    {
        if (_disposed || _ticking || !_service.IsRunning) return;
        if (_lock?.IsLocked == true) return;   // nobody's looking; the first tick after unlock catches up

        _ticking = true;
        Task.Run(() => _service.TickAsync()).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _ticking = false;
            if (_disposed || !t.IsCompletedSuccessfully) return;
            var result = t.Result;
            if (result.Changed) Publish();
            if (result.Arrivals.Count > 0) Arrived?.Invoke(result.Arrivals);
        }));
    }

    private void Publish()
    {
        Current = _service.Snapshot();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _saveRead.Stop();
        _service.Dispose();   // stops, saves read state, disposes the fetcher
    }
}
