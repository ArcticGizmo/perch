using Perch.Data;
using Perch.Platform;
namespace Perch.App;

/// <summary>
/// Owns Perch's notification dispatch: the Windows tray balloon plus optional chime for a
/// session event, the external (ntfy) push with its per-session / AFK-lock gating, and the plain info
/// balloons the update and plugin flows raise. Extracted from <c>OverlayApplicationContext</c> so the
/// orchestrator stays a thin wiring shell.
///
/// It also tracks which session's balloon was shown last (<see cref="LastNotifiedPid"/> /
/// <see cref="LastNotifiedProject"/>) so a balloon click can focus that terminal — the click itself is
/// handled by the owner, since acknowledging the session is the monitor's concern.
/// </summary>
internal sealed class NotificationService
{
    private readonly NotifyIcon _icon;
    private readonly AppSettings _settings;
    private readonly ISessionLock _lock;
    private readonly IAudioCue _audioCue;

    public NotificationService(NotifyIcon icon, AppSettings settings, ISessionLock sessionLock, IAudioCue audioCue)
    {
        _icon = icon;
        _settings = settings;
        _lock = sessionLock;
        _audioCue = audioCue;
    }

    /// <summary>PID of the session whose balloon was shown last (null for a non-session info balloon),
    /// so a balloon click can focus the right terminal.</summary>
    public string? LastNotifiedPid { get; private set; }

    /// <summary>Project name of that session, used to disambiguate a host (e.g. VSCode) that owns
    /// several windows. Tracked alongside <see cref="LastNotifiedPid"/>.</summary>
    public string? LastNotifiedProject { get; private set; }

    /// <summary>
    /// Fires the desktop balloon + chime + external push for a session event, each gated by its own
    /// setting. The overlay's own attention flash is the owner's concern and is not raised here.
    /// </summary>
    public void Notify(NotificationKind kind, ClaudeSession session)
    {
        if (_settings.NotificationsEnabled && BalloonEnabled(kind))
            ShowSessionBalloon(kind, session.DisplayName, session.Pid);

        if (_settings.NotificationsEnabled && ChimeEnabled(kind))
            _audioCue.Play(kind);

        MaybeSendExternal(kind, session);
    }

    private bool BalloonEnabled(NotificationKind kind) =>
        kind == NotificationKind.Done ? _settings.NotifyOnDone : _settings.NotifyOnWaitingInput;

    private bool ChimeEnabled(NotificationKind kind) =>
        kind == NotificationKind.Done ? _settings.ChimeOnDone : _settings.ChimeOnWaitingInput;

    /// <summary>
    /// Settings "Test" preview: shows a sample balloon and plays the chime regardless of the saved
    /// toggles, so the user can preview exactly what a notification looks and sounds like.
    /// </summary>
    public void ShowTest(NotificationKind kind)
    {
        ShowSessionBalloon(kind, "example-project", null);
        _audioCue.Play(kind);
    }

    // Shows the desktop balloon for a session notification. A null pid means there's no real session
    // behind it (a settings "Test"), so a click won't try to focus a terminal.
    private void ShowSessionBalloon(NotificationKind kind, string projectName, string? pid)
    {
        LastNotifiedPid = pid;
        LastNotifiedProject = projectName;
        switch (kind)
        {
            case NotificationKind.Done:
                _icon.BalloonTipTitle = "Claude Code — Done";
                _icon.BalloonTipText  = $"Waiting for you in {projectName}";
                _icon.BalloonTipIcon  = ToolTipIcon.Info;
                break;
            case NotificationKind.WaitingForInput:
                _icon.BalloonTipTitle = "Claude Code — Waiting for Input";
                _icon.BalloonTipText  = $"{projectName} needs your response";
                _icon.BalloonTipIcon  = ToolTipIcon.Warning;
                break;
        }
        _icon.ShowBalloonTip(8000);
    }

    // Pushes an external notification for a session, but only when the feature is on and that session
    // has opted in (or the account-wide AFK override is on and the screen is locked). Independent of
    // the Windows-balloon per-type toggles.
    private void MaybeSendExternal(NotificationKind kind, ClaudeSession session)
    {
        bool optedIn   = session.ExternalNotify;
        bool afkActive = _settings.NotifyWhenLocked && _lock.IsLocked;
        if (!_settings.ExternalNotificationsEnabled || (!optedIn && !afkActive))
            return;

        var (title, body, tags) = kind == NotificationKind.Done
            ? ("Claude Code — Done", $"Waiting for you in {session.DisplayName}", "white_check_mark")
            : ("Claude Code — Waiting for Input", $"{session.DisplayName} needs your response", "bell");

        var host = _settings.NtfyHost;
        var topic = _settings.NtfyTopic;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
            return;

        // Attach an "Open session" action only when the session is remote-controlled (so the deep
        // link actually resolves) and the user has opted into including it.
        string? actionUrl = _settings.ExternalNotificationsIncludeRemoteLink && session.RemoteControlled
            ? $"https://claude.ai/code/{session.BridgeSessionId}"
            : null;

        // Fire-and-forget: a failed push must never stall or crash the monitor callback.
        _ = NtfyNotifier.SendAsync(host, topic, title, body, tags, actionUrl, "Open session");
    }

    /// <summary>
    /// The settings window's "Send test notification": pushes a sample to the configured ntfy
    /// host/topic and reports the outcome via a tray balloon, so misconfiguration is visible.
    /// </summary>
    public async Task SendExternalTestAsync()
    {
        var host = _settings.NtfyHost;
        var topic = _settings.NtfyTopic;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
        {
            ShowInfo("Perch — ntfy", "Enter a server URL and topic first.", ToolTipIcon.Warning);
            return;
        }

        var (ok, error) = await NtfyNotifier.SendAsync(
            host, topic, "Perch — Test", "External notifications are working.", "bell");

        ShowInfo("Perch — ntfy",
            ok ? "Test notification sent." : $"Failed to send: {error}",
            ok ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    /// <summary>A tray balloon not tied to any session (so a click won't focus a stale terminal).
    /// Used by the update and plugin-install flows as well as the ntfy test.</summary>
    public void ShowInfo(string title, string text, ToolTipIcon icon, int timeoutMs = 5000)
    {
        LastNotifiedPid = null;
        LastNotifiedProject = null;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText  = text;
        _icon.BalloonTipIcon  = icon;
        _icon.ShowBalloonTip(timeoutMs);
    }
}
