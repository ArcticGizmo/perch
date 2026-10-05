using System.IO;
using Avalonia.Threading;
using Perch.Avalonia.Services;
using Perch.Data;
using Perch.Data.Roost;
using Perch.Platform;

namespace Perch.Avalonia;

// Session recovery in the Roost (docs/session-recovery-plan.md, R6): the sessions it shows dormant — no process, the
// conversation tailed from disk, a composer whose first send resumes it. They're what Perch had open when it closed,
// Perch sessions open dormant in a window, whatever a tab region still holds once its process is gone (so a tab
// survives Perch closing or a reboot), and the Recent list. The roster takes them with each fold and hands a region's
// session across the change (RoostRoster.Adopted), live → dormant and back.
public partial class App
{
    // The Recent list, built off the UI thread (it reads transcripts), plus every transcript by id: what a tab's cell is
    // looked up in after a restart, when its session is in no other list.
    private readonly RecentSessions _recentBuilder = new();
    private IReadOnlyList<RecentSession> _recentRows = [];
    private IReadOnlyDictionary<string, RecentSession> _recentById = new Dictionary<string, RecentSession>();
    private IReadOnlyDictionary<string, HistoryEntry> _transcriptsById = new Dictionary<string, HistoryEntry>();
    private bool _recentBuilding, _recentRebuild;
    private DispatcherTimer? _recentDebounce;
    // The first Recent build has landed. Until then a tab's persisted cell that matches no live session waits rather
    // than dropping: it may be about to come back dormant.
    private bool _roostDormantReady;
    private HashSet<string> _roostLiveIds = new(StringComparer.Ordinal);

    // A session that just left the scan may still be writing its exit flush (cost-state): read it a few seconds later,
    // or a clean exit would read as interrupted.
    private static readonly TimeSpan RecentAfterEndDelay = TimeSpan.FromSeconds(5);

    /// <summary>Rebuilds the Recent list off the UI thread, then re-folds the Roost. A request while one runs queues
    /// one more. Under render, tests and replay (no ledger, no real history) it only marks the dormant set ready.</summary>
    private void RefreshRecent()
    {
        if (!LedgerWritable)
        {
            if (!_roostDormantReady) { _roostDormantReady = true; RefoldRoost(); }
            return;
        }
        if (_recentBuilding) { _recentRebuild = true; return; }
        _recentBuilding = true;
        var active = ActiveSessionIds();
        var held = _ledger?.HeldIds() ?? new HashSet<string>(StringComparer.Ordinal);
        var dismissed = new Dictionary<string, DateTime>(_appSettings?.RecentDismissed ?? [], StringComparer.Ordinal);
        var shutdowns = _ledger?.ShutdownsSnapshot() ?? [];
        var restorableIds = _restorable.Select(r => r.SessionId).ToList();
        Task.Run(() =>
        {
            var entries = SessionHistory.ListAll(active);
            var rows = _recentBuilder.Build(entries, held, dismissed, shutdowns, DateTime.Now);
            var byId = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
            foreach (var e in entries) byId.TryAdd(e.SessionId, e);
            var rowsById = new Dictionary<string, RecentSession>(StringComparer.Ordinal);
            foreach (var r in rows) rowsById.TryAdd(r.Entry.SessionId, r);
            // What Perch had open that has no transcript with a message in it (opened, never prompted): nothing to
            // resume, so it doesn't come back as "was open".
            var empty = restorableIds
                .Where(id => !byId.TryGetValue(id, out var e) || !_recentBuilder.EndOf(e.Path, null).HasConversation)
                .ToHashSet(StringComparer.Ordinal);
            return (rows, rowsById, byId, empty);
        }).ContinueWith(t =>
        {
            _recentBuilding = false;
            if (t.IsCompletedSuccessfully)
            {
                (_recentRows, _recentById, _transcriptsById, var empty) = t.Result;
                DropRestorable(empty);
            }
            else LaunchLog.Write($"recent sessions: build failed ({t.Exception?.GetBaseException().GetType().Name})");
            _roostDormantReady = true;
            RefoldRoost();
            MaybeShowRecoveryToast();
            if (_recentRebuild) { _recentRebuild = false; RefreshRecent(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RefreshRecentAfterEnd()
    {
        if (_recentDebounce is null)
        {
            _recentDebounce = new DispatcherTimer { Interval = RecentAfterEndDelay };
            _recentDebounce.Tick += (_, _) => { _recentDebounce.Stop(); RefreshRecent(); };
        }
        _recentDebounce.Stop();
        _recentDebounce.Start();
    }

    // The monitor has delivered its first scan. Before that the roster has no live panes, and folding (or settling the
    // tabs' persisted cells) against it would treat every running session as gone.
    private bool _roostScanned;

    /// <summary>Re-folds the Roost against the latest scan, which also pushes the overlay's Recent lines: the dormant set
    /// changed (a Recent build, a restore, a dismissal, a dormant window opening or closing). Before the first scan only
    /// the overlay's lines move; that scan folds the rest in anyway.</summary>
    private void RefoldRoost()
    {
        if (_roostScanned) UpdateRoost(_lastSessions);
        else PushRecentLines();
    }

    // After each fold: a session that left the scan may now be a Recent row.
    private void NoteRoostLiveSet(IReadOnlyList<ClaudeSession> sessions)
    {
        var ids = sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        if (_roostLiveIds.Any(id => !ids.Contains(id))) RefreshRecentAfterEnd();
        _roostLiveIds = ids;
    }

    // ── The dormant set ───────────────────────────────────────────────────────────

    /// <summary>Every session the Roost shows dormant, in the Recent group's order: what Perch had open when it closed,
    /// then Perch sessions open dormant in a window, then what the tabs hold, then the Recent list. One per session,
    /// never one that's live (the roster also refuses those), never a dismissed ending.</summary>
    private List<RoostDormant> RoostDormantSessions(IReadOnlyList<ClaudeSession> live)
    {
        var liveIds = live.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        var list = new List<RoostDormant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(RoostDormant? d, bool dismissible = true)
        {
            if (d is null || liveIds.Contains(d.SessionId) || (dismissible && IsDismissed(d.SessionId, d.LastActive))
                || !seen.Add(d.SessionId)) return;
            list.Add(d);
        }

        foreach (var r in _restorable) Add(RestorableDormant(r), dismissible: false);

        // A woken one stays until the scan sees its process, so its pane holds the place for the live one to take.
        foreach (var s in _perchSessions)
        {
            if (s.SessionId is not { } id) continue;
            bool waking = s.IsRunning && _roostRoster.Find(RoostToken.DormantKey(id)) is not null;
            if (!s.IsDormant && !waking) continue;
            Add(new RoostDormant(id, s.Cwd, ProjectOf(s.Cwd), s.Title, LastActiveOf(id),
                _recentById.TryGetValue(id, out var row) ? KindOf(row) : RoostDormantKind.NotRunning, PerchOrigin: true),
                dismissible: !waking);
        }

        foreach (var id in _roostTabs.SessionIds())
            if (!liveIds.Contains(id)) Add(TabHeldDormant(id));

        foreach (var row in _recentRows.Take(SessionRecovery.RoostRecentRows))
            Add(FromRecent(row, PerchOriginOf(row.Entry.SessionId)));

        return list;
    }

    // What Perch had open when it closed. Perch closing ended it rather than the user, so it reads as interrupted
    // whichever way it ended (RoostDormant.WasInterrupted).
    private RoostDormant RestorableDormant(LedgerSession r) =>
        new(r.SessionId, r.Cwd, ProjectOf(r.Cwd), r.Title, LastActiveOf(r.SessionId), RoostDormantKind.WasOpenInPerch,
            PerchOrigin: true, JustBeforeShutdown: _recentById.TryGetValue(r.SessionId, out var ended) && ended.JustBeforeShutdown);

    // A tab region's session that isn't running: from the Recent list when it's there (it knows how it ended), else the
    // pane that just ended (the process went this run), else the dormant pane already showing it, else its transcript.
    private RoostDormant? TabHeldDormant(string id)
    {
        if (_recentById.TryGetValue(id, out var row)) return FromRecent(row, PerchOriginOf(id));
        foreach (var p in _roostRoster.Panes)
        {
            if (p.Session.SessionId != id) continue;
            if (p.Dormant is { } existing) return existing;
            if (p.EndedAt is { } ended)
                return new RoostDormant(id, p.Session.Cwd, p.Session.ProjectName, p.Session.Title, ended,
                    RoostDormantKind.Ended, p.Session.IsPerchControlled);
        }
        return _transcriptsById.TryGetValue(id, out var e)
            ? new RoostDormant(id, e.Cwd, e.ProjectName, e.Title, e.LastUpdated, RoostDormantKind.NotRunning)
            : null;
    }

    private static RoostDormant FromRecent(RecentSession row, bool perch) => new(
        row.Entry.SessionId, row.Entry.Cwd, row.Entry.ProjectName, row.Entry.Title, row.EndedAt, KindOf(row), perch,
        row.JustBeforeShutdown);

    private static RoostDormantKind KindOf(RecentSession row) =>
        row.End.Kind == SessionEndKind.Abrupt ? RoostDormantKind.Interrupted
        : row.JustBeforeShutdown ? RoostDormantKind.BeforeShutdown
        : row.End.Kind == SessionEndKind.Exited ? RoostDormantKind.Exited
        : RoostDormantKind.Ended;

    // What the Roost already knows of a session's origin (a Recent row doesn't say).
    private bool PerchOriginOf(string id) =>
        _roostRoster.Panes.FirstOrDefault(p => p.Session.SessionId == id)?.Session.IsPerchControlled == true;

    private DateTime LastActiveOf(string id) =>
        _transcriptsById.TryGetValue(id, out var e) ? e.LastUpdated : DateTime.Now;

    // The folder's name, split on either separator (a transcript's cwd may come from another OS).
    private static string ProjectOf(string cwd) => PathLeaf.Of(cwd) is { Length: > 0 } name ? name : cwd;

    // A dismissal covers the ending it was made on (Q2): a later ending of the same session shows again.
    private bool IsDismissed(string sessionId, DateTime endedAt) =>
        _appSettings?.RecentDismissed is { } map && map.TryGetValue(sessionId, out var at) && endedAt <= at;

    // ── Pane actions ──────────────────────────────────────────────────────────────

    /// <summary>"Dismiss" on a Recent line or a dormant Roost pane: it leaves the overlay's Recent list and the Roost
    /// (and its tab), and stays dismissed until the session ends again. One that Perch had open also stops coming back
    /// after the next restart.</summary>
    private void DismissRecent(string sessionId)
    {
        if (_restorable.Any(r => r.SessionId == sessionId)) ForgetHeld(sessionId);
        if (_appSettings is { } s)
        {
            var map = s.RecentDismissed ?? new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var cutoff = DateTime.Now - SessionRecovery.RecentWindow;
            foreach (var old in map.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) map.Remove(old);
            // "Now" covers the ending being dismissed (it's in the past, whichever clock read it); a later one isn't.
            map[sessionId] = DateTime.Now;
            s.RecentDismissed = map;
            s.Save();
        }
        _roostTabs.Unassign(RoostToken.DormantKey(sessionId));
        RefoldRoost();
    }

    // ── The overlay's Recent button ───────────────────────────────────────────────

    private DispatcherTimer? _recentAgeTimer;

    /// <summary>Pushes the lines behind the overlay's Recent button: what Perch had open first ("was open"), then the
    /// whole Recent list (flagged first, newest first), each through the same <see cref="RecentLine.From"/> the Roost's
    /// list uses. Never a session that's live now or dismissed. Cheap (no IO): run on every fold, and each minute so the
    /// ages move.</summary>
    private void PushRecentLines()
    {
        if (_overlay is null) return;
        var live = _lastSessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<RecentLine>();
        var now = DateTime.Now;

        void Add(RoostDormant d)
        {
            if (!live.Contains(d.SessionId) && seen.Add(d.SessionId)) lines.Add(RecentLine.From(d, now));
        }

        foreach (var r in _restorable) Add(RestorableDormant(r));
        foreach (var row in _recentRows)
            if (!IsDismissed(row.Entry.SessionId, row.EndedAt)) Add(FromRecent(row, perch: false));
        _overlay.Canvas.SetRecent(lines);

        if (_recentAgeTimer is null)
        {
            _recentAgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _recentAgeTimer.Tick += (_, _) => PushRecentLines();
            _recentAgeTimer.Start();
        }
    }

    /// <summary>The startup toast (once a run, after the first Recent build): the Perch sessions that were open when
    /// Perch closed, and the sessions the restart that ended the previous run interrupted.</summary>
    private void MaybeShowRecoveryToast()
    {
        if (!_recoveryToastPending) return;
        _recoveryToastPending = false;
        int open = _restorable.Count;
        int interrupted = _previousShutdown is { } shutdown
            ? _recentRows.Count(r => r.End.Kind == SessionEndKind.Abrupt && SessionRecovery.JustBeforeShutdown(r.EndedAt, [shutdown]))
            : 0;
        if (open == 0 && interrupted == 0) return;

        static string Count(int n, string what) => n == 1 ? $"1 {what} was" : $"{n} {what}s were";
        var parts = new List<string>();
        if (open > 0) parts.Add($"{Count(open, "Perch session")} open when Perch closed");
        if (interrupted > 0) parts.Add($"{Count(interrupted, "session")} interrupted by a restart");
        _notifier?.Show(open == 0 ? "Interrupted by a restart" : "Pick up where you left off",
            string.Join(", and ", parts) + ". They're behind the Recent (clock) button on the overlay.", ToastLevel.Info, null, null);
    }

    /// <summary>
    /// A dormant pane's first send: the same checks a session window runs before resuming (the folder-trust question,
    /// over the Roost; not live in a terminal; not held by another Perch — <see cref="ResumeGate"/>), then the session
    /// wakes and the message goes. True when it went; on a refusal the pane keeps the text and a toast says why. The
    /// live pane takes the dormant one's place once the scan sees the process.
    /// </summary>
    private async Task<bool> WakeFromRoostAsync(RoostPane pane, string text)
    {
        var sid = pane.Session.SessionId;
        var cwd = pane.Session.Cwd;
        if (_roostWindow is not { } owner) return false;
        if (!Directory.Exists(cwd)) return WakeRefused($"folder not found: {cwd}");

        string? configDir;
        bool trusted;
        try { (configDir, trusted) = await ResumeGate.ReadTrustAsync(cwd, () => TranscriptLocator.ResumeConfigRoot(sid, cwd)); }
        catch (Exception ex) { return WakeRefused($"couldn't read the session: {ex.Message}"); }

        if (!trusted && !await ResumeGate.ConfirmTrustAsync(owner, configDir, cwd)) return false;   // declined: the text stays
        if (ResumeGate.Refusal(sid, configDir, id => _lastSessions.FirstOrDefault(s => s.SessionId == id)) is { } refusal)
            return WakeRefused(refusal);

        // The same PerchSession a window may already be showing dormant, so there's only ever one writer.
        bool existed = _perchSessions.Any(s => s.SessionId == sid && !s.HasEnded);
        var session = OpenDormantPerchSession(new SessionLaunchOptions(cwd, ResumeId: sid));
        try
        {
            if (session.IsDormant)
            {
                LaunchLog.Write($"roost wake: {TranscriptLocator.DescribeResume(sid, cwd)}");
                var mode = ClaudeUserSettings.ReadSessionDefaults().PermissionMode ?? Windows.SessionWindow.FallbackMode;
                session.Wake(new SessionLaunchOptions(cwd, PermissionMode: mode, ResumeId: sid, ConfigDir: configDir));
            }
            session.SendPrompt(text);
            return true;
        }
        catch (Exception ex)
        {
            if (!existed && session.IsDormant) _perchSessions.Remove(session);
            return WakeRefused($"failed to start claude: {ex.Message}");
        }
    }

    private bool WakeRefused(string why)
    {
        LaunchLog.Write($"roost wake refused: {why}");
        _notifier?.Show("Couldn't resume the session", why, ToastLevel.Warning, null, null);
        return false;
    }
}
