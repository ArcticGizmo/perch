using Avalonia.Threading;
using Perch.Data;

namespace Perch.Avalonia.Services;

/// <summary>
/// Feeds the overlay's "daemon" section: watches the Claude Code background daemon's worker roster
/// (<c>~/.claude/daemon/roster.json</c>) and pushes each changed list to a callback (the canvas's
/// <c>SetDaemonWorkers</c>).
/// </summary>
/// <remarks>
/// Same two-clock shape as <see cref="HypertreeMonitorHost"/>, for the same two questions. "What is in
/// the roster now?" is answered by the file changing, so a <see cref="FileSystemWatcher"/> answers it
/// immediately. "Are the workers still alive?" is not visible in the file — a daemon killed outright
/// leaves its roster behind untouched, and only the per-worker pid probe inside
/// <see cref="DaemonRosterReader.Read"/> can tell — so a slow liveness poll re-reads regardless. The
/// poll also picks up the daemon directory appearing after startup (first background dispatch of the
/// session). Reads run off the UI thread and marshal back, per the house idiom.
/// </remarks>
internal sealed class DaemonMonitorHost : IDisposable
{
    /// <summary>How often to re-read purely to notice dead worker pids (the roster file doesn't change
    /// when the daemon is killed), and to pick up the daemon directory appearing after we started.</summary>
    private static readonly TimeSpan LivenessInterval = TimeSpan.FromSeconds(5);

    /// <summary>Trailing coalesce over a watcher burst — one logical roster rewrite raises several events.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(30);

    private const string RosterFileName = "roster.json";

    private readonly Action<IReadOnlyList<DaemonWorker>> _onWorkers;
    private readonly DispatcherTimer _liveness;
    private readonly DispatcherTimer _settle;
    private FileSystemWatcher? _watcher;
    private bool _reading;      // one read in flight at a time — a slow disk must not queue reads up
    private bool _disposed;
    private string _signature = " "; // deliberately not "" so a first empty result still publishes

    public DaemonMonitorHost(Action<IReadOnlyList<DaemonWorker>> onWorkers)
    {
        _onWorkers = onWorkers;
        _liveness = new DispatcherTimer { Interval = LivenessInterval };
        _liveness.Tick += (_, _) => { EnsureWatcher(); Read(); };
        _settle = new DispatcherTimer { Interval = Settle };
        _settle.Tick += (_, _) => { _settle.Stop(); Read(); };
    }

    /// <summary>Starts watching and takes a first reading. Idempotent; call on the UI thread.</summary>
    public void Start()
    {
        if (_disposed || _liveness.IsEnabled) return;
        _liveness.Start();
        EnsureWatcher();
        Read();
    }

    /// <summary>Stops watching and publishes an empty roster, so turning the setting off empties the
    /// overlay's daemon section at once (and returns any deduped sessions to the normal rows).</summary>
    public void Stop()
    {
        if (!_liveness.IsEnabled) return;
        _liveness.Stop();
        _settle.Stop();
        DisposeWatcher();
        _signature = " ";
        _onWorkers([]);
    }

    // Attaches the watcher once the daemon directory exists. It may well not: the daemon starts on the
    // first background dispatch of a Claude Code session. The liveness timer retries, so a daemon
    // appearing mid-session is picked up without a Perch restart.
    private void EnsureWatcher()
    {
        if (_disposed || _watcher is not null) return;

        var dir = DaemonRosterReader.Directory;
        if (!System.IO.Directory.Exists(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir, RosterFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // These fire on a watcher thread; hop to the UI thread before touching the settle timer.
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            // A watcher that dies (the directory is removed out from under it) must not wedge the strip:
            // drop it and let the liveness tick re-attach.
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(DisposeWatcher);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // No rights, or the directory vanished between the check and the attach. The liveness poll
            // still covers correctness; we simply lose the fast path this tick.
            _watcher = null;
        }
    }

    private void DisposeWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { /* already torn down */ }
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || !_liveness.IsEnabled) return;
        _settle.Stop();   // restart the window — trailing debounce, not a fixed-rate flush
        _settle.Start();
    });

    private void Read()
    {
        if (_reading || _disposed) return;
        _reading = true;

        Task.Run(() => DaemonRosterReader.Read()).ContinueWith(t =>
        {
            _reading = false;
            if (_disposed || !_liveness.IsEnabled) return;

            var workers = t.IsCompletedSuccessfully ? t.Result : [];

            // Publish only on a real change, so the liveness tick doesn't repaint an unchanged strip.
            var signature = Signature(workers);
            if (signature == _signature) return;
            _signature = signature;
            _onWorkers(workers);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Everything the strip draws (and the dedupe key), and nothing else.
    private static string Signature(IReadOnlyList<DaemonWorker> workers) =>
        string.Join(";", workers.Select(w => $"{w.SessionId}|{w.Pid}|{w.DisplayName}|{w.Source}"));

    public void Dispose()
    {
        _disposed = true;
        _liveness.Stop();
        _settle.Stop();
        DisposeWatcher();
    }
}
