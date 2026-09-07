using System.Collections.Concurrent;

namespace Perch.Data.Control;

/// <summary>The live state of a Perch-controlled session, as its window reports it: a settled session is
/// <see cref="Idle"/>, a running turn is <see cref="Busy"/>, and one blocked on a permission/question is
/// <see cref="Waiting"/>. Maps onto the overlay's Running / AwaitingInput / Idle statuses.</summary>
internal enum ControlledActivity { Idle, Busy, Waiting }

/// <summary>
/// The session ids this Perch instance currently owns over stream-json (<see cref="ClaudeSessionController"/>
/// registers on init, unregisters on exit), each with its live <see cref="ControlledActivity"/>. Consulted so
/// the other control surfaces don't double-handle an owned session — the permission valet must pass (the
/// console already answers <c>can_use_tool</c> over stdin), and focus routing should target the console window
/// rather than hunt for a terminal — and so <see cref="SessionMonitor"/> can read an owned session's real
/// status directly (the CLI doesn't heartbeat the session-file <c>status</c> over stream-json, so it would
/// otherwise read as idle however hard the session works). In-process only by design: a session another Perch
/// instance drives isn't here, and the overlay simply falls back to the CLI status for it.
/// </summary>
internal static class ControlledSessions
{
    private static readonly ConcurrentDictionary<string, ControlledActivity> Ids = new();

    /// <summary>Fires whenever the owned set or a session's status changes, so <see cref="SessionMonitor"/> can
    /// rescan promptly — a registry change writes no file, so nothing else would trigger the overlay to
    /// refresh until the reconcile poll. Best-effort and may fire on any thread; the monitor debounces it.</summary>
    public static event Action? Changed;

    public static void Register(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        Ids[sessionId] = ControlledActivity.Idle;
        Changed?.Invoke();
    }

    public static void Unregister(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId) && Ids.TryRemove(sessionId, out _))
            Changed?.Invoke();
    }

    public static bool Owns(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && Ids.ContainsKey(sessionId);

    /// <summary>Records the live status of an owned session. Only ever <em>updates</em> an existing entry —
    /// never adds one — so a status update racing <see cref="Unregister"/> at shutdown can't resurrect a
    /// session that has already let go (a lingering entry would be harmless anyway: its process is gone, so
    /// the monitor drops it on the dead-pid check). Fires <see cref="Changed"/> only on an actual change, so
    /// the many no-op status writes (StateChanged fires for more than just busy/idle) don't churn rescans.</summary>
    public static void SetActivity(string sessionId, ControlledActivity activity)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (!Ids.TryGetValue(sessionId, out var current) || current == activity) return;
        Ids[sessionId] = activity;
        Changed?.Invoke();
    }

    /// <summary>The live status of a session this instance owns, or null when it doesn't own it.</summary>
    public static ControlledActivity? Activity(string? sessionId) =>
        !string.IsNullOrEmpty(sessionId) && Ids.TryGetValue(sessionId, out var a) ? a : null;
}
