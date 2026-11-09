using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Perch.Data.Control;
using Perch.Remote;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Remote workers' session protocol (docs/remote-workers-plan.md, W3): the worker's journal, the catch-up frames, and a
/// controller driving a worker session end to end. The two ends talk over plain loopback TCP here; the TLS underneath is
/// covered by <see cref="RemoteWorkerTests"/>.
/// </summary>
public class RemoteSessionTests
{
    private const string Init = """{"type":"system","subtype":"init","session_id":"sess-1","model":"claude-opus-5-5","permissionMode":"default","tools":[]}""";
    private const string Partial = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}},"session_id":"sess-1","parent_tool_use_id":null}""";
    private const string SpacedPartial = """{"type": "stream_event", "event": {"type": "content_block_delta"}}""";
    private const string Answer = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Hello from the worker"}]},"session_id":"sess-1","parent_tool_use_id":null}""";
    private const string AskWrite = """{"type":"control_request","request_id":"req-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"C:\\t\\a.txt","content":"x"},"description":"a.txt","tool_use_id":"toolu_1"}}""";

    // ── The journal ─────────────────────────────────────────────────────────────

    [Fact]
    public void Journal_KeepsFinalRecords_ButNotPartials()
    {
        var journal = new SessionJournal(() => true);
        journal.Append(Init);
        journal.Append(Partial);
        journal.Append(SpacedPartial);
        journal.Append(Answer);

        JournalSnapshot? snap = null;
        journal.Subscribe(s => snap = s, new Recorder());
        Assert.Equal(new[] { Init, Answer }, snap!.Lines);
        Assert.False(snap.Truncated);
    }

    [Theory]
    [InlineData(Partial, true)]
    [InlineData(SpacedPartial, true)]
    [InlineData(Answer, false)]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"type stream_event"}]}}""", false)]
    [InlineData("not json stream_event", false)]
    public void IsPartial_ReadsTheTopLevelType(string line, bool partial) =>
        Assert.Equal(partial, SessionJournal.IsPartial(line));

    [Fact]
    public void Journal_Subscriber_GetsEachLineOnce_AfterTheSnapshot()
    {
        var journal = new SessionJournal(() => true);
        journal.Append(Init);
        var rec = new Recorder();
        JournalSnapshot? snap = null;
        var sub = journal.Subscribe(s => snap = s, rec);
        journal.Append(Partial);
        journal.Append(Answer);
        sub.Dispose();
        journal.Append(AskWrite);

        Assert.Equal(new[] { Init }, snap!.Lines);
        Assert.Equal(new[] { Partial, Answer }, rec.Lines);   // partials are relayed live, just not kept
    }

    [Fact]
    public void Journal_NotRecording_KeepsNothing_AndSaysTheStartIsMissing()
    {
        bool on = false;
        var journal = new SessionJournal(() => on);
        journal.Append(Init);
        journal.SetTranscript(["{}"], clipped: false);
        on = true;
        journal.Append(Answer);

        JournalSnapshot? snap = null;
        journal.Subscribe(s => snap = s, new Recorder());
        Assert.Empty(snap!.Transcript);
        Assert.Equal(new[] { Answer }, snap.Lines);
        Assert.True(snap.Truncated);
    }

    [Fact]
    public void Journal_OverTheCap_DropsTheOldest_AndSaysSo()
    {
        var journal = new SessionJournal(() => true);
        var line = new string('x', 1024 * 1024);
        journal.SetTranscript([line], clipped: false);
        for (int i = 0; i < 20; i++) journal.Append(line + i);

        JournalSnapshot? snap = null;
        journal.Subscribe(s => snap = s, new Recorder());
        Assert.Empty(snap!.Transcript);   // the oldest go first
        Assert.True(snap.Truncated);
        Assert.True(journal.CharCount <= SessionJournal.MaxChars);
        Assert.EndsWith("19", snap.Lines[^1]);
    }

    [Fact]
    public void Journal_AfterTheEnd_SnapshotCarriesTheExitCode()
    {
        var journal = new SessionJournal(() => true);
        journal.Append(Init);
        journal.End(3);
        var rec = new Recorder();
        JournalSnapshot? snap = null;
        journal.Subscribe(s => snap = s, rec);
        Assert.Equal(3, snap!.ExitCode);
    }

    // ── Catch-up frames ─────────────────────────────────────────────────────────

    [Fact]
    public void HistoryFrames_SplitLargeCatchUps_AndMarkOnlyTheLastDone()
    {
        var big = new string('y', WorkerProtocol.HistoryChunkChars / 2 + 1);
        var frames = WorkerProtocol.HistoryFrames("s1", [big, big], [big, "small"], truncated: true).ToList();

        Assert.Equal(3, frames.Count);   // [big] [big] | [big, small]: a chunk holds what fits, across the two lists
        Assert.All(frames[..^1], f => Assert.False(WorkerProtocol.Bool(f, "done")));
        Assert.True(WorkerProtocol.Bool(frames[^1], "done"));
        Assert.All(frames, f => Assert.True(WorkerProtocol.Bool(f, "truncated")));
        Assert.Equal(2, frames.Sum(f => ((JsonArray)f["transcript"]!).Count));
        Assert.Equal(2, frames.Sum(f => ((JsonArray)f["lines"]!).Count));
    }

    [Fact]
    public void HistoryFrames_Empty_IsOneDoneFrame()
    {
        var frames = WorkerProtocol.HistoryFrames("s1", [], [], truncated: false).ToList();
        Assert.Single(frames);
        Assert.True(WorkerProtocol.Bool(frames[0], "done"));
    }

    [Fact]
    public void Images_AreChecked()
    {
        var ok = WorkerProtocol.PromptFrame("s", Guid.NewGuid().ToString(), "t", [new ImageContent("image/png", "aGVsbG8=")]);
        Assert.Single(WorkerProtocol.ParseImages(ok, 100)!);

        var badType = WorkerProtocol.PromptFrame("s", "u", "t", [new ImageContent("text/html", "aGVsbG8=")]);
        Assert.Throws<InvalidDataException>(() => WorkerProtocol.ParseImages(badType, 100));
        var notBase64 = WorkerProtocol.PromptFrame("s", "u", "t", [new ImageContent("image/png", "!!!")]);
        Assert.Throws<InvalidDataException>(() => WorkerProtocol.ParseImages(notBase64, 100));
        Assert.Throws<InvalidDataException>(() => WorkerProtocol.ParseImages(ok, 3));   // "hello" is 5 bytes
    }

    [Fact]
    public void SessionList_RoundTrips_AndReadsAnUnknownStateAsIdle()
    {
        var sent = new WorkerSessionInfo("w1", "sess-1", "t", @"C:\p", "m", "plan", "high", true, WorkerProtocol.Waiting);
        var back = Assert.Single(WorkerProtocol.ParseSessions(WorkerProtocol.SessionsFrame([sent])));
        Assert.Equal(sent, back);

        var odd = WorkerProtocol.SessionsFrame([sent]);
        ((JsonObject)((JsonArray)odd["sessions"]!)[0]!)["state"] = "exploding";
        Assert.Equal(WorkerProtocol.Idle, WorkerProtocol.ParseSessions(odd)[0].State);
    }

    [Fact]
    public void RemoteRowKey_RoundTrips_AndIsNeverAPid()
    {
        var key = Perch.Data.ClaudeSession.RemoteKey("abc123");
        Assert.Equal("abc123", Perch.Data.ClaudeSession.RemoteSidOf(key));
        Assert.Null(Perch.Data.ClaudeSession.RemoteSidOf("12345"));
        Assert.Null(Perch.Data.ClaudeSession.RemoteSidOf("~dormant"));
        Assert.Null(Perch.Data.ClaudeSession.RemoteSidOf(null));
        Assert.False(int.TryParse(key, out _));
    }

    // ── End to end ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Controller_CatchesUp_ThenStreams_AndDrivesTheWorkerSession()
    {
        var session = new FakeSession("w-1");
        session.Journal.SetTranscript(["""{"type":"user","message":{"role":"user","content":"earlier question"},"uuid":"t1","timestamp":"2026-11-09T00:00:00Z"}"""], clipped: false);
        session.Journal.Append(Init);
        session.Journal.Append(Answer);
        session.Journal.Append(AskWrite);

        var hub = new WorkerSessionHub();
        hub.SetSessions([(Info(session), session)]);
        var (workerLink, controllerLink) = await LinkPair();
        controllerLink.PeerName = "worker-box";
        workerLink.PeerName = "controller-box";
        hub.Serve(workerLink);

        using var connection = new WorkerConnection(controllerLink);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionsChanged += () => listed.TrySetResult();
        connection.Start();
        await listed.Task.WaitAsync(Timeout);
        var info = Assert.Single(connection.Sessions);
        Assert.Equal("sess-1", info.SessionId);

        var events = new BlockingCollection<SessionEvent>();
        var backend = connection.Attach(info);
        backend.EventReceived += events.Add;
        backend.Start();
        Assert.True(backend.IsRemote);

        // The catch-up: the transcript, then the journal's stream lines.
        var history = Assert.IsType<TranscriptHistoryEvent>(Next<TranscriptHistoryEvent>(events));
        Assert.Single(history.Lines);
        Assert.Equal("Hello from the worker", Next<AssistantTextEvent>(events).Text);
        Assert.Equal("req-1", Next<PermissionRequestEvent>(events).RequestId);

        // Then live lines.
        session.Journal.Append("""{"type":"system","subtype":"session_state_changed","state":"requires_action"}""");
        Assert.Equal("requires_action", Next<SessionStateEvent>(events).State);

        // Commands reach the worker session.
        var uuid = Guid.NewGuid().ToString();
        backend.SendPrompt("do the thing", null, uuid);
        backend.RespondToPermission(new PermissionRequestEvent("req-1", "Write", "", "{}", null), allow: false, denyMessage: "no");
        backend.SetPermissionMode("acceptEdits");
        backend.SetModel("claude-sonnet-5-5");
        backend.SetEffort("high");
        backend.Interrupt();
        backend.StopTask("task_9");

        Assert.Equal(("prompt", $"do the thing|{uuid}|controller-box"), session.Next());
        Assert.Equal(("answer", "req-1|False|no"), session.Next());
        Assert.Equal(("mode", "acceptEdits"), session.Next());
        Assert.Equal(("model", "claude-sonnet-5-5"), session.Next());
        Assert.Equal(("effort", "high"), session.Next());
        Assert.Equal(("interrupt", "controller-box"), session.Next());
        Assert.Equal(("stopTask", "task_9"), session.Next());

        // The session ends on the worker: the controller's view ends with its code.
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Exited += (code, _) => exited.TrySetResult(code);
        session.Journal.End(0);
        Assert.Equal(0, await exited.Task.WaitAsync(Timeout));
        Assert.False(backend.IsRunning);
    }

    [Fact]
    public async Task Worker_Refuses_BypassMode_BadTokens_AndUnknownSessions()
    {
        var session = new FakeSession("w-1");
        session.Journal.Append(Init);
        var hub = new WorkerSessionHub();
        hub.SetSessions([(Info(session), session)]);
        var (workerLink, controllerLink) = await LinkPair();
        controllerLink.PeerName = "worker-box";
        hub.Serve(workerLink);
        using var connection = new WorkerConnection(controllerLink);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionsChanged += () => listed.TrySetResult();
        connection.Start();
        await listed.Task.WaitAsync(Timeout);

        var events = new BlockingCollection<SessionEvent>();
        var backend = connection.Attach(connection.Sessions[0]);
        backend.EventReceived += events.Add;
        backend.Start();
        Next<SessionInitEvent>(events);

        backend.SetPermissionMode("bypassPermissions");
        Assert.Contains("bypass permissions", Next<BackendNoteEvent>(events).Text);
        backend.SetModel("opus; rm -rf");
        Assert.Contains("invalid model", Next<BackendNoteEvent>(events).Text);
        backend.SetEffort("ludicrous");
        Assert.Contains("effort", Next<BackendNoteEvent>(events).Text);
        Assert.False(session.HasCommands);

        var ghost = connection.Attach(new WorkerSessionInfo("nope", null, null, "", null, null, null, true));
        var ghostEvents = new BlockingCollection<SessionEvent>();
        ghost.EventReceived += ghostEvents.Add;
        ghost.Start();
        Assert.Contains("isn't running", Next<BackendNoteEvent>(ghostEvents).Text);
    }

    [Fact]
    public async Task ControllerStop_DetachesOnly_AndALostLinkEndsTheView()
    {
        var session = new FakeSession("w-1");
        session.Journal.Append(Init);
        var hub = new WorkerSessionHub();
        hub.SetSessions([(Info(session), session)]);
        var (workerLink, controllerLink) = await LinkPair();
        controllerLink.PeerName = "worker-box";
        hub.Serve(workerLink);
        using var connection = new WorkerConnection(controllerLink);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionsChanged += () => listed.TrySetResult();
        connection.Start();
        await listed.Task.WaitAsync(Timeout);

        // The worker knows who follows which session (its "controlled from" chip), as soon as they attach.
        var following = new BlockingCollection<int>();
        hub.AttachmentsChanged += () => following.Add(hub.ControllersOf(session.Sid).Count);
        workerLink.PeerName = "controller-box";

        var first = connection.Attach(connection.Sessions[0]);
        first.Start();
        Assert.Equal(1, following.Take(new CancellationTokenSource(Timeout).Token));
        Assert.Equal(("controller-box", "controller-pin"), Assert.Single(hub.ControllersOf(session.Sid)));

        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Exited += (code, _) => stopped.TrySetResult(code);
        first.Stop();
        Assert.Equal(0, await stopped.Task.WaitAsync(Timeout));
        Assert.Equal(0, following.Take(new CancellationTokenSource(Timeout).Token));   // detached: no longer followed
        Assert.False(session.HasCommands);   // nothing was sent to end the worker's session

        var second = connection.Attach(connection.Sessions[0]);
        Assert.NotSame(first, second);
        second.Start();
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Exited += (_, why) => lost.TrySetResult(why);
        hub.DisconnectAll();
        Assert.Contains("lost the connection to worker-box", await lost.Task.WaitAsync(Timeout));
        Assert.Empty(hub.ControllersOf(session.Sid));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static WorkerSessionInfo Info(FakeSession s) =>
        new(s.Sid, "sess-1", "a title", @"C:\fixtures\proj", "claude-opus-5-5", "default", null, true);

    private static T Next<T>(BlockingCollection<SessionEvent> events) where T : SessionEvent
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
            if (events.Take(cts.Token) is T match) return match;
    }

    // Two links joined by a loopback socket: the worker's end and the controller's.
    private static async Task<(RemoteLink Worker, RemoteLink Controller)> LinkPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await accept;
            return (new RemoteLink(server.GetStream(), "controller-pin"), new RemoteLink(client.GetStream(), "worker-pin"));
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class Recorder : IJournalListener
    {
        public List<string> Lines { get; } = [];
        public void OnLine(string line) => Lines.Add(line);
        public void OnTranscript(IReadOnlyList<string> lines, bool truncated) { }
        public void OnEnded(int exitCode) { }
    }

    // A worker session that records the commands it's given.
    private sealed class FakeSession(string sid) : IWorkerSession
    {
        private readonly BlockingCollection<(string, string)> _commands = new();

        public string Sid { get; } = sid;
        public SessionJournal Journal { get; } = new(() => true);
        public bool HasCommands => _commands.Count > 0;

        public (string, string) Next()
        {
            using var cts = new CancellationTokenSource(Timeout);
            return _commands.Take(cts.Token);
        }

        public void SendPrompt(string text, IReadOnlyList<ImageContent>? images, string uuid, string fromName) =>
            _commands.Add(("prompt", $"{text}|{uuid}|{fromName}"));
        public void Answer(string requestId, bool allow, string? denyMessage, JsonNode? updatedInput, string fromName) =>
            _commands.Add(("answer", $"{requestId}|{allow}|{denyMessage}"));
        public void Interrupt(string fromName) => _commands.Add(("interrupt", fromName));
        public void SetPermissionMode(string mode, string fromName) => _commands.Add(("mode", mode));
        public void SetModel(string model, string fromName) => _commands.Add(("model", model));
        public void SetEffort(string level, string fromName) => _commands.Add(("effort", level));
        public void StopTask(string taskId, string fromName) => _commands.Add(("stopTask", taskId));
    }
}
