using System.Text.Json;
using Perch.Platform;

namespace Perch.Data;

/// <summary>One run of Perch, as the ledger saw it: when it started, when it was last alive, and how it ended (a clean
/// Exit, or the OS shutting down). Both end stamps stay null when Perch was killed.</summary>
internal sealed class LedgerRun
{
    public DateTime StartedAt { get; set; }
    public DateTime? LastAlive { get; set; }
    public DateTime? CleanExitAt { get; set; }
    public DateTime? ShutdownAt { get; set; }
}

/// <summary>A Perch-controlled session Perch is holding, live or not running, so it comes back after Perch restarts
/// (docs/session-recovery-plan.md, D3). Removed when the user ends it. The launch settings (null = the CLI's default)
/// are what it last ran with, so its first send starts it the way it was running.</summary>
internal sealed record LedgerSession(
    string SessionId, string Cwd, string? ConfigDir, string? Title,
    string? Model = null, string? PermissionMode = null, string? Effort = null);

/// <summary>
/// Perch's own record for session recovery (docs/session-recovery-plan.md, D10): the current run's stamps, the recent
/// shutdowns, and the Perch-controlled sessions it holds. Kept in <c>session-ledger.json</c> beside
/// <c>settings.json</c> rather than in <see cref="AppSettings"/>, because it changes on every heartbeat. Thread-safe;
/// <see cref="Load"/> never throws and <see cref="Save"/> writes atomically.
/// </summary>
internal sealed class SessionLedger
{
    private readonly Lock _gate = new();

    public LedgerRun Run { get; set; } = new();
    public List<DateTime> Shutdowns { get; set; } = [];
    public List<LedgerSession> Sessions { get; set; } = [];

    /// <summary>Where the ledger lives: beside <c>settings.json</c>, so it follows the dev/release profile.</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.SettingsFilePath) ?? Path.GetTempPath(), "session-ledger.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The ledger at <paramref name="path"/>, or an empty one when it's missing or unreadable — losing it only
    /// loses the "just before shutdown" flags and the restore list, never anything the user typed.</summary>
    public static SessionLedger Load(string path)
    {
        if (AtomicFile.TryRead(path, out var json) != AtomicFile.ReadResult.Ok) return new();
        try
        {
            var ledger = JsonSerializer.Deserialize<SessionLedger>(json) ?? new();
            ledger.Run ??= new();
            ledger.Shutdowns ??= [];
            ledger.Sessions = (ledger.Sessions ?? []).Where(s => !string.IsNullOrEmpty(s?.SessionId)).ToList();
            return ledger;
        }
        catch
        {
            return new();
        }
    }

    public void Save(string path)
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(this, WriteOptions);
        AtomicFile.Write(path, json);
    }

    /// <summary>Starts a new run at <paramref name="now"/>: works out the shutdown (if any) that ended the previous run,
    /// adds it to <see cref="Shutdowns"/>, drops shutdowns older than the Recent window, and resets the run stamps.
    /// <see cref="Sessions"/> carries over: those are what comes back dormant. Returns the shutdown it found.</summary>
    public DateTime? BeginRun(DateTime now, IPowerHistory? power)
    {
        lock (_gate)
        {
            var previous = Run.StartedAt == default ? null : Run;
            var shutdown = ShutdownClock.Resolve(previous, power, now);
            // The same shutdown can be found again (a stamp and the event log, or two quick restarts reading one log).
            if (shutdown is { } s && !Shutdowns.Any(x => (x - s).Duration() < SessionRecovery.HeartbeatInterval))
                Shutdowns.Add(s);
            Shutdowns.RemoveAll(x => now - x > SessionRecovery.RecentWindow);
            Shutdowns.Sort();
            Run = new LedgerRun { StartedAt = now, LastAlive = now };
            return shutdown;
        }
    }

    public void Heartbeat(DateTime now)
    {
        lock (_gate) Run.LastAlive = now;
    }

    public void MarkCleanExit(DateTime now)
    {
        lock (_gate) Run.CleanExitAt = Run.LastAlive = now;
    }

    public void MarkShutdown(DateTime now)
    {
        lock (_gate) Run.ShutdownAt = Run.LastAlive = now;
    }

    /// <summary>The shutdown stamped earlier didn't happen: something cancelled it (an unsaved note in Perch, another
    /// app, the user), and Perch is still running.</summary>
    public void CancelShutdown()
    {
        lock (_gate) Run.ShutdownAt = null;
    }

    /// <summary>Starts (or updates) holding a Perch-controlled session.</summary>
    public void Track(LedgerSession session)
    {
        lock (_gate)
        {
            int i = Sessions.FindIndex(s => s.SessionId == session.SessionId);
            if (i >= 0) Sessions[i] = session;
            else Sessions.Add(session);
        }
    }

    /// <summary>Stops holding a session: the user ended it, so it doesn't come back.</summary>
    public void Forget(string sessionId)
    {
        lock (_gate) Sessions.RemoveAll(s => s.SessionId == sessionId);
    }

    /// <summary>The same conversation continuing under a new id (<c>/clear</c> starts one under the same process).</summary>
    public void Rebind(string oldId, string newId)
    {
        if (oldId == newId) return;
        lock (_gate)
        {
            int i = Sessions.FindIndex(s => s.SessionId == oldId);
            if (i < 0) return;
            Sessions[i] = Sessions[i] with { SessionId = newId };
            // Keep the moved entry's place; drop any older entry that already had the new id.
            for (int j = Sessions.Count - 1; j >= 0; j--)
                if (j != i && Sessions[j].SessionId == newId) Sessions.RemoveAt(j);
        }
    }

    /// <summary>The held session with <paramref name="sessionId"/>, or null.</summary>
    public LedgerSession? Find(string sessionId)
    {
        lock (_gate) return Sessions.FirstOrDefault(s => s.SessionId == sessionId);
    }

    /// <summary>The ids of the sessions Perch holds (they're shown as Perch's own, so Recent leaves them out).</summary>
    public IReadOnlySet<string> HeldIds()
    {
        lock (_gate) return Sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A copy of the shutdown list, for <see cref="RecentSessions"/>.</summary>
    public IReadOnlyList<DateTime> ShutdownsSnapshot()
    {
        lock (_gate) return Shutdowns.ToArray();
    }
}
