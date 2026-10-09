using Avalonia.Controls;
using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia;

// Session recovery (docs/session-recovery-plan.md, R3/R5): Perch's own ledger of its runs — when this run started, a
// heartbeat while it's alive, how it ended (a clean Exit, or the OS shutting down) — and of the Perch-controlled
// sessions it holds. The next run reads it to find the shutdown that ended this one (the Recent list's "just before
// shutdown" flag) and to bring those sessions back.
//
// A Perch session lives until the user ends it. Whatever stops its process — Perch exiting, an update, a crash, the OS
// restarting — it stays where it was (an overlay row, a Roost pane) with no claude behind it, and the first reply starts
// it again with the settings it last ran with.
public partial class App
{
    private SessionLedger? _ledger;
    // The shutdown that ended the previous run (BeginRun's answer), if any: the restart the startup toast is about.
    private DateTime? _previousShutdown;
    private bool _recoveryToastPending;
    private DispatcherTimer? _ledgerHeartbeat;
    private bool _ledgerShutdownStamped;
    private bool _exiting;   // Perch is closing: the processes it stops now come back next run, not this one
    private static readonly Lock LedgerSaveGate = new();
    private CoalescingTrigger? _ledgerSave;   // SaveLedgerSoon: a burst of changes is one save, off the UI thread

    // The Perch sessions this run holds in the ledger, each with the entry last written for it (so a /clear's new id
    // rebinds the entry, and an unchanged one isn't rewritten). A session leaves when the user releases it (ended /
    // handed back) or its process ends.
    private readonly Dictionary<Services.PerchSession, LedgerSession?> _heldSessions = new();

    // Perch sessions Perch holds with no process: the previous run's (until they're woken), and this run's whose process
    // Perch stopped or lost. Overlay rows (App.DormantRows.cs) and Roost panes in their usual places (App.RoostDormant.cs).
    private List<LedgerSession> _parked = [];

    // The same guard every other Perch-owned file honours: render/tests disable persistence, and a replay runs against
    // a sandbox, so its stamps would describe a recording rather than this machine.
    private static bool LedgerWritable => !AppSettings.PersistenceDisabled && !Services.Replay.ReplaySession.IsActive;

    /// <summary>Begins this run in the ledger. The power-history lookup reads the event log, so it runs off the UI
    /// thread; the heartbeat starts only once it's done, so it can't overwrite the previous run's last-alive time
    /// before <see cref="SessionLedger.BeginRun"/> has read it.</summary>
    private void StartSessionLedger()
    {
        if (!LedgerWritable)
        {
            RefreshRecent();   // marks the Roost's dormant set ready (there's none to find)
            return;
        }
        var path = SessionLedger.DefaultPath;
        Task.Run(() =>
        {
            var ledger = SessionLedger.Load(path);
            var shutdown = ledger.BeginRun(DateTime.Now, PlatformServices.PowerHistory);
            SaveLedger(ledger, path);
            LaunchLog.Write(shutdown is { } s
                ? $"session ledger: the previous run ended with a shutdown at {s:yyyy-MM-dd HH:mm:ss}"
                : "session ledger: no shutdown since the previous run");
            return (ledger, shutdown);
        }).ContinueWith(t =>
        {
            if (_ledgerShutdownStamped) return;
            // The Recent list needs the ledger (held sessions, shutdowns); without one it's built bare.
            if (!t.IsCompletedSuccessfully) { RefreshRecent(); return; }
            var (ledger, shutdown) = t.Result;
            _ledger = ledger;
            _ledgerSave = new CoalescingTrigger(() => Task.Run(() => SaveLedger(ledger, SessionLedger.DefaultPath)), TimeSpan.Zero);
            _previousShutdown = shutdown;

            // What the previous run held is what comes back — read before this run's own sessions are tracked in.
            var holding = _heldSessions.Keys.Select(s => s.SessionId).OfType<string>().ToHashSet();
            _parked = ledger.Sessions.Where(s => !holding.Contains(s.SessionId)).ToList();
            // A session parked before Perch kept SessionAccounts still resumes under the account it ran under. Only a
            // pinned dir counts: a null may be one an earlier resume wrongly inherited.
            foreach (var s in _parked)
                if (s.ConfigDir is { Length: > 0 } dir && SessionAccounts.Recall(s.SessionId) is null)
                    SessionAccounts.Remember(s.SessionId, dir);
            foreach (var s in _heldSessions.Keys.ToList()) TrackHeld(s);   // started before the ledger was ready
            OnParkedChanged();
            // The toast waits for the Recent build, so it can also count the sessions a restart interrupted.
            _recoveryToastPending = true;
            RefreshRecent();

            _ledgerHeartbeat = new DispatcherTimer { Interval = SessionRecovery.HeartbeatInterval };
            _ledgerHeartbeat.Tick += (_, _) =>
            {
                // Still here a heartbeat after an OS shutdown was stamped: it was cancelled (an unsaved note, another
                // app, the user), so the stamp would describe a shutdown that never happened.
                if (_ledgerShutdownStamped)
                {
                    _ledgerShutdownStamped = false;
                    ledger.CancelShutdown();
                }
                ledger.Heartbeat(DateTime.Now);
                SaveLedgerSoon();
            };
            _ledgerHeartbeat.Start();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ── Held sessions ─────────────────────────────────────────────────────────────

    /// <summary>Holds a Perch session in the ledger from now on: it started, or a dormant one woke. It comes back
    /// not running after a restart unless the user releases it first.</summary>
    private void HoldSession(Services.PerchSession session)
    {
        if (!_heldSessions.ContainsKey(session))
        {
            _heldSessions[session] = null;
            session.TitleChanged += () => TrackHeld(session);
            // /clear starts a new id under the same process, and a mode switch changes what it would resume with: both
            // land here (TrackHeld writes only a real change).
            session.Conversation.StateChanged += () => TrackHeld(session);
            session.Released += ReleaseHeld;
            session.Ended += OnHeldEnded;
        }
        TrackHeld(session);
    }

    private void TrackHeld(Services.PerchSession session)
    {
        if (!_heldSessions.TryGetValue(session, out var last) || _ledger is not { } ledger || session.SessionId is not { } id) return;
        var entry = new LedgerSession(id, session.Cwd, session.ConfigDir, session.Title,
            session.Model, session.PermissionMode, session.Effort);
        if (entry == last) return;
        if (last is not null && last.SessionId != id) ledger.Rebind(last.SessionId, id);
        _heldSessions[session] = entry;
        ledger.Track(entry);
        DropParked(id);
        SaveLedgerSoon();
    }

    // The process went. Perch stopping it (an update) or losing it (a crash) leaves it where it was, not running; one
    // that exited by itself, cleanly (/exit), is the user ending it. Perch exiting leaves the ledger as it is: those
    // come back next run.
    private void OnHeldEnded(Services.PerchSession session)
    {
        _heldSessions.Remove(session);
        if (_exiting || session.SessionId is not { } id || _ledger?.Find(id) is not { } entry) return;
        if (session.StoppedByPerch || session.ExitCode != 0) Park(entry);
        else ForgetHeld(id);
    }

    // The user ended it or handed it back to a terminal: it isn't Perch's to bring back.
    private void ReleaseHeld(Services.PerchSession session)
    {
        _heldSessions.Remove(session);
        if (session.SessionId is { } id) ForgetHeld(id);
    }

    // Out of the ledger and the parked list: it's gone from the overlay and the Roost, and won't come back.
    private void ForgetHeld(string sessionId)
    {
        _ledger?.Forget(sessionId);
        DropParked(sessionId);
        SaveLedgerSoon();
    }

    // ── Parked sessions ───────────────────────────────────────────────────────────

    private void Park(LedgerSession entry)
    {
        _parked.RemoveAll(s => s.SessionId == entry.SessionId);
        _parked.Add(entry);
        OnParkedChanged();
    }

    private void DropParked(string sessionId)
    {
        if (_parked.RemoveAll(s => s.SessionId == sessionId) > 0) OnParkedChanged();
    }

    // A batch in one go: one refold, not one per session.
    private void DropParked(IReadOnlySet<string> sessionIds)
    {
        if (sessionIds.Count > 0 && _parked.RemoveAll(s => sessionIds.Contains(s.SessionId)) > 0) OnParkedChanged();
    }

    private void OnParkedChanged()
    {
        RefoldRoost();      // Roost panes
        PushOverlayRows();  // overlay rows
    }

    /// <summary>The launch settings a not-running Perch session last ran with, for its wake. Null when Perch doesn't
    /// hold it.</summary>
    private LedgerSession? ParkedEntry(string? sessionId) =>
        sessionId is null ? null : _parked.FirstOrDefault(s => s.SessionId == sessionId) ?? _ledger?.Find(sessionId);

    // ── Exit and shutdown ─────────────────────────────────────────────────────────

    /// <summary>The OS is shutting down or logging off (Avalonia's ShutdownRequested, which Perch's own Exit never
    /// raises). Written synchronously: the process may not get another chance. Windows only: on macOS Avalonia raises
    /// the same event for an ordinary Cmd+Q / Dock "Quit" (applicationShouldTerminate), so there it stays a plain exit
    /// until the port adds a real power-off signal (NSWorkspaceWillPowerOffNotification).</summary>
    private void StampLedgerShutdown()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ledgerShutdownStamped = true;
        if (_ledger is not { } ledger) return;
        ledger.MarkShutdown(DateTime.Now);
        SaveLedger(ledger, SessionLedger.DefaultPath);
    }

    /// <summary>Perch is exiting. A clean exit unless the OS shutdown was already stamped (that path ends here too).
    /// The held sessions are already in the ledger, so ending their processes here doesn't lose them.</summary>
    private void StampLedgerExit()
    {
        _exiting = true;
        _ledgerHeartbeat?.Stop();
        if (_ledgerShutdownStamped || _ledger is not { } ledger) return;
        ledger.MarkCleanExit(DateTime.Now);
        SaveLedger(ledger, SessionLedger.DefaultPath);
    }

    // Off the UI thread, one at a time, a burst collapsed into one trailing save. Each serialises the ledger when it
    // runs, so the last save always writes the latest state; the gate keeps it apart from the synchronous exit saves.
    private void SaveLedgerSoon() => _ledgerSave?.Request();

    private static void SaveLedger(SessionLedger ledger, string path)
    {
        try
        {
            lock (LedgerSaveGate) ledger.Save(path);
        }
        catch (Exception ex)
        {
            LaunchLog.Write($"session ledger: couldn't save ({ex.GetType().Name})");
        }
    }
}
