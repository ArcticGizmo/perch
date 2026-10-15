namespace Perch.Data;

/// <summary>
/// Remembers, for each app holding the microphone, which of its windows is "the call". A mic monitor can
/// tell which app has the mic, but an app with several windows in one process gives no way to tell them
/// apart afterwards. New Teams is the case that matters: the hub and the meeting window belong to the same
/// <c>ms-teams.exe</c>, so "the app's most recently used window" lands on whichever one you touched last.
///
/// <para>The answer used here is the window that was in the foreground when the app was <em>first seen</em>
/// taking the mic. You join a call by pressing Join in the call's own window (Teams' pre-join screen, a Zoom
/// meeting, a browser tab), so that window is almost always the one in front at that moment. It works the
/// same for every app, and the platform supplies only "the foreground window, if it belongs to this app".</para>
///
/// <para>A sighting on the monitor's <em>first</em> observation is deliberately not learned. A mic already in
/// use when watching starts (Perch launched mid-call, the strip just switched on) began at some unknown
/// earlier time, and the foreground window now says nothing about the call. Those entries stay at 0, which
/// means "unknown", and the activator falls back to its usual most-recently-used rule. The memory lasts for
/// as long as the app holds the mic. Once it lets go, the next call learns its window afresh.</para>
///
/// Pure bookkeeping with no OS calls, so it is unit-testable. Not thread-safe; the caller serialises.
/// </summary>
public sealed class MicCallWindowTracker
{
    private readonly Dictionary<string, long> _windows = new(StringComparer.OrdinalIgnoreCase);
    private bool _primed;

    /// <summary>
    /// Records one poll's holders and returns each one's remembered call window (0 = unknown).
    /// <paramref name="foregroundWindowOf"/> is asked only for a holder that is new since the last poll, and
    /// returns the current foreground window when it belongs to that app, else 0.
    /// </summary>
    public IReadOnlyDictionary<string, long> Observe(
        IReadOnlyCollection<string> holders, Func<string, long> foregroundWindowOf)
    {
        var current = new HashSet<string>(holders, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _windows.Keys.Where(k => !current.Contains(k)).ToList())
            _windows.Remove(gone);

        foreach (var identity in current)
        {
            if (_windows.ContainsKey(identity)) continue;
            _windows[identity] = _primed ? foregroundWindowOf(identity) : 0;
        }

        _primed = true;
        return _windows;
    }

    /// <summary>Forgets everything, including that a first poll has happened, for when watching stops.</summary>
    public void Reset()
    {
        _windows.Clear();
        _primed = false;
    }
}
