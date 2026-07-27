using Avalonia.Threading;
using Perch.Data.Hypertree;

namespace Perch.Avalonia.Services;

/// <summary>
/// Feeds the overlay's Hypertree strip: watches Hypertree's published <c>status.json</c> and pushes each
/// changed snapshot to a callback (the canvas's <c>SetHypertree</c>).
/// </summary>
/// <remarks>
/// <para><b>Two clocks, because there are two different questions.</b> "Where is the cursor now?" is
/// answered by the file changing, so a <see cref="FileSystemWatcher"/> answers it as fast as it can be
/// asked. "Is Hypertree still alive?" is <em>not</em> visible in the file at all — a killed tray leaves
/// its file behind untouched, and only the liveness check on the pid inside it can tell. Driving both
/// from one poll meant choosing an interval that was simultaneously too slow for the first question and
/// far too fast for the second; splitting them lets each run at its own natural rate.</para>
///
/// <para>The watcher is also what makes a jump feel immediate. Hypertree debounces its writes by ~120ms
/// and a jump round-trips in ~85ms, so the confirming snapshot lands about a fifth of a second after the
/// click — against the up-to-a-second wait a one-second poll imposed. (The canvas moves its marker
/// optimistically on click regardless; this is what makes the correction invisible.)</para>
///
/// <para>Reads run off the UI thread and marshal back, per the house idiom. Nothing is watched, read or
/// spawned until <see cref="Start"/>, which only happens while the setting is on.</para>
/// </remarks>
internal sealed class HypertreeMonitorHost : IDisposable
{
    /// <summary>How often to re-read purely to notice the tray has died, and to pick up the state
    /// directory appearing after we started (Hypertree installed or first run mid-session).</summary>
    private static readonly TimeSpan LivenessInterval = TimeSpan.FromSeconds(5);

    /// <summary>Trailing coalesce over a watcher burst. Hypertree writes atomically (temp file + replace),
    /// which raises several events for one logical update.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(30);

    private readonly Action<HypertreeStatus?> _onStatus;
    private readonly DispatcherTimer _liveness;
    private readonly DispatcherTimer _settle;
    private FileSystemWatcher? _watcher;
    private bool _reading;      // one read in flight at a time — a slow disk must not queue reads up
    private bool _disposed;
    private string _signature = " "; // deliberately not "" so a first null result still publishes

    /// <summary>The most recent snapshot, or null when no Hypertree tray is running.</summary>
    public HypertreeStatus? Last { get; private set; }

    public HypertreeMonitorHost(Action<HypertreeStatus?> onStatus)
    {
        _onStatus = onStatus;
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

    /// <summary>Stops watching and clears the strip, so turning the setting off empties it at once.</summary>
    public void Stop()
    {
        if (!_liveness.IsEnabled) return;
        _liveness.Stop();
        _settle.Stop();
        DisposeWatcher();
        _signature = " ";
        Last = null;
        _onStatus(null);
    }

    // Attaches the watcher once the state directory exists. It may well not: Hypertree isn't installed,
    // or has never run. The liveness timer retries, so installing Hypertree mid-session is picked up
    // without a Perch restart.
    private void EnsureWatcher()
    {
        if (_disposed || _watcher is not null) return;

        var dir = HypertreeStatusReader.Directory;
        if (!Directory.Exists(dir)) return;

        try
        {
            var watcher = new FileSystemWatcher(dir, HypertreeStatusReader.FileName)
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

        Task.Run(HypertreeStatusReader.Read).ContinueWith(t =>
        {
            _reading = false;
            if (_disposed || !_liveness.IsEnabled) return; // stopped mid-flight — don't resurrect the strip

            var status = t.IsCompletedSuccessfully ? t.Result : null;

            // Repaint only on a real change. The liveness tick alone would otherwise invalidate the
            // overlay every few seconds for a strip that hasn't moved.
            var signature = Signature(status);
            if (signature == _signature) return;
            _signature = signature;
            Last = status;
            _onStatus(status);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // Everything the strip draws, and nothing else — so a change we don't render can't cost a repaint.
    private static string Signature(HypertreeStatus? s)
    {
        if (s is null) return "";
        var parts = s.Rows.Select(r => $"{r.Target}|{r.Name}|{r.Desktops.Count}|{r.ResumeLabel}");
        return $"{s.Current.Row}:{s.Current.Desktop}/{string.Join(";", parts)}";
    }

    public void Dispose()
    {
        _disposed = true;
        _liveness.Stop();
        _settle.Stop();
        DisposeWatcher();
    }
}
