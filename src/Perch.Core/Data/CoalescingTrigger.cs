namespace Perch.Data;

/// <summary>
/// Collapses a burst of "do it now" requests into as few runs as possible: at most one run in flight, any number
/// of requests during a run collapse into a single trailing run, and consecutive runs start at least
/// <c>minGap</c> apart. An isolated request (nothing running, gap elapsed) runs immediately, so a user's own
/// action still feels instant, while a flood of triggers (e.g. inbox broadcasts) costs one run per gap at most.
///
/// <para>Call <see cref="Request"/> from one context (the UI thread, for the feed poll): the work's awaits resume
/// on the caller's synchronization context, exactly as a direct <c>_ = Poll()</c> would. A throwing run is
/// swallowed, like the fire-and-forget calls it replaces.</para>
/// </summary>
public sealed class CoalescingTrigger
{
    private readonly Func<Task> _work;
    private readonly TimeSpan _minGap;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly object _gate = new();
    private bool _running;     // a run is in flight, or waiting out the gap before starting
    private bool _pending;     // a request arrived after the current run started → one more run afterwards
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public CoalescingTrigger(Func<Task> work, TimeSpan minGap,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, Task>? delay = null)
    {
        _work = work;
        _minGap = minGap;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? (d => Task.Delay(d));
    }

    /// <summary>Asks for a run. Never blocks and never throws.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_running) { _pending = true; return; }
            _running = true;
        }
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            var wait = _lastStart == DateTimeOffset.MinValue ? TimeSpan.Zero : _lastStart + _minGap - _now();
            if (wait > TimeSpan.Zero)
            {
                try { await _delay(wait); } catch { /* a failed delay just runs early */ }
            }

            // Requests made up to this point are served by the run about to start.
            lock (_gate) { _pending = false; _lastStart = _now(); }
            try { await _work(); } catch { /* best-effort, like the fire-and-forget call it replaces */ }

            lock (_gate)
            {
                if (!_pending) { _running = false; return; }
            }
        }
    }
}
