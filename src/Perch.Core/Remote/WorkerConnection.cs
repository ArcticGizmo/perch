using System.Text.Json.Nodes;

namespace Perch.Remote;

/// <summary>
/// The controller's side of the session protocol for one connected worker (docs/remote-workers-plan.md, W3): the
/// sessions it offers, and the <see cref="RemoteSessionBackend"/>s attached to them, each fed the frames for its own
/// session. Events are raised on the link's receive thread.
/// </summary>
internal sealed class WorkerConnection : IDisposable
{
    private readonly RemoteLink _link;
    private readonly Lock _lock = new();
    private IReadOnlyList<WorkerSessionInfo> _sessions = [];
    private readonly Dictionary<string, RemoteSessionBackend> _attached = new(StringComparer.Ordinal);

    /// <param name="link">A link that has exchanged hellos (<see cref="WorkerClient.ConnectAsync"/>) and isn't receiving yet.</param>
    public WorkerConnection(RemoteLink link) => _link = link;

    public string WorkerName => _link.PeerName;
    public bool IsConnected => !_link.IsClosed;

    /// <summary>The worker's live sessions, as it last listed them.</summary>
    public IReadOnlyList<WorkerSessionInfo> Sessions { get { lock (_lock) return _sessions; } }

    public event Action? SessionsChanged;

    /// <summary>The worker refused something not tied to one session.</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>The connection ended; the argument says why when it wasn't a clean close.</summary>
    public event Action<string?>? Closed;

    /// <summary>Starts receiving and asks for the session list.</summary>
    public void Start()
    {
        _link.FrameReceived += OnFrame;
        _link.Closed += OnClosed;
        _link.StartReceiving();
        _link.Send(RemoteLink.Frame("list"));
    }

    /// <summary>A backend for one of the worker's sessions. Nothing arrives until its <see cref="RemoteSessionBackend.Start"/>,
    /// so its consumer can wire up first; then it catches up from the worker's journal and streams. A session already
    /// attached here returns its backend.</summary>
    public RemoteSessionBackend Attach(WorkerSessionInfo info)
    {
        lock (_lock)
        {
            if (_attached.TryGetValue(info.Sid, out var existing) && existing.IsRunning) return existing;
            var backend = new RemoteSessionBackend(this, info);
            _attached[info.Sid] = backend;
            return backend;
        }
    }

    internal void Detach(RemoteSessionBackend backend)
    {
        lock (_lock)
        {
            if (!_attached.TryGetValue(backend.Sid, out var current) || !ReferenceEquals(current, backend)) return;
            _attached.Remove(backend.Sid);
        }
        _link.Send(WorkerProtocol.Frame("detach", backend.Sid));
    }

    internal bool Send(JsonObject frame) => _link.TrySend(frame);

    private void OnFrame(JsonObject frame)
    {
        var type = RemoteLink.Str(frame, "t");
        if (type == "sessions")
        {
            var sessions = WorkerProtocol.ParseSessions(frame);
            lock (_lock) _sessions = sessions;
            SessionsChanged?.Invoke();
            return;
        }
        var sid = RemoteLink.Str(frame, "sid");
        RemoteSessionBackend? backend = null;
        if (sid is not null)
            lock (_lock) _attached.TryGetValue(sid, out backend);
        switch (type)
        {
            case "history":
                backend?.OnHistory(frame);
                break;
            case "line":
                if (RemoteLink.Str(frame, "raw") is { } raw) backend?.OnLine(raw);
                break;
            case "ended":
                if (backend is not null)
                {
                    Forget(backend);
                    backend.OnEnded(WorkerProtocol.Int(frame, "exitCode") ?? -1);
                }
                break;
            case "error":
                var msg = CleanMessage(RemoteLink.Str(frame, "msg"));
                if (backend is not null) backend.OnError(msg);
                else ErrorReceived?.Invoke(msg);
                break;
        }
    }

    private void Forget(RemoteSessionBackend backend)
    {
        lock (_lock)
            if (_attached.TryGetValue(backend.Sid, out var current) && ReferenceEquals(current, backend))
                _attached.Remove(backend.Sid);
    }

    private void OnClosed(string? reason)
    {
        RemoteSessionBackend[] backends;
        lock (_lock)
        {
            backends = [.. _attached.Values];
            _attached.Clear();
        }
        foreach (var b in backends) b.OnDisconnected(reason);
        Closed?.Invoke(reason);
    }

    // The worker's words, shown to the user: printable characters only, and short.
    private static string CleanMessage(string? msg)
    {
        var clean = new string((msg ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return "the worker refused that";
        return clean.Length > 200 ? clean[..200] : clean;
    }

    public void Dispose() => _link.Close();
}
