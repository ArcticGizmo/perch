using Avalonia.Controls;
using Avalonia.Threading;
using Perch.Data;
using Perch.Platform;

namespace Perch.Avalonia;

// Session recovery (docs/session-recovery-plan.md, R3/R5): Perch's own ledger of its runs — when this run started, a
// heartbeat while it's alive, how it ended (a clean Exit, or the OS shutting down) — and of the Perch-controlled
// sessions it holds. The next run reads it to find the shutdown that ended this one (the Recent list's "just before
// shutdown" flag) and to bring those sessions back, dormant.
public partial class App
{
    private SessionLedger? _ledger;
    private DispatcherTimer? _ledgerHeartbeat;
    private bool _ledgerShutdownStamped;
    private static readonly Lock LedgerSaveGate = new();

    // The Perch sessions this run holds in the ledger, each with the id last written for it (so a /clear's new id
    // rebinds the entry). A session leaves when the user releases it (ended / handed back) or its process ends.
    private readonly Dictionary<Services.PerchSession, string?> _heldSessions = new();

    // Sessions the previous run held that haven't been picked up yet: the "Reopen Perch sessions" tray item (and, in
    // R6/R7, the Roost rail and the top of the overlay's Recent section).
    private List<LedgerSession> _restorable = [];
    private NativeMenuItem? _reopenItem;

    /// <summary>This run's ledger, once it has begun. Null under the headless render, tests and replay (which never
    /// write it), and for the first moment of a run while the previous shutdown is still being looked up.</summary>
    internal SessionLedger? Ledger => _ledger;

    /// <summary>The previous run's Perch sessions that are waiting to be reopened.</summary>
    internal IReadOnlyList<LedgerSession> RestorableSessions => _restorable;

    // The same guard every other Perch-owned file honours: render/tests disable persistence, and a replay runs against
    // a sandbox, so its stamps would describe a recording rather than this machine.
    private static bool LedgerWritable => !AppSettings.PersistenceDisabled && !Services.Replay.ReplaySession.IsActive;

    /// <summary>Begins this run in the ledger. The power-history lookup reads the event log, so it runs off the UI
    /// thread; the heartbeat starts only once it's done, so it can't overwrite the previous run's last-alive time
    /// before <see cref="SessionLedger.BeginRun"/> has read it.</summary>
    private void StartSessionLedger()
    {
        if (!LedgerWritable) return;
        var path = SessionLedger.DefaultPath;
        Task.Run(() =>
        {
            var ledger = SessionLedger.Load(path);
            var shutdown = ledger.BeginRun(DateTime.Now, PlatformServices.PowerHistory);
            SaveLedger(ledger, path);
            LaunchLog.Write(shutdown is { } s
                ? $"session ledger: the previous run ended with a shutdown at {s:yyyy-MM-dd HH:mm:ss}"
                : "session ledger: no shutdown since the previous run");
            return ledger;
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully || _ledgerShutdownStamped) return;
            var ledger = t.Result;
            _ledger = ledger;

            // What the previous run held is what comes back — read before this run's own sessions are tracked in.
            var holding = _heldSessions.Keys.Select(s => s.SessionId).OfType<string>().ToHashSet();
            _restorable = ledger.Sessions.Where(s => !holding.Contains(s.SessionId)).ToList();
            foreach (var s in _heldSessions.Keys.ToList()) TrackHeld(s);   // started before the ledger was ready
            OnRestorableChanged();
            if (_restorable.Count > 0)
                _notifier?.Show("Pick up where you left off",
                    _restorable.Count == 1
                        ? "A Perch session was open when Perch closed. Reopen it from the tray menu."
                        : $"{_restorable.Count} Perch sessions were open when Perch closed. Reopen them from the tray menu.",
                    ToastLevel.Info, null, null);

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
    /// dormant after a restart unless the user releases it first.</summary>
    private void HoldSession(Services.PerchSession session)
    {
        if (!_heldSessions.ContainsKey(session))
        {
            _heldSessions[session] = session.SessionId;
            session.TitleChanged += () => TrackHeld(session);
            // /clear starts a new id under the same process: move the ledger entry with it.
            session.Conversation.StateChanged += () =>
            {
                if (!_heldSessions.TryGetValue(session, out var last) || last == session.SessionId) return;
                _heldSessions[session] = session.SessionId;
                if (last is not null && session.SessionId is { } now) _ledger?.Rebind(last, now);
                SaveLedgerSoon();
            };
            session.Released += ReleaseHeld;
            // A process that ended (Perch exiting, or a crash) stays in the ledger — that's what comes back.
            session.Ended += s => _heldSessions.Remove(s);
        }
        TrackHeld(session);
    }

    private void TrackHeld(Services.PerchSession session)
    {
        if (_ledger is not { } ledger || session.SessionId is not { } id) return;
        ledger.Track(new LedgerSession(id, session.Cwd, session.ConfigDir, session.Title));
        DropRestorable(id);
        SaveLedgerSoon();
    }

    // The user ended it or handed it back to a terminal: it isn't Perch's to bring back.
    private void ReleaseHeld(Services.PerchSession session)
    {
        _heldSessions.Remove(session);
        if (session.SessionId is not { } id) return;
        _ledger?.Forget(id);
        DropRestorable(id);
        SaveLedgerSoon();
    }

    // ── Restorable sessions ───────────────────────────────────────────────────────

    /// <summary>Opens every restorable session, dormant, each in its own window. They stay in the ledger until they're
    /// woken (then held as live) or released, so ignoring a reopened one just brings it back next time.</summary>
    private void ReopenRestorable()
    {
        var toOpen = _restorable.ToList();
        _restorable = [];
        OnRestorableChanged();
        foreach (var s in toOpen) OpenSessionResume(s.SessionId, s.Cwd);
    }

    private void DropRestorable(string sessionId)
    {
        if (_restorable.RemoveAll(s => s.SessionId == sessionId) > 0) OnRestorableChanged();
    }

    private void OnRestorableChanged()
    {
        if (_reopenItem is not { } item) return;
        item.Header = _restorable.Count == 1 ? "Reopen Perch session" : $"Reopen Perch sessions ({_restorable.Count})";
        item.IsVisible = _restorable.Count > 0;
    }

    /// <summary>The tray's "Reopen Perch sessions" item, hidden until there's something to reopen.</summary>
    private NativeMenuItem BuildReopenItem()
    {
        _reopenItem = new NativeMenuItem("Reopen Perch sessions") { IsVisible = false };
        _reopenItem.Click += (_, _) => ReopenRestorable();
        OnRestorableChanged();
        return _reopenItem;
    }

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
        _ledgerHeartbeat?.Stop();
        if (_ledgerShutdownStamped || _ledger is not { } ledger) return;
        ledger.MarkCleanExit(DateTime.Now);
        SaveLedger(ledger, SessionLedger.DefaultPath);
    }

    // Saves are rare (a session starting, renaming or ending, and the heartbeat), so each one simply goes off the UI
    // thread; the gate makes them serial, and each serialises the ledger inside it, so a later save always writes the
    // later state.
    private void SaveLedgerSoon()
    {
        if (_ledger is not { } ledger) return;
        Task.Run(() => SaveLedger(ledger, SessionLedger.DefaultPath));
    }

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
