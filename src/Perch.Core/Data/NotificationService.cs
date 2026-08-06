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
            var (title, body, level) = Describe(kind, session.DisplayName, session.ApiFailure?.Status ?? 0, session.PullRequest);
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
        // A sample PR with reviews so the PR-related previews (finished / reviewed / approved) read like the
        // real toast; ignored by the non-PR kinds. octocat approves; hubot (newer) requests changes — so the
        // approved preview names octocat and the reviewed preview names hubot.
        var samplePr = new PullRequestInfo(123, "", "example", PrState.Merged)
        {
            LatestReviews =
            [
                new PrReview("octocat", PrReviewState.Approved, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)),
                new PrReview("hubot", PrReviewState.ChangesRequested, new DateTime(2026, 1, 1, 9, 5, 0, DateTimeKind.Utc)),
            ],
        };
        var (title, body, level) = Describe(kind, "example-project", 529, samplePr);
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

    private bool ToastEnabled(NotificationKind kind) => kind switch
    {
        NotificationKind.Done => _settings.NotifyOnDone,
        NotificationKind.ApiFailed => _settings.NotifyOnApiError,
        NotificationKind.PrFinished => _settings.NotifyOnPrFinished,
        NotificationKind.PrReviewed => _settings.NotifyOnPrReviewed,
        NotificationKind.PrApproved => _settings.NotifyOnPrApproved,
        _ => _settings.NotifyOnWaitingInput,
    };

    private bool ChimeEnabled(NotificationKind kind) => kind switch
    {
        NotificationKind.Done => _settings.ChimeOnDone,
        NotificationKind.ApiFailed => _settings.ChimeOnApiError,
        NotificationKind.PrFinished => _settings.ChimeOnPrFinished,
        NotificationKind.PrReviewed => _settings.ChimeOnPrReviewed,
        NotificationKind.PrApproved => _settings.ChimeOnPrApproved,
        _ => _settings.ChimeOnWaitingInput,
    };

    // The API status code is folded into the body when known (0 = unknown, so a bare "API error").
    private static (string title, string body, ToastLevel level) Describe(
        NotificationKind kind, string project, int apiStatus, PullRequestInfo? pr = null) => kind switch
    {
        NotificationKind.Done =>
            ("Claude Code — Done", $"Waiting for you in {project}", ToastLevel.Info),
        NotificationKind.ApiFailed =>
            ("Claude Code — API Error",
             apiStatus > 0 ? $"API {apiStatus} error in {project} — try again" : $"API error in {project} — try again",
             ToastLevel.Error),
        NotificationKind.PrFinished => DescribePrFinished(project, pr),
        NotificationKind.PrApproved => DescribePrApproved(project, pr),
        NotificationKind.PrReviewed => DescribePrReviewed(project, pr),
        _ =>
            ("Claude Code — Waiting for Input", $"{project} needs your response", ToastLevel.Warning),
    };

    // The PR-finished toast reflects the terminal state: a merge is the good outcome (info), a close
    // without merge is the "it didn't land" outcome (warning). Falls back to the merge wording if the
    // PR detail somehow isn't attached (PrFinished only ever fires with a PR present).
    private static (string title, string body, ToastLevel level) DescribePrFinished(string project, PullRequestInfo? pr) =>
        pr?.State == PrState.Closed
            ? ("PR: Closed", $"{PrLabel(pr)} closed in {project}", ToastLevel.Warning)
            : ("PR: Merged", $"{PrLabel(pr)} merged in {project}", ToastLevel.Info);

    // "approved by {who} in {project}" — the who is the most recent approver.
    private static (string title, string body, ToastLevel level) DescribePrApproved(string project, PullRequestInfo? pr)
    {
        string by = By(pr?.NewestApproval?.Author);
        return ("PR: Approved", $"{PrLabel(pr)} approved{by} in {project}", ToastLevel.Info);
    }

    // A new review that isn't an approval: changes-requested reads as a warning, a plain comment as info.
    private static (string title, string body, ToastLevel level) DescribePrReviewed(string project, PullRequestInfo? pr) =>
        pr?.NewestReview?.State == PrReviewState.ChangesRequested
            ? ("PR: Changes requested", $"{Reviewer(pr)} requested changes on {PrLabel(pr)} in {project}", ToastLevel.Warning)
            : ("PR: Reviewed", $"{Reviewer(pr)} reviewed {PrLabel(pr)} in {project}", ToastLevel.Info);

    // "PR #123" when the number is known, else a neutral "A PR" (the PR alerts always carry one, but the
    // ntfy/preview paths stay safe if it doesn't).
    private static string PrLabel(PullRequestInfo? pr) => pr is { Number: > 0 } p ? $"PR #{p.Number}" : "A PR";

    // " by alice" when a login is known, else "" — so bodies read naturally either way.
    private static string By(string? login) => string.IsNullOrEmpty(login) ? "" : $" by {login}";

    // The newest reviewer's login, or "Someone" when unknown — the subject of the "reviewed" alert.
    private static string Reviewer(PullRequestInfo? pr) =>
        string.IsNullOrEmpty(pr?.NewestReview?.Author) ? "Someone" : pr!.Value.NewestReview!.Value.Author;

    // Pushes an external notification for a session, but only when the feature is on and that session
    // has opted in (or the account-wide AFK override is on and the screen is locked). Independent of the
    // local-toast per-type toggles.
    private void MaybeSendExternal(NotificationKind kind, ClaudeSession session)
    {
        bool optedIn = session.ExternalNotify;
        bool afkActive = _settings.NotifyWhenLocked && _lock.IsLocked;
        if (!_settings.ExternalNotificationsEnabled || (!optedIn && !afkActive))
            return;

        var (title, body, tags) = kind switch
        {
            NotificationKind.Done =>
                ("Claude Code — Done", $"Waiting for you in {session.DisplayName}", "white_check_mark"),
            NotificationKind.ApiFailed =>
                ("Claude Code — API Error",
                 session.ApiFailure is { Status: > 0 } f
                     ? $"API {f.Status} error in {session.DisplayName} — try again"
                     : $"API error in {session.DisplayName} — try again",
                 "warning"),
            NotificationKind.PrFinished =>
                session.PullRequest?.State == PrState.Closed
                    ? ("PR: Closed",
                       $"{PrLabel(session.PullRequest)} closed in {session.DisplayName}", "wastebasket")
                    : ("PR: Merged",
                       $"{PrLabel(session.PullRequest)} merged in {session.DisplayName}", "tada"),
            NotificationKind.PrApproved =>
                ("PR: Approved",
                 $"{PrLabel(session.PullRequest)} approved{By(session.PullRequest?.NewestApproval?.Author)} in {session.DisplayName}",
                 "white_check_mark"),
            NotificationKind.PrReviewed =>
                session.PullRequest?.NewestReview?.State == PrReviewState.ChangesRequested
                    ? ("PR: Changes requested",
                       $"{Reviewer(session.PullRequest)} requested changes on {PrLabel(session.PullRequest)} in {session.DisplayName}",
                       "warning")
                    : ("PR: Reviewed",
                       $"{Reviewer(session.PullRequest)} reviewed {PrLabel(session.PullRequest)} in {session.DisplayName}",
                       "eyes"),
            _ =>
                ("Claude Code — Waiting for Input", $"{session.DisplayName} needs your response", "bell"),
        };

        // When the screen is locked, prefix the title with "AFK" so the reason you got a push (you're
        // away, not that this session opted in) is obvious at a glance on the phone. Plain ASCII on
        // purpose: NtfyNotifier strips non-ASCII from the title, so a dash/colon here survives where an
        // em dash would blank out.
        if (afkActive)
            title = $"AFK: {title}";

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
