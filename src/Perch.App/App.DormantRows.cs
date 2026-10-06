using Avalonia.Controls;
using Perch.Avalonia.Services;
using Perch.Avalonia.Windows;
using Perch.Data;
using Perch.Data.Roost;

namespace Perch.Avalonia;

// Not-running Perch sessions on the overlay (docs/session-recovery-plan.md). A Perch session Perch holds keeps its row
// when its process goes — Perch exiting, an update, a crash, the OS restarting — faded and marked "not running". Clicking
// it opens the conversation without starting claude; the first reply starts it again (the dormant wake), with the
// settings it last ran with. Only "End session" takes the row away. And since closing Perch now stops a turn rather than
// losing a session, Exit and Update ask first when one is mid-turn.
public partial class App
{
    /// <summary>The scan plus a row for each not-running Perch session (none for one the scan has live again).</summary>
    private IReadOnlyList<ClaudeSession> WithDormantRows(IReadOnlyList<ClaudeSession> live)
    {
        if (_parked.Count == 0) return live;
        var liveIds = live.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        var rows = live.ToList();
        foreach (var p in _parked)
        {
            if (liveIds.Contains(p.SessionId)) continue;
            rows.Add(new ClaudeSession(RoostToken.DormantKey(p.SessionId), p.SessionId, SessionStatus.Idle, p.Cwd,
                ProjectOf(p.Cwd), LastActiveOf(p.SessionId), Views.ModeGlyph.Parse(p.PermissionMode),
                Title: p.Title, Model: p.Model, PerchControlled: true) { IsDormant = true });
        }
        return rows;
    }

    /// <summary>Re-feeds the overlay its rows when the not-running set changes between scans.</summary>
    private void PushOverlayRows() => _overlay?.Canvas.Update(WithDormantRows(_lastSessions));

    /// <summary>A not-running row's click: its conversation in a window, still not running until the first reply.</summary>
    private void OpenDormantRow(ClaudeSession row) => OpenSessionResume(row.SessionId, row.Cwd);

    /// <summary>"End session" on a not-running row or "Dismiss" on its Roost pane: Perch stops holding it.</summary>
    private void EndDormant(string sessionId)
    {
        ForgetHeld(sessionId);
        // A window showing it dormant has nothing left to show it for.
        foreach (var w in _sessionWindows.Where(w => w.Session is { IsDormant: true } s && s.SessionId == sessionId).ToList())
            w.Close();
    }

    /// <summary>"Resume in terminal" on a session that isn't running. One Perch holds is a terminal session from then on,
    /// so Perch lets it go first, as handing a live one back does.</summary>
    private void ResumeDormantInTerminal(string cwd, string sessionId)
    {
        if (_parked.Any(p => p.SessionId == sessionId)) EndDormant(sessionId);
        ReopenSession(cwd, sessionId);
    }

    // ── Exit and Update stop a turn ────────────────────────────────────────────────

    private bool _cutShortConfirming;

    // A turn is running, or a permission/question card is waiting on the user.
    private static bool IsMidTurn(PerchSession s) =>
        s.IsRunning && (s.Conversation.TurnActive || s.Conversation.PendingPermission is not null);

    /// <summary>True to go ahead with <paramref name="action"/>: nothing is mid-turn, or the user said so. Closing Perch
    /// leaves every session where it was, but a running turn stops and has to be asked to carry on.</summary>
    private async Task<bool> ConfirmStopTurnsAsync(string action, string confirmLabel, Window? requester)
    {
        int busy = _perchSessions.Count(IsMidTurn);
        if (busy == 0) return true;
        if (_cutShortConfirming) return false;
        if ((requester is { IsVisible: true } ? requester : _overlay) is not { } owner) return true;
        _cutShortConfirming = true;
        try
        {
            return await ConfirmDialog.ShowAsync(owner,
                busy == 1 ? "A Perch session is working" : $"{busy} Perch sessions are working",
                $"{action} stops {(busy == 1 ? "it" : "them")} mid-turn. {(busy == 1 ? "It stays" : "They stay")} " +
                "where you left it, not running — reply to pick it back up, and ask Claude to carry on.",
                confirmLabel, "Not now");
        }
        finally { _cutShortConfirming = false; }
    }

    /// <summary>Every "update now" (overlay badge, toast, tray item, Settings).</summary>
    private async void StartUpdate(Window? requester = null)
    {
        if (_updateService is not { } updater) return;
        if (InstallChannel.SelfUpdates && !await ConfirmStopTurnsAsync("Updating", "Update now", requester)) return;
        updater.PerformUpdate(CloseAuxWindows);
    }

    /// <summary>The overlay's "Exit Perch" and the tray's Exit.</summary>
    private async void RequestExit()
    {
        if (!await ConfirmStopTurnsAsync("Exiting", "Exit", null)) return;
        _desktop?.Shutdown();
    }
}
