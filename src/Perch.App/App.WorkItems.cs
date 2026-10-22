using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Data.Work;

namespace Perch.Avalonia;

// Work items (docs/work-items-plan.md): named piles of work that sessions belong to, however and wherever they run.
// The host owns the markdown files; this wires it to the overlay's grouped session list, its header and row menus,
// the Work items window, and the scan (snapshots, "ended" log lines).
public partial class App
{
    private WorkItemsHost? _work;
    private WorkItemsWindow? _workWindow;
    private string _workOverlaySignature = "";

    private void InitWorkItems(AppSettings settings)
    {
        _work = new WorkItemsHost(WorkItemStore.Open());
        _work.Changed += () =>
        {
            PushWorkItems(force: true);
            _workWindow?.RosterChanged();
        };

        var canvas = _overlay!.Canvas;
        canvas.SetCollapsedWorkItems(settings.CollapsedWorkItems);
        canvas.SetGroupByWork(settings.GroupByWorkItem);
        canvas.GroupByWorkChanged += grouped =>
        {
            if (_appSettings is not { } s) return;
            s.GroupByWorkItem = grouped;
            s.Save();
        };
        canvas.WorkItemExpandChanged += (id, expanded) =>
        {
            if (_appSettings is not { } s) return;
            s.CollapsedWorkItems.Remove(id);
            if (!expanded) s.CollapsedWorkItems.Add(id);
            s.Save();
        };
        canvas.WorkItemNewSessionRequested += OpenSessionWindowInWork;
        canvas.WorkItemOpenRequested += id => OpenWorkItems(id);
        canvas.WorkItemLogRequested += id => OpenWorkItems(id, WorkFocus.Log);
        canvas.WorkItemStatusRequested += (id, status) => _work?.SetStatus(id, status);
        canvas.WorkItemMembershipRequested += (id, session, add) =>
        {
            if (add) _work?.AddSession(id, session.SessionId, session.DisplayName, session.Cwd, WorkJoin.Manual);
            else _work?.RemoveSession(id, session.SessionId);
        };
        canvas.WorkItemMoveRequested += (sessionId, title, cwd, from, to) =>
        {
            if (to is not null) _work?.AddSession(to, sessionId, title, cwd, WorkJoin.Manual);
            if (from is not null) _work?.RemoveSession(from, sessionId);
        };
        canvas.WorkItemRemoveEndedRequested += (id, sessionId) => _work?.RemoveSession(id, sessionId);
        canvas.WorkEndedResumeRequested += (id, cwd) => OpenSessionResume(id, cwd);
        canvas.WorkEndedTerminalRequested += (id, cwd) => ReopenSession(cwd, id);
        canvas.WorkItemFromSessionRequested += session =>
        {
            if (_work?.Create(session.DisplayName) is not { } item) return;
            _work.AddSession(item.Id, session.SessionId, session.DisplayName, session.Cwd, WorkJoin.Manual);
            OpenWorkItems(item.Id, WorkFocus.Rename);
        };

        _work.Start();
    }

    // The roster the overlay shows: the scan plus Perch's not-running rows.
    private IReadOnlyList<ClaudeSession> WorkRoster() => WithDormantRows(_lastSessions);

    /// <summary>From every scan: let the host snapshot/log member sessions, then re-push the groups if they moved.</summary>
    private void ObserveWorkItems(IReadOnlyList<ClaudeSession> roster)
    {
        if (_work is null) return;
        _work.ObserveSessions(roster);
        PushWorkItems(roster);
        _workWindow?.RosterChanged();
    }

    private void PushWorkItems(bool force = false) => PushWorkItems(WorkRoster(), force);

    private void PushWorkItems(IReadOnlyList<ClaudeSession> roster, bool force = false)
    {
        if (_work is null || _overlay is null) return;
        var items = _work.ForOverlay(roster);
        var sig = WorkItemsHost.Signature(items);
        if (!force && sig == _workOverlaySignature) return;
        _workOverlaySignature = sig;
        _overlay.Canvas.SetWorkItems(items);
    }

    /// <summary>Opens (or focuses) the Work items window, on <paramref name="itemId"/> when given.</summary>
    private void OpenWorkItems(string? itemId = null, WorkFocus focus = WorkFocus.None)
    {
        if (_work is not { } host) return;
        _workWindow = WindowHost.ShowOrFocus(_workWindow,
            () =>
            {
                var w = new WorkItemsWindow(host, WorkRoster);
                w.FocusSessionRequested += FocusSession;
                w.ResumeInPerchRequested += (id, cwd) => OpenSessionResume(id, cwd);
                w.ResumeInTerminalRequested += (id, cwd) => ReopenSession(cwd, id);
                w.HistoryRequested += id => OpenHistory(id);
                w.NewSessionRequested += OpenSessionWindowInWork;
                return w;
            },
            () => _workWindow = null,
            w => w.Retarget(itemId, focus));
    }

    /// <summary>"+" on an item: a session launcher whose first start (or resume) joins the item, logged as
    /// "launched". The binding is one-shot, so a later /resume in the same window doesn't join anything.</summary>
    private void OpenSessionWindowInWork(string itemId)
    {
        if (_work?.Find(itemId) is null) return;
        var w = NewSessionWindow();
        BindToWork(w, itemId);
        w.Show();
        ForceFront(w);
        w.LoadRecents(ActiveSessionIds());
    }

    /// <summary>Makes the next session window <paramref name="w"/> starts (or resumes) join the item, logged as
    /// "launched", then lets go: a later /resume in the same window joins nothing.</summary>
    private void BindToWork(SessionWindow w, string itemId)
    {
        if (_work?.Find(itemId) is not { } item) return;
        var start = w.StartRequested;
        var dormant = w.DormantRequested;
        PerchSession Join(PerchSession s, SessionLaunchOptions o)
        {
            w.StartRequested = start;
            w.DormantRequested = dormant;
            if (s.SessionId is { } id) JoinWork(itemId, id, o.Cwd);
            return s;
        }
        if (start is not null) w.StartRequested = o => Join(start(o), o);
        if (dormant is not null) w.DormantRequested = o => Join(dormant(o), o);
        w.Title = $"New session · {item.Name}";
    }

    // A session Perch started or resumed for an item. Its title is the folder for now; the scan replaces it with the
    // session's own name once it's on the roster.
    private void JoinWork(string itemId, string sessionId, string cwd) =>
        _work?.AddSession(itemId, sessionId, Path.GetFileName(cwd.TrimEnd('\\', '/')), cwd, WorkJoin.Launched);

    // `perch --work <ref>` (and the hook's PERCH_WORK marker, W2): the item a ref names, if any.
    private WorkItem? ResolveWork(string? reference) =>
        _work is null || string.IsNullOrWhiteSpace(reference) ? null : WorkItemStore.Resolve(reference, _work.Items);
}
