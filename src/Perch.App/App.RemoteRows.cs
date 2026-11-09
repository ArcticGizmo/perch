using Perch.Avalonia.Services;
using Perch.Data;
using Perch.Remote;
using Perch.Platform;

namespace Perch.Avalonia;

// Remote workers on the controller (docs/remote-workers-plan.md, W4): every session a paired worker offers gets an
// overlay row, labelled with the worker's name. Clicking one opens the normal session window on a PerchSession whose
// backend is the network link. A worker that can't be reached keeps its rows, faded, until it's back.
public partial class App
{
    // The remote sessions this machine is viewing, by the worker's handle for each. One view per remote session, so a
    // second click shows the open one rather than attaching twice.
    private readonly Dictionary<string, PerchSession> _remoteSessions = new(StringComparer.Ordinal);

    /// <summary>The rows plus one for each session the paired workers offer.</summary>
    private IReadOnlyList<ClaudeSession> WithRemoteRows(IReadOnlyList<ClaudeSession> rows)
    {
        if (_workerHost?.Workers.All is not { Count: > 0 } workers || workers.All(w => w.Sessions.Count == 0)) return rows;
        var list = rows.ToList();
        foreach (var w in workers)
            foreach (var s in w.Sessions)
                list.Add(RemoteRow(w, s));
        return list;
    }

    private static ClaudeSession RemoteRow(WorkerLinks.Worker worker, WorkerSessionInfo s)
    {
        var status = !worker.Online ? SessionStatus.Idle : s.State switch
        {
            WorkerProtocol.Busy => SessionStatus.Running,
            WorkerProtocol.Waiting => SessionStatus.AwaitingInput,
            _ => SessionStatus.Idle,
        };
        return new ClaudeSession(ClaudeSession.RemoteKey(s.Sid), s.SessionId ?? "", status, s.Cwd, ProjectOf(s.Cwd),
            DateTime.Now, Views.ModeGlyph.Parse(s.Mode), Title: s.Title, Model: s.Model, PerchControlled: true)
        {
            RemoteWorker = worker.Name,
            RemoteOffline = !worker.Online,
        };
    }

    /// <summary>A remote row's click: its session in a window, attaching to it on first open.</summary>
    private void OpenRemoteRow(ClaudeSession row)
    {
        if (ClaudeSession.RemoteSidOf(row.Pid) is not { } sid) return;
        if (_remoteSessions.TryGetValue(sid, out var open) && !open.HasEnded)
        {
            ShowSessionView(open);
            return;
        }
        if (_workerHost?.Workers.FindSession(sid) is not { } found) return;
        var (worker, info) = found;
        if (worker.Connection is not { IsConnected: true } connection)
        {
            _notifier?.Show($"{worker.Name} is offline",
                $"Perch keeps trying to reach it{(worker.Problem is { Length: > 0 } why ? $" (last time: {why})" : "")}. " +
                "Its sessions open once it's back.", ToastLevel.Info, null, null);
            return;
        }
        var backend = connection.Attach(info);
        var session = PerchSession.ForBackend(backend, new SessionLaunchOptions(info.Cwd, info.Model, info.Mode, info.Effort),
            worker.Name);
        _remoteSessions[sid] = session;
        session.Ended += s =>
        {
            if (_remoteSessions.TryGetValue(sid, out var current) && ReferenceEquals(current, s)) _remoteSessions.Remove(sid);
        };
        backend.Start();   // after ForBackend wired its events, so the catch-up lands in the conversation
        ShowSessionView(session);
    }

    // A remote session nobody is looking at stops following its worker: there's no process here to keep alive, and the
    // overlay row reads its state from the worker's list, not from this stream. Opening the row again re-attaches.
    private void DetachUnviewedRemoteSessions()
    {
        foreach (var s in _remoteSessions.Values.ToList())
            if (!_sessionWindows.Any(w => ReferenceEquals(w.Session, s))) s.End();
    }
}
