using System.Text.Json.Nodes;
using Perch.Data.Control;

namespace Perch.Remote;

/// <summary>
/// One live session as the worker offers it to controllers. The app implements it over its own session object; the
/// commands are called on a link's receive thread, so an implementation marshals to wherever its session lives.
/// </summary>
internal interface IWorkerSession
{
    /// <summary>The worker's handle for this session, fixed for its life.</summary>
    string Sid { get; }
    SessionJournal Journal { get; }

    /// <summary>A prompt from a controller, keyed by its <paramref name="uuid"/> so every view shows it once.</summary>
    void SendPrompt(string text, IReadOnlyList<ImageContent>? images, string uuid, string fromName);

    /// <summary>Answers the pending permission card <paramref name="requestId"/>, if it is still pending.
    /// <paramref name="updatedInput"/> is honoured only for an AskUserQuestion card (its answers).</summary>
    void Answer(string requestId, bool allow, string? denyMessage, JsonNode? updatedInput, string fromName);

    void Interrupt(string fromName);
    void SetPermissionMode(string mode);
    void SetModel(string model);
    void SetEffort(string level);
    void StopTask(string taskId);
}

/// <summary>
/// The worker's side of the session protocol (docs/remote-workers-plan.md, W3; frames in <see cref="WorkerProtocol"/>):
/// serves every connected controller the live sessions the app offers, streams the ones it attaches to, and turns its
/// commands into calls on those sessions. Only what the session window itself can do is reachable: no files, no
/// launches, no restarts.
/// </summary>
internal sealed class WorkerSessionHub
{
    private readonly Lock _lock = new();
    private IReadOnlyList<(WorkerSessionInfo Info, IWorkerSession Session)> _sessions = [];
    private readonly List<Controller> _controllers = [];

    internal const string BypassMode = "bypassPermissions";

    /// <summary>The image limit a remote prompt gets: the same as a local one.</summary>
    public long MaxImageBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>A controller connected or left (raised on the link's thread).</summary>
    public event Action? ControllersChanged;

    /// <summary>The names of the controllers connected now, one per connection.</summary>
    public IReadOnlyList<string> ConnectedNames()
    {
        lock (_lock) return [.. _controllers.Select(c => c.Link.PeerName)];
    }

    /// <summary>The sessions on offer changed (any thread). Every controller is sent the new list; one attached to a
    /// session that's gone has already been told it <c>ended</c>.</summary>
    public void SetSessions(IReadOnlyList<(WorkerSessionInfo Info, IWorkerSession Session)> sessions)
    {
        Controller[] controllers;
        lock (_lock)
        {
            _sessions = sessions;
            controllers = [.. _controllers];
        }
        var frame = WorkerProtocol.SessionsFrame(sessions.Select(s => s.Info));
        foreach (var c in controllers) c.Link.Send(frame.DeepClone().AsObject());
    }

    /// <summary>Serves a controller that connected and said hello: wires its frames and starts receiving.</summary>
    public void Serve(RemoteLink link)
    {
        var controller = new Controller(this, link);
        lock (_lock) _controllers.Add(controller);
        link.FrameReceived += controller.OnFrame;
        link.Closed += _ =>
        {
            controller.DetachAll();
            lock (_lock) _controllers.Remove(controller);
            ControllersChanged?.Invoke();
        };
        link.StartReceiving();
        ControllersChanged?.Invoke();
    }

    /// <summary>Drops every controller (the user's "disconnect remote controllers").</summary>
    public void DisconnectAll()
    {
        Controller[] controllers;
        lock (_lock) controllers = [.. _controllers];
        foreach (var c in controllers) c.Link.Close();
    }

    /// <summary>Drops the connections of the controller with this pinned key (it was just unpaired).</summary>
    public void Disconnect(string pin)
    {
        Controller[] controllers;
        lock (_lock) controllers = [.. _controllers.Where(c => c.Link.PeerPin == pin)];
        foreach (var c in controllers) c.Link.Close();
    }

    private IWorkerSession? Find(string? sid)
    {
        if (sid is null) return null;
        lock (_lock) return _sessions.FirstOrDefault(s => s.Session.Sid == sid).Session;
    }

    private IEnumerable<WorkerSessionInfo> Infos()
    {
        lock (_lock) return [.. _sessions.Select(s => s.Info)];
    }

    // One connected controller: its link and the sessions it follows.
    private sealed class Controller(WorkerSessionHub hub, RemoteLink link)
    {
        private readonly Dictionary<string, IDisposable> _attached = new(StringComparer.Ordinal);

        public RemoteLink Link { get; } = link;

        public void OnFrame(JsonObject frame)
        {
            var type = RemoteLink.Str(frame, "t");
            if (type == "list")
            {
                Link.Send(WorkerProtocol.SessionsFrame(hub.Infos()));
                return;
            }
            var sid = RemoteLink.Str(frame, "sid");
            if (type == "detach")
            {
                if (sid is not null) Detach(sid);
                return;
            }
            if (type is not ("attach" or "prompt" or "answer" or "interrupt" or "mode" or "model" or "effort" or "stopTask"))
                return;   // a newer controller's frame this worker doesn't know
            if (hub.Find(sid) is not { } session)
            {
                Error(sid, "that session isn't running on this machine any more");
                return;
            }
            try
            {
                Handle(type, frame, session);
            }
            catch (InvalidDataException ex)
            {
                Error(sid, ex.Message);
            }
        }

        private void Handle(string type, JsonObject frame, IWorkerSession session)
        {
            var name = Link.PeerName;
            switch (type)
            {
                case "attach":
                    Attach(session);
                    break;
                case "prompt":
                {
                    var uuid = RemoteLink.Str(frame, "uuid");
                    if (!Guid.TryParse(uuid, out _)) throw new InvalidDataException("prompt has no id");
                    var text = RemoteLink.Str(frame, "text") ?? "";
                    var images = WorkerProtocol.ParseImages(frame, hub.MaxImageBytes);
                    if (text.Length == 0 && images is null) return;
                    session.SendPrompt(text, images, uuid!, name);
                    break;
                }
                case "answer":
                {
                    if (RemoteLink.Str(frame, "requestId") is not { Length: > 0 } requestId) throw new InvalidDataException("answer has no request");
                    var deny = RemoteLink.Str(frame, "denyMessage");
                    if (deny is { Length: > WorkerProtocol.MaxDenyMessageChars }) deny = deny[..WorkerProtocol.MaxDenyMessageChars];
                    session.Answer(requestId, WorkerProtocol.Bool(frame, "allow"), deny, frame["updatedInput"]?.DeepClone(), name);
                    break;
                }
                case "interrupt":
                    session.Interrupt(name);
                    break;
                case "mode":
                {
                    var mode = RemoteLink.Str(frame, "mode") ?? "";
                    // Turning every check off is a decision for someone at the worker, not one a paired machine can make.
                    if (mode == BypassMode) throw new InvalidDataException("bypass permissions can only be turned on at the machine itself");
                    if (!SessionOpenIntent.PermissionModes.Contains(mode)) throw new InvalidDataException("unknown permission mode");
                    session.SetPermissionMode(mode);
                    break;
                }
                case "model":
                    session.SetModel(Token(RemoteLink.Str(frame, "model"), "model"));
                    break;
                case "effort":
                {
                    var level = RemoteLink.Str(frame, "level") ?? "";
                    if (level != "auto" && !ClaudeSessionController.IsEffortLevel(level)) throw new InvalidDataException("unknown effort level");
                    session.SetEffort(level);
                    break;
                }
                case "stopTask":
                    session.StopTask(Token(RemoteLink.Str(frame, "taskId"), "task"));
                    break;
            }
        }

        // A bare identifier (a model name, a task id): what the CLI accepts, and nothing that reads as anything else.
        private static string Token(string? value, string what)
        {
            if (value is not { Length: > 0 and <= 128 } || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
                throw new InvalidDataException($"invalid {what} name");
            return value;
        }

        private void Attach(IWorkerSession session)
        {
            lock (_attached)
            {
                if (_attached.ContainsKey(session.Sid)) return;
                var listener = new Follower(Link, session.Sid);
                _attached[session.Sid] = session.Journal.Subscribe(snapshot =>
                {
                    foreach (var f in WorkerProtocol.HistoryFrames(session.Sid, snapshot.Transcript, snapshot.Lines, snapshot.Truncated))
                        Link.TrySend(f);
                    if (snapshot.ExitCode is { } code) listener.OnEnded(code);
                }, listener);
            }
        }

        private void Detach(string sid)
        {
            lock (_attached)
                if (_attached.Remove(sid, out var sub)) sub.Dispose();
        }

        public void DetachAll()
        {
            lock (_attached)
            {
                foreach (var sub in _attached.Values) sub.Dispose();
                _attached.Clear();
            }
        }

        private void Error(string? sid, string message)
        {
            var frame = RemoteLink.Frame("error");
            if (sid is not null) frame["sid"] = sid;
            frame["msg"] = message;
            Link.Send(frame);
        }
    }

    // Forwards one attached session's journal to one controller.
    private sealed class Follower(RemoteLink link, string sid) : IJournalListener
    {
        public void OnLine(string line)
        {
            var frame = WorkerProtocol.Frame("line", sid);
            frame["raw"] = line;
            link.TrySend(frame);   // a line too large for one frame is left out; its effects still arrive in later lines
        }

        public void OnTranscript(IReadOnlyList<string> lines, bool truncated)
        {
            foreach (var f in WorkerProtocol.HistoryFrames(sid, lines, [], truncated)) link.TrySend(f);
        }

        public void OnEnded(int exitCode)
        {
            var frame = WorkerProtocol.Frame("ended", sid);
            frame["exitCode"] = exitCode;
            link.Send(frame);
        }
    }
}
