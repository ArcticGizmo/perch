using System.Security.Cryptography.X509Certificates;
using Avalonia.Threading;
using Perch.Remote;

namespace Perch.Avalonia.Services;

/// <summary>
/// This machine's connections to the workers it paired with (docs/remote-workers-plan.md, W4): one per worker, kept up
/// while Perch runs, retried with a growing delay when the worker can't be reached. A worker that drops keeps its last
/// session list, so its rows grey out rather than vanish, and come back when it reconnects.
/// <para>Lives on the UI thread; raises <see cref="Changed"/> there.</para>
/// </summary>
internal sealed class WorkerLinks : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    private readonly Func<X509Certificate2?> _identity;
    private readonly Func<string> _deviceName;
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private bool _disposed;

    public WorkerLinks(Func<X509Certificate2?> identity, Func<string> deviceName)
    {
        _identity = identity;
        _deviceName = deviceName;
    }

    /// <summary>A worker connected or dropped, or its sessions changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<Worker> All => [.. _workers.Values.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)];

    public Worker? ByPin(string pin) => _workers.GetValueOrDefault(pin);

    /// <summary>The worker offering the session with this handle, and the session as it last listed it.</summary>
    public (Worker Worker, WorkerSessionInfo Info)? FindSession(string sid)
    {
        foreach (var w in _workers.Values)
            if (w.Sessions.FirstOrDefault(s => s.Sid == sid) is { } info) return (w, info);
        return null;
    }

    /// <summary>Follows the paired-worker list: connects to new ones, drops removed ones, picks up a changed address.</summary>
    public void Sync(IEnumerable<PairedDevice> paired)
    {
        if (_disposed) return;
        var wanted = paired.Where(d => d.Role == PairedRole.Worker).ToDictionary(d => d.Pin, StringComparer.Ordinal);
        bool changed = false;
        foreach (var pin in _workers.Keys.Where(p => !wanted.ContainsKey(p)).ToList())
        {
            _workers.Remove(pin, out var gone);
            gone!.Shut();
            changed = true;
        }
        foreach (var (pin, device) in wanted)
        {
            if (_workers.TryGetValue(pin, out var w))
            {
                if (w.Device.Host == device.Host && w.Device.Port == device.Port) { w.Device = device; continue; }
                w.Shut();   // re-paired at another address: start over there
                _workers.Remove(pin);
            }
            var worker = new Worker(device);
            _workers[pin] = worker;
            Connect(worker);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Tries an offline worker again now, rather than waiting for its next retry.</summary>
    public void Retry(Worker worker)
    {
        worker.CancelRetry();
        Connect(worker);
    }

    private async void Connect(Worker w)
    {
        if (_disposed || w.Connecting || w.Online || !_workers.ContainsValue(w)) return;
        if (_identity() is not { } identity)
        {
            w.Problem = "this machine has no key";
            Changed?.Invoke();
            return;
        }
        w.Connecting = true;
        w.Cancel = new CancellationTokenSource();
        Changed?.Invoke();
        try
        {
            var link = await WorkerClient.ConnectAsync(w.Device, identity, _deviceName(), w.Cancel.Token);
            if (_disposed || !_workers.ContainsValue(w))
            {
                link.Close();
                return;
            }
            var connection = new WorkerConnection(link);
            connection.SessionsChanged += () => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(w.Connection, connection)) return;
                w.LastSessions = connection.Sessions;
                Changed?.Invoke();
            });
            connection.Closed += reason => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(w.Connection, connection)) return;
                w.Connection = null;
                w.Problem = reason ?? "the connection closed";
                ScheduleRetry(w);
                Changed?.Invoke();
            });
            w.Connection = connection;
            w.Failures = 0;
            w.Problem = null;
            connection.Start();
        }
        catch (RemoteLinkException ex)
        {
            w.Problem = ex.Message;
            ScheduleRetry(w);
        }
        catch (OperationCanceledException)
        {
            // Shut while connecting.
        }
        finally
        {
            w.Connecting = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    private void ScheduleRetry(Worker w)
    {
        if (_disposed || !_workers.ContainsValue(w)) return;
        var delay = RetryDelays[Math.Min(w.Failures, RetryDelays.Length - 1)];
        w.Failures++;
        w.CancelRetry();
        w.RetryAtUtc = DateTime.UtcNow + delay;
        w.RetryTimer = new DispatcherTimer { Interval = delay };
        w.RetryTimer.Tick += (_, _) =>
        {
            w.CancelRetry();
            Connect(w);
        };
        w.RetryTimer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _workers.Values) w.Shut();
        _workers.Clear();
    }

    /// <summary>One paired worker and the state of this machine's link to it.</summary>
    internal sealed class Worker(PairedDevice device)
    {
        public PairedDevice Device { get; set; } = device;
        public WorkerConnection? Connection { get; set; }

        /// <summary>The name it gave in its hello, else the name it had when paired.</summary>
        public string Name => Connection?.WorkerName is { Length: > 0 } n ? n : Device.Name;

        public bool Online => Connection is { IsConnected: true };
        public bool Connecting { get; set; }

        /// <summary>Why it's offline, for Settings; null while connected or before the first try.</summary>
        public string? Problem { get; set; }

        /// <summary>Its sessions as last listed, kept while it's offline.</summary>
        public IReadOnlyList<WorkerSessionInfo> Sessions => LastSessions;

        internal IReadOnlyList<WorkerSessionInfo> LastSessions { get; set; } = [];
        internal int Failures { get; set; }
        internal DateTime RetryAtUtc { get; set; }
        internal DispatcherTimer? RetryTimer { get; set; }
        internal CancellationTokenSource? Cancel { get; set; }

        internal void CancelRetry()
        {
            RetryTimer?.Stop();
            RetryTimer = null;
        }

        internal void Shut()
        {
            CancelRetry();
            Cancel?.Cancel();
            var connection = Connection;
            Connection = null;
            connection?.Dispose();
        }
    }
}
