using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia;

// Session recovery (docs/session-recovery-plan.md, R3): Perch's own ledger of its runs — when this run started, a
// heartbeat while it's alive, and how it ended (a clean Exit, or the OS shutting down). The next run reads it to find
// the shutdown that ended this one, which drives the Recent list's "just before shutdown" flag.
public partial class App
{
    private SessionLedger? _ledger;
    private DispatcherTimer? _ledgerHeartbeat;
    private bool _ledgerShutdownStamped;

    /// <summary>This run's ledger, once it has begun. Null under the headless render, tests and replay (which never
    /// write it), and for the first moment of a run while the previous shutdown is still being looked up.</summary>
    internal SessionLedger? Ledger => _ledger;

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
            _ledger = t.Result;
            _ledgerHeartbeat = new DispatcherTimer { Interval = SessionRecovery.HeartbeatInterval };
            _ledgerHeartbeat.Tick += (_, _) =>
            {
                if (_ledger is not { } ledger) return;
                ledger.Heartbeat(DateTime.Now);
                Task.Run(() => SaveLedger(ledger, path));
            };
            _ledgerHeartbeat.Start();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The OS is shutting down or logging off (Avalonia's ShutdownRequested, which Perch's own Exit never
    /// raises). Written synchronously: the process may not get another chance. Windows only: on macOS Avalonia raises
    /// the same event for an ordinary Cmd+Q / Dock "Quit" (applicationShouldTerminate), so there it stays a plain exit
    /// until the port adds a real power-off signal (NSWorkspaceWillPowerOffNotification).</summary>
    private void StampLedgerShutdown()
    {
        if (!OperatingSystem.IsWindows()) return;
        _ledgerShutdownStamped = true;
        _ledgerHeartbeat?.Stop();
        if (_ledger is not { } ledger) return;
        ledger.MarkShutdown(DateTime.Now);
        SaveLedger(ledger, SessionLedger.DefaultPath);
    }

    /// <summary>Perch is exiting. A clean exit unless the OS shutdown was already stamped (that path ends here too).</summary>
    private void StampLedgerExit()
    {
        _ledgerHeartbeat?.Stop();
        if (_ledgerShutdownStamped || _ledger is not { } ledger) return;
        ledger.MarkCleanExit(DateTime.Now);
        SaveLedger(ledger, SessionLedger.DefaultPath);
    }

    private static void SaveLedger(SessionLedger ledger, string path)
    {
        try { ledger.Save(path); }
        catch (Exception ex) { LaunchLog.Write($"session ledger: couldn't save ({ex.GetType().Name})"); }
    }
}
