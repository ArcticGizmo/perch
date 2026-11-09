using System.Text.Json.Nodes;
using Perch.Data.Control;

namespace Perch.Remote;

/// <summary>
/// A session hosted by another Perch, driven over the network (docs/remote-workers-plan.md, W3). It stands where a
/// <see cref="ClaudeSessionController"/> would: the worker relays the CLI's raw stream-json lines, decoded here by the
/// same <see cref="StreamJsonParser"/>, and each command becomes a frame. The catch-up arrives first: the transcript the
/// worker loaded on resume (as a <see cref="TranscriptHistoryEvent"/>), then the stream lines since.
/// <para>Stopping it detaches this view only; the session keeps running on the worker.</para>
/// <para>Events are raised on the link's receive thread (or the caller's, for a local stop).</para>
/// </summary>
internal sealed class RemoteSessionBackend : ISessionBackend
{
    private readonly WorkerConnection _connection;
    private List<string>? _transcript;
    private List<string>? _lines;
    private bool _truncated;
    private int _ended;

    internal RemoteSessionBackend(WorkerConnection connection, WorkerSessionInfo info)
    {
        _connection = connection;
        Sid = info.Sid;
        SessionId = info.SessionId;
    }

    public event Action<SessionEvent>? EventReceived;
    public event Action<int, string>? Exited;

    /// <summary>The worker's handle for the session.</summary>
    public string Sid { get; }
    public string WorkerName => _connection.WorkerName;

    /// <summary>Claude's session id, followed through a <c>/clear</c> as the stream reports it.</summary>
    public string? SessionId { get; private set; }

    public bool IsRunning => Volatile.Read(ref _ended) == 0 && _connection.IsConnected;
    public bool IsRemote => true;

    public void SendPrompt(string text, IReadOnlyList<ImageContent>? images, string? uuid = null)
    {
        if (!Guid.TryParse(uuid, out _)) uuid = Guid.NewGuid().ToString();
        if (Send(WorkerProtocol.PromptFrame(Sid, uuid!, text, images))) return;
        Note(images is { Count: > 0 }
            ? $"couldn't send that to {WorkerName}: the images are too large to send over the link together"
            : $"couldn't send that to {WorkerName}");
    }

    public void RespondToPermission(PermissionRequestEvent request, bool allow, string? denyMessage = null, JsonNode? updatedInput = null)
    {
        var frame = WorkerProtocol.Frame("answer", Sid);
        frame["requestId"] = request.RequestId;
        frame["allow"] = allow;
        if (denyMessage is not null) frame["denyMessage"] = denyMessage;
        if (updatedInput is not null) frame["updatedInput"] = updatedInput.DeepClone();
        Send(frame);
    }

    public void SetPermissionMode(string mode) => Send(With("mode", "mode", mode));
    public void SetModel(string model) => Send(With("model", "model", model));
    public void SetEffort(string level) => Send(With("effort", "level", level));
    public void Interrupt() => Send(WorkerProtocol.Frame("interrupt", Sid));
    public void StopTask(string taskId) => Send(With("stopTask", "taskId", taskId));

    /// <summary>Asks the worker for the session: its catch-up, then its live stream. Call once the events are wired.</summary>
    public void Start() => Send(WorkerProtocol.Frame("attach", Sid));

    /// <summary>Stops following the session here. It keeps running on the worker.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _connection.Detach(this);
        Exited?.Invoke(0, "");
    }

    public void Dispose() => Stop();

    // ── From the worker (receive thread) ────────────────────────────────────────

    internal void OnHistory(JsonObject frame)
    {
        _transcript ??= [];
        _lines ??= [];
        Collect(frame["transcript"], _transcript);
        Collect(frame["lines"], _lines);
        _truncated |= WorkerProtocol.Bool(frame, "truncated");
        if (!WorkerProtocol.Bool(frame, "done")) return;

        var (transcript, lines, truncated) = (_transcript, _lines, _truncated);
        _transcript = null;
        _lines = null;
        _truncated = false;
        if (transcript.Count > 0 || truncated) Raise(new TranscriptHistoryEvent(transcript, truncated));
        foreach (var line in lines) OnLine(line);

        static void Collect(JsonNode? node, List<string> into)
        {
            if (node is not JsonArray list) return;
            foreach (var item in list)
                if (item is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    into.Add(v.GetValue<string>());
        }
    }

    internal void OnLine(string raw)
    {
        if (Volatile.Read(ref _ended) != 0) return;
        foreach (var ev in StreamJsonParser.Parse(raw))
        {
            if (ev is SessionInitEvent { SessionId.Length: > 0 } init) SessionId = init.SessionId;
            Raise(ev);
        }
    }

    internal void OnEnded(int exitCode)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        Exited?.Invoke(exitCode, "");
    }

    internal void OnDisconnected(string? reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        Exited?.Invoke(-1, reason is { Length: > 0 } ? $"lost the connection to {WorkerName}: {reason}"
                                                     : $"lost the connection to {WorkerName}");
    }

    internal void OnError(string message) => Note($"{WorkerName}: {message}");

    private void Raise(SessionEvent ev) => EventReceived?.Invoke(ev);

    private void Note(string text) => Raise(new BackendNoteEvent(text, IsError: true));

    private bool Send(JsonObject frame) => IsRunning && _connection.Send(frame);

    private JsonObject With(string type, string key, string value)
    {
        var frame = WorkerProtocol.Frame(type, Sid);
        frame[key] = value;
        return frame;
    }
}
