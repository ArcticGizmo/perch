using Perch.Platform;

namespace Perch.Data;

/// <summary>
/// Dispatches Perch's notifications: the local desktop toast (via <see cref="INotifier"/>) plus the
/// optional chime (<see cref="IAudioCue"/>) for a session event, the external (ntfy) push with its
/// per-session / AFK-lock gating, and the plain info toasts the update / plugin / ntfy-test flows raise.
///
/// Toolkit-neutral — it depends only on the platform seams, so both heads share it. The Avalonia port of
/// the WinForms <c>NotificationService</c>; where that talked to a WinForms <c>NotifyIcon</c> directly,
/// this goes through <see cref="INotifier"/>, and clicking a session toast is routed back through
/// <see cref="INotifier.SessionActivated"/> (the owner focuses + acknowledges), so no last-notified
/// bookkeeping lives here.
/// </summary>
internal sealed class NotificationService
{
    private readonly INotifier _notifier;
    private readonly AppSettings _settings;
    private readonly ISessionLock _lock;
    private readonly IAudioCue _audio;

    public NotificationService(INotifier notifier, AppSettings settings, ISessionLock sessionLock, IAudioCue audioCue)
    {
        _notifier = notifier;
        _settings = settings;
        _lock = sessionLock;
        _audio = audioCue;
    }

    /// <summary>
    /// Fires the desktop toast + chime + external push for a session event, each gated by its own
    /// setting. The overlay's own attention flash is the owner's concern and is not raised here.
    /// </summary>
    public void Notify(NotificationKind kind, ClaudeSession session)
    {
        if (_settings.NotificationsEnabled && ToastEnabled(kind))
        {
            var (title, body, level) = Describe(kind, session.DisplayName);
            _notifier.Show(title, body, level, session.Pid, session.ProjectName);
        }

        if (_settings.NotificationsEnabled && ChimeEnabled(kind))
            _audio.Play(kind);

        MaybeSendExternal(kind, session);
    }

    /// <summary>Settings "Test" preview: shows a sample toast and plays the chime regardless of the saved
    /// toggles, so the user can preview exactly what a notification looks and sounds like.</summary>
    public void ShowTest(NotificationKind kind)
    {
        var (title, body, level) = Describe(kind, "example-project");
        _notifier.Show(title, body, level, null, null); // null pid — a preview, not a real session
        _audio.Play(kind);
    }

    /// <summary>The settings window's "Send test notification": pushes a sample to the configured ntfy
    /// host/topic and reports the outcome via a toast, so misconfiguration is visible.</summary>
    public async Task SendExternalTestAsync()
    {
        var host = _settings.NtfyHost;
        var topic = _settings.NtfyTopic;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(topic))
        {
            _notifier.Show("Perch — ntfy", "Enter a server URL and topic first.", ToastLevel.Warning, null, null);
            return;
        }

        var (ok, error) = await NtfyNotifier.SendAsync(
            host, topic, "Perch — Test", "External notifications are working.", "bell");

        _notifier.Show("Perch — ntfy",
            ok ? "Test notification sent." : $"Failed to send: {error}",
            ok ? ToastLevel.Info : ToastLevel.Error, null, null);
    }

    /// <summary>A toast not tied to any session — used by the update / plugin flows.</summary>
    public void ShowInfo(string title, string text, ToastLevel level) =>
        _notifier.Show(title, text, level, null, null);

    /// <summary>The actionable "update available" toast — clicking it starts the update, the same action
    /// as the update button, routed back through <see cref="INotifier.UpdateActivated"/>.</summary>
    public void ShowUpdateAvailable(string title, string text) =>
        _notifier.ShowUpdate(title, text);

    private bool ToastEnabled(NotificationKind kind) =>
        kind == NotificationKind.Done ? _settings.NotifyOnDone : _settings.NotifyOnWaitingInput;

    private bool ChimeEnabled(NotificationKind kind) =>
        kind == NotificationKind.Done ? _settings.ChimeOnDone : _settings.ChimeOnWaitingInput;

    private static (string title, string body, ToastLevel level) Describe(NotificationKind kind, string project) =>
        kind == NotificationKind.Done
            ? ("Claude Code — Done", $"Waiting for you in {project}", ToastLevel.Info)
            : ("Claude Code — Waiting for Input", $"{project} needs your response", ToastLevel.Warning);

    // Pushes an external notification for a session, but only when the feature is on and that session
    // has opted in (or the account-wide AFK override is on and the screen is locked). Independent of the
    // local-toast per-type toggles.
    private void MaybeSendExternal(NotificationKind kind, ClaudeSession session)
    {
        bool optedIn = session.ExternalNotify;
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

        // Attach an "Open session" action only when the session is remote-controlled (so the deep link
        // actually resolves) and the user has opted into including it.
        string? actionUrl = _settings.ExternalNotificationsIncludeRemoteLink && session.RemoteControlled
            ? $"https://claude.ai/code/{session.BridgeSessionId}"
            : null;

        // Fire-and-forget: a failed push must never stall or crash the monitor callback.
        _ = NtfyNotifier.SendAsync(host, topic, title, body, tags, actionUrl, "Open session");
    }
}
