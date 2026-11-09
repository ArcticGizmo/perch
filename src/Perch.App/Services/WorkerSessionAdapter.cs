using System.Text.Json.Nodes;
using Avalonia.Threading;
using Perch.Data.Control;
using Perch.Remote;

namespace Perch.Avalonia.Services;

/// <summary>A live <see cref="PerchSession"/> as this machine offers it to paired controllers
/// (docs/remote-workers-plan.md, W3). Commands arrive on a link's thread and run on the UI thread, where the session
/// lives.</summary>
internal sealed class WorkerSessionAdapter(PerchSession session) : IWorkerSession
{
    public PerchSession Session { get; } = session;
    public string Sid { get; } = Guid.NewGuid().ToString("N");
    public SessionJournal Journal => Session.Journal;

    /// <summary>What controllers are told about the session now (UI thread).</summary>
    public WorkerSessionInfo Info() => new(Sid, Session.SessionId, Session.Title, Session.Cwd, Session.Model,
        Session.PermissionMode, Session.Effort, Session.IsRunning, State());

    // As the overlay reads a local Perch session: blocked on someone, mid-turn, or settled.
    private string State()
    {
        var conv = Session.Conversation;
        return conv.PendingPermission is not null || conv.WaitingElsewhere ? WorkerProtocol.Waiting
             : conv.TurnActive ? WorkerProtocol.Busy
             : WorkerProtocol.Idle;
    }

    public void SendPrompt(string text, IReadOnlyList<ImageContent>? images, string uuid, string fromName) =>
        Post(() => Session.SendFromController(text, images, uuid, fromName));

    public void Answer(string requestId, bool allow, string? denyMessage, JsonNode? updatedInput, string fromName) =>
        Post(() => Session.AnswerFromController(requestId, allow, denyMessage, updatedInput, fromName));

    public void Interrupt(string fromName) => Post(() => Session.InterruptFromController(fromName));
    public void SetPermissionMode(string mode, string fromName) => Post(() => Session.SetPermissionMode(mode, fromName));
    public void SetModel(string model, string fromName) => Post(() => Session.SetModel(model, fromName));
    public void SetEffort(string level, string fromName) => Post(() => Session.SetEffort(level, fromName));
    public void StopTask(string taskId, string fromName) => Post(() => Session.StopTaskFromController(taskId, fromName));

    private void Post(Action action) => Dispatcher.UIThread.Post(() =>
    {
        if (!Session.HasEnded) action();
    });
}
