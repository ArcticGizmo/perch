using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// Lets a poller skip its work while the desktop is locked and catch up as soon as it unlocks. Nobody sees the
/// overlay while the screen is locked, so a usage, status or social poll then only costs network and wakeups.
///
/// <para><see cref="ISessionLock"/> has no change event on every head (the macOS one is a query), so the flag is
/// sampled on a slow UI-thread timer. A deferral is only taken against a sampled lock, which guarantees the
/// matching unlock is seen and every deferred catch-up runs; a lock shorter than one sample defers nothing.</para>
/// </summary>
internal sealed class LockWatch : IDisposable
{
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(5);

    private readonly ISessionLock _lock;
    private readonly DispatcherTimer _timer;
    // One pending catch-up per owner, so a poller that skips several ticks while locked catches up once.
    private readonly Dictionary<object, Action> _owed = new(ReferenceEqualityComparer.Instance);

    public LockWatch(ISessionLock sessionLock)
    {
        _lock = sessionLock;
        _timer = new DispatcherTimer { Interval = SampleEvery };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    /// <summary>The lock state as of the last sample.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>When the desktop is locked, records <paramref name="catchUp"/> to run (on the UI thread) at the next
    /// unlock, replacing any earlier one from the same <paramref name="owner"/>, and returns true so the caller skips
    /// its work now. Returns false when unlocked.</summary>
    public bool Defer(object owner, Action catchUp)
    {
        if (!IsLocked) return false;
        _owed[owner] = catchUp;
        return true;
    }

    private void Sample()
    {
        bool locked;
        try { locked = _lock.IsLocked; }
        catch { locked = false; }
        if (locked == IsLocked) return;
        IsLocked = locked;

        // The only live evidence that pollers paused: nothing on screen changes while the desktop is locked.
        var line = locked ? "locked" : $"unlocked; catching up: {string.Join(", ", _owed.Keys.Select(k => k.GetType().Name))}";
        Task.Run(() => DiagnosticLog.Append("lock.log", line));
        if (locked || _owed.Count == 0) return;

        var due = _owed.Values.ToList();
        _owed.Clear();
        foreach (var catchUp in due)
        {
            try { catchUp(); }
            catch { /* one poller's catch-up failing must not starve the rest */ }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _owed.Clear();
    }
}
