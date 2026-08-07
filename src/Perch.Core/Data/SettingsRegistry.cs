namespace Perch.Data;

/// <summary>
/// The single description of every user-facing setting — the spine the redesigned Settings window is built
/// on. Search filters <see cref="All"/> by name/keywords, the catalogue groups it by
/// <see cref="SettingDescriptor.Surface"/>, and the live preview reads each entry's
/// <see cref="SettingDescriptor.Preview"/> to know which glyph to spotlight. Every entry names the
/// <see cref="AppSettings"/> property it governs (<see cref="SettingDescriptor.Backing"/>), and a test
/// (<c>SettingsRegistryTests</c>) fails the build if a user-facing property is ever added without an entry.
///
/// <para>Toggle and stepper entries carry live get/set bindings; richer kinds (slider, dropdown, hotkey,
/// field, list) are listed for findability and get their bespoke editors as the catalogue is built out.</para>
/// </summary>
internal static class SettingsRegistry
{
    /// <summary>Every setting, in a sensible reading order (grouped by surface).</summary>
    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [
        // ── Session row ──────────────────────────────────────────────────────
        Toggle("permission-mode-badges", "Permission-mode badges",
            "Show Plan / Accept edits / Auto / Bypass next to a session.",
            SettingSurface.SessionRow, ["plan", "accept", "edits", "auto", "bypass", "mode", "badge", "permission"],
            PreviewTarget.ModeBadge, nameof(AppSettings.ShowPermissionModeBadges),
            s => s.ShowPermissionModeBadges, (s, v) => s.ShowPermissionModeBadges = v),

        Toggle("task-progress", "Task progress",
            "The n/m task-checklist count from a session's to-do list.",
            SettingSurface.SessionRow, ["task", "todo", "checklist", "progress", "count"],
            PreviewTarget.TaskProgress, nameof(AppSettings.ShowTaskProgress),
            s => s.ShowTaskProgress, (s, v) => s.ShowTaskProgress = v),

        Toggle("context-pressure", "Context pressure",
            "Thermometer glyph showing how full a session's context window is.",
            SettingSurface.SessionRow,
            ["context", "window", "pressure", "thermometer", "fill", "tokens", "full", "green", "segment"],
            PreviewTarget.ContextPressure, nameof(AppSettings.ShowContextPressure),
            s => s.ShowContextPressure, (s, v) => s.ShowContextPressure = v),

        Info("context-thresholds", "Context pressure thresholds",
            "The yellow / orange / red bands for the context thermometer.",
            SettingSurface.SessionRow, SettingKind.Slider,
            ["context", "threshold", "yellow", "orange", "red", "band", "percent"], PreviewTarget.ContextPressure,
            nameof(AppSettings.ContextPressureYellowPercent), nameof(AppSettings.ContextPressureOrangePercent),
            nameof(AppSettings.ContextPressureRedPercent)),

        Toggle("context-green-segment", "Always show context pressure",
            "Always show the thermometer — green while context is low — instead of hiding it until the first threshold.",
            SettingSurface.SessionRow, ["context", "green", "segment", "below", "threshold", "always", "show"],
            PreviewTarget.ContextPressure, nameof(AppSettings.ShowContextGreenSegment),
            s => s.ShowContextGreenSegment, (s, v) => s.ShowContextGreenSegment = v),

        Toggle("notes", "Pinned notes",
            "Show a session's pinned note glyph and text on its row.",
            SettingSurface.SessionRow, ["note", "pinned", "sticky", "reminder", "text"],
            PreviewTarget.Note, nameof(AppSettings.ShowNotes),
            s => s.ShowNotes, (s, v) => s.ShowNotes = v),

        Toggle("burn-rate", "Burn rate",
            "Live tokens-per-minute for a running session.",
            SettingSurface.SessionRow, ["burn", "rate", "tokens", "minute", "speed", "live"],
            PreviewTarget.BurnRate, nameof(AppSettings.ShowBurnRate),
            s => s.ShowBurnRate, (s, v) => s.ShowBurnRate = v),

        Toggle("waiting-timer", "Waiting timer",
            "How long a blocked session has been waiting on you.",
            SettingSurface.SessionRow, ["waiting", "timer", "idle", "attention", "blocked", "input"],
            PreviewTarget.WaitingTimer, nameof(AppSettings.ShowWaitingTimer),
            s => s.ShowWaitingTimer, (s, v) => s.ShowWaitingTimer = v),

        Stepper("waiting-timer-red", "Waiting timer red threshold",
            "Minutes a session waits before the timer reaches full red.",
            SettingSurface.SessionRow, ["waiting", "timer", "red", "minutes", "threshold"],
            PreviewTarget.WaitingTimer, nameof(AppSettings.WaitingTimerRedMinutes),
            s => s.WaitingTimerRedMinutes, (s, v) => s.WaitingTimerRedMinutes = v),

        Toggle("artifacts", "Artifacts",
            "Clickable glyph for a session's published web artifacts.",
            SettingSurface.SessionRow, ["artifact", "published", "link", "web", "claude.ai"],
            PreviewTarget.Artifacts, nameof(AppSettings.ShowArtifacts),
            s => s.ShowArtifacts, (s, v) => s.ShowArtifacts = v),

        Toggle("git-stats", "Git line churn",
            "The +added / -removed chip from git diff in a session's directory.",
            SettingSurface.SessionRow, ["git", "diff", "lines", "added", "removed", "churn", "numstat"],
            PreviewTarget.GitStats, nameof(AppSettings.ShowGitStats),
            s => s.ShowGitStats, (s, v) => s.ShowGitStats = v),

        Toggle("git-review", "Change review",
            "Adds a \"Review changes…\" item to a session's right-click menu that opens a read-only git diff / history window for its directory.",
            SettingSurface.Advanced, ["git", "diff", "review", "changes", "commit", "history", "audit"],
            PreviewTarget.None, nameof(AppSettings.ShowGitReview),
            s => s.ShowGitReview, (s, v) => s.ShowGitReview = v),

        Toggle("git-review-split", "Change review: split diff",
            "Show the Change Review diff side-by-side (old vs new) instead of unified. Also toggled from the window's own toolbar.",
            SettingSurface.Advanced, ["git", "diff", "split", "side", "unified", "review"],
            PreviewTarget.None, nameof(AppSettings.GitReviewSplitView),
            s => s.GitReviewSplitView, (s, v) => s.GitReviewSplitView = v),

        Toggle("git-review-wrap", "Change review: wrap lines",
            "Wrap long lines in the Change Review diff. Also toggled from the window's own toolbar.",
            SettingSurface.Advanced, ["git", "diff", "wrap", "lines", "review"],
            PreviewTarget.None, nameof(AppSettings.GitReviewWrap),
            s => s.GitReviewWrap, (s, v) => s.GitReviewWrap = v),

        Toggle("media-controller", "Now-playing media",
            "The media controller strip - track plus previous / play-pause / next.",
            SettingSurface.SessionRow, ["media", "music", "now", "playing", "spotify", "track", "controller", "sound"],
            PreviewTarget.MediaController, nameof(AppSettings.ShowMediaController),
            s => s.ShowMediaController, (s, v) => s.ShowMediaController = v),

        Toggle("mic-presence", "Microphone presence",
            "Which app currently holds the microphone.",
            SettingSurface.SessionRow, ["mic", "microphone", "voice", "recording", "presence", "teams", "zoom", "slack"],
            PreviewTarget.MicPresence, nameof(AppSettings.ShowMicPresence),
            s => s.ShowMicPresence, (s, v) => s.ShowMicPresence = v),

        Toggle("daemon-processes", "Daemon workers",
            "List the Claude Code background daemon's headless worker sessions.",
            SettingSurface.SessionRow, ["daemon", "background", "headless", "worker", "processes"],
            PreviewTarget.DaemonProcesses, nameof(AppSettings.ShowDaemonProcesses),
            s => s.ShowDaemonProcesses, (s, v) => s.ShowDaemonProcesses = v),

        Toggle("stuck-detection", "Stuck detection",
            "Amber warning glyph when a session is spinning - error streaks or failing loops.",
            SettingSurface.SessionRow, ["stuck", "runaway", "loop", "error", "streak", "frozen", "spinning"],
            PreviewTarget.Stuck, nameof(AppSettings.StuckDetectionEnabled),
            s => s.StuckDetectionEnabled, (s, v) => s.StuckDetectionEnabled = v),

        Toggle("detect-error-streaks", "Detect error streaks",
            "Flag a session with several tool calls failing in a row.",
            SettingSurface.SessionRow, ["stuck", "error", "streak", "failing", "detect"],
            PreviewTarget.Stuck, nameof(AppSettings.DetectErrorStreaks),
            s => s.DetectErrorStreaks, (s, v) => s.DetectErrorStreaks = v),

        Toggle("detect-failing-loops", "Detect failing loops",
            "Flag a session repeating the same failing action.",
            SettingSurface.SessionRow, ["stuck", "loop", "failing", "repeat", "detect"],
            PreviewTarget.Stuck, nameof(AppSettings.DetectFailingLoops),
            s => s.DetectFailingLoops, (s, v) => s.DetectFailingLoops = v),

        Toggle("hide-inactive-team", "Hide inactive team members",
            "Drop idle teammates from the overlay roster.",
            SettingSurface.SessionRow, ["team", "teammate", "idle", "inactive", "roster", "hide", "agents"],
            PreviewTarget.None, nameof(AppSettings.HideInactiveTeamMembers),
            s => s.HideInactiveTeamMembers, (s, v) => s.HideInactiveTeamMembers = v),

        // ── Usage bars ───────────────────────────────────────────────────────
        Toggle("usage-bars", "Usage bars",
            "The 5-hour and weekly rate-limit usage bars.",
            SettingSurface.UsageBars, ["usage", "limit", "quota", "5-hour", "weekly", "consumption", "rate", "bar"],
            PreviewTarget.UsageBars, nameof(AppSettings.ShowUsage),
            s => s.ShowUsage, (s, v) => s.ShowUsage = v),

        Toggle("expected-rate", "Expected-rate marker",
            "A marker showing where consumption should be for the elapsed time.",
            SettingSurface.UsageBars, ["expected", "rate", "pace", "marker", "ahead", "behind"],
            PreviewTarget.ExpectedRate, nameof(AppSettings.ShowExpectedUsageRate),
            s => s.ShowExpectedUsageRate, (s, v) => s.ShowExpectedUsageRate = v),

        // ── System & metrics ─────────────────────────────────────────────────
        Toggle("system-metrics", "System metrics",
            "Whole-machine CPU + RAM strip at the top of the panel.",
            SettingSurface.SystemMetrics, ["cpu", "ram", "memory", "system", "machine", "metrics", "usage"],
            PreviewTarget.SystemMetrics, nameof(AppSettings.ShowSystemMetrics),
            s => s.ShowSystemMetrics, (s, v) => s.ShowSystemMetrics = v),

        Toggle("session-metrics", "Per-session metrics",
            "A CPU/RAM mini-bar on each session row.",
            SettingSurface.SystemMetrics, ["cpu", "ram", "memory", "session", "per-session", "mini", "bar"],
            PreviewTarget.SessionMetrics, nameof(AppSettings.ShowSessionMetrics),
            s => s.ShowSessionMetrics, (s, v) => s.ShowSessionMetrics = v),

        Toggle("include-subprocess-metrics", "Include subprocesses",
            "Roll a session's metrics up over its whole process tree.",
            SettingSurface.SystemMetrics, ["subprocess", "process", "tree", "rollup", "cpu", "ram", "mcp"],
            PreviewTarget.None, nameof(AppSettings.IncludeSubprocessMetrics),
            s => s.IncludeSubprocessMetrics, (s, v) => s.IncludeSubprocessMetrics = v),

        Toggle("service-status", "Service status footer",
            "Outage footer when status.claude.com reports a problem.",
            SettingSurface.SystemMetrics, ["status", "outage", "incident", "service", "down", "claude.com"],
            PreviewTarget.ServiceStatus, nameof(AppSettings.ShowServiceStatus),
            s => s.ShowServiceStatus, (s, v) => s.ShowServiceStatus = v),

        Stepper("service-status-interval", "Service status poll interval",
            "How often (minutes) to poll status.claude.com.",
            SettingSurface.SystemMetrics, ["status", "poll", "interval", "minutes"],
            PreviewTarget.ServiceStatus, nameof(AppSettings.ServiceStatusIntervalMinutes),
            s => s.ServiceStatusIntervalMinutes, (s, v) => s.ServiceStatusIntervalMinutes = v),

        // ── Notifications ────────────────────────────────────────────────────
        Toggle("notifications-enabled", "Desktop notifications",
            "Master switch for desktop toasts and chimes.",
            SettingSurface.Notifications, ["notification", "toast", "desktop", "alert", "popup", "balloon"],
            PreviewTarget.None, nameof(AppSettings.NotificationsEnabled),
            s => s.NotificationsEnabled, (s, v) => s.NotificationsEnabled = v),

        Toggle("notify-done", "Notify when done",
            "Toast when a session finishes working.",
            SettingSurface.Notifications, ["notify", "done", "finished", "complete", "toast"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnDone),
            s => s.NotifyOnDone, (s, v) => s.NotifyOnDone = v),

        Toggle("notify-waiting", "Notify when waiting for input",
            "Toast when a session is blocked on a prompt.",
            SettingSurface.Notifications, ["notify", "waiting", "input", "prompt", "blocked"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnWaitingInput),
            s => s.NotifyOnWaitingInput, (s, v) => s.NotifyOnWaitingInput = v),

        Toggle("notify-api-error", "Notify on API error",
            "Toast when a session's request errored and it stopped.",
            SettingSurface.Notifications, ["notify", "api", "error", "failure", "529", "overloaded"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnApiError),
            s => s.NotifyOnApiError, (s, v) => s.NotifyOnApiError = v),

        Toggle("notify-pr-finished", "Notify when a PR finishes",
            "Toast when a tracked pull request is merged or closed.",
            SettingSurface.Notifications, ["notify", "pr", "pull request", "merged", "closed", "github"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnPrFinished),
            s => s.NotifyOnPrFinished, (s, v) => s.NotifyOnPrFinished = v),

        Toggle("notify-pr-reviewed", "Notify when a PR is reviewed",
            "Toast when a new review is added to a tracked pull request.",
            SettingSurface.Notifications, ["notify", "pr", "pull request", "review", "reviewed", "comment", "changes"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnPrReviewed),
            s => s.NotifyOnPrReviewed, (s, v) => s.NotifyOnPrReviewed = v),

        Toggle("notify-pr-approved", "Notify when a PR is approved",
            "Toast when a tracked pull request is approved.",
            SettingSurface.Notifications, ["notify", "pr", "pull request", "approved", "review", "lgtm"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnPrApproved),
            s => s.NotifyOnPrApproved, (s, v) => s.NotifyOnPrApproved = v),

        Toggle("pr-finished-banner", "Flash a PR state banner",
            "Flash a banner over the overlay row on a PR state change (merged, closed, reviewed, approved).",
            SettingSurface.Notifications, ["pr", "pull request", "merged", "closed", "approved", "banner", "overlay", "indicator"],
            PreviewTarget.None, nameof(AppSettings.PrFinishedOverlayBanner),
            s => s.PrFinishedOverlayBanner, (s, v) => s.PrFinishedOverlayBanner = v),

        Toggle("chime-done", "Chime when done",
            "Play a sound when a session finishes.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "done"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnDone),
            s => s.ChimeOnDone, (s, v) => s.ChimeOnDone = v),

        Toggle("chime-waiting", "Chime when waiting",
            "Play a sound when a session needs input.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "waiting"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnWaitingInput),
            s => s.ChimeOnWaitingInput, (s, v) => s.ChimeOnWaitingInput = v),

        Toggle("chime-api-error", "Chime on API error",
            "Play a sound when a session errors.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "error"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnApiError),
            s => s.ChimeOnApiError, (s, v) => s.ChimeOnApiError = v),

        Toggle("chime-pr-finished", "Chime when a PR finishes",
            "Play a sound when a pull request is merged or closed.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "pr", "merged", "closed"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnPrFinished),
            s => s.ChimeOnPrFinished, (s, v) => s.ChimeOnPrFinished = v),

        Toggle("chime-pr-reviewed", "Chime when a PR is reviewed",
            "Play a sound when a review is added to a pull request.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "pr", "review", "reviewed"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnPrReviewed),
            s => s.ChimeOnPrReviewed, (s, v) => s.ChimeOnPrReviewed = v),

        Toggle("chime-pr-approved", "Chime when a PR is approved",
            "Play a sound when a pull request is approved.",
            SettingSurface.Notifications, ["chime", "sound", "beep", "audio", "bell", "pr", "approved"],
            PreviewTarget.None, nameof(AppSettings.ChimeOnPrApproved),
            s => s.ChimeOnPrApproved, (s, v) => s.ChimeOnPrApproved = v),

        Toggle("external-notifications", "External push (ntfy)",
            "Send pushes to your phone via ntfy.",
            SettingSurface.Notifications, ["ntfy", "push", "phone", "mobile", "remote", "external"],
            PreviewTarget.None, nameof(AppSettings.ExternalNotificationsEnabled),
            s => s.ExternalNotificationsEnabled, (s, v) => s.ExternalNotificationsEnabled = v),

        Info("ntfy-host", "ntfy server",
            "The ntfy host that external pushes are sent to.",
            SettingSurface.Notifications, SettingKind.Field,
            ["ntfy", "host", "server", "url", "address"], PreviewTarget.None, nameof(AppSettings.NtfyHost)),

        Info("ntfy-topic", "ntfy topic",
            "The ntfy topic external pushes are published to.",
            SettingSurface.Notifications, SettingKind.Field,
            ["ntfy", "topic", "channel"], PreviewTarget.None, nameof(AppSettings.NtfyTopic)),

        Toggle("notify-when-locked", "Push while screen locked",
            "Push any session while the screen is locked (AFK).",
            SettingSurface.Notifications, ["locked", "afk", "away", "push", "screen", "lock"],
            PreviewTarget.None, nameof(AppSettings.NotifyWhenLocked),
            s => s.NotifyWhenLocked, (s, v) => s.NotifyWhenLocked = v),

        Toggle("external-remote-link", "Include claude.ai link in push",
            "Add a claude.ai deep link to a remote-controlled session's push.",
            SettingSurface.Notifications, ["remote", "link", "claude.ai", "deep", "push"],
            PreviewTarget.None, nameof(AppSettings.ExternalNotificationsIncludeRemoteLink),
            s => s.ExternalNotificationsIncludeRemoteLink, (s, v) => s.ExternalNotificationsIncludeRemoteLink = v),

        // ── Tray & stats ─────────────────────────────────────────────────────
        Toggle("today-stats-tray", "Today's stats in tray",
            "The 'Today: N sessions - active' line in the tray menu.",
            SettingSurface.TrayAndStats, ["tray", "stats", "today", "menu", "summary", "sessions"],
            PreviewTarget.None, nameof(AppSettings.ShowTodayStatsInTray),
            s => s.ShowTodayStatsInTray, (s, v) => s.ShowTodayStatsInTray = v),

        Toggle("estimated-cost", "Estimated cost",
            "The equivalent-API-cost figure in the stats window.",
            SettingSurface.TrayAndStats, ["cost", "price", "dollars", "money", "api", "spend", "estimate"],
            PreviewTarget.None, nameof(AppSettings.ShowEstimatedCost),
            s => s.ShowEstimatedCost, (s, v) => s.ShowEstimatedCost = v),

        Stepper("active-idle-minutes", "Active-time idle threshold",
            "Gaps longer than this count as stepped-away rather than active.",
            SettingSurface.TrayAndStats, ["active", "idle", "minutes", "threshold", "time", "stats"],
            PreviewTarget.None, nameof(AppSettings.StatsActiveIdleMinutes),
            s => s.StatsActiveIdleMinutes, (s, v) => s.StatsActiveIdleMinutes = v),

        // ── Whimsy ───────────────────────────────────────────────────────────
        Toggle("perch-reacts", "Perch reacts",
            "The bird reflects the aggregate session mood.",
            SettingSurface.Whimsy, ["bird", "mood", "reacts", "whimsy", "perch", "emotion"],
            PreviewTarget.PerchReacts, nameof(AppSettings.PerchReacts),
            s => s.PerchReacts, (s, v) => s.PerchReacts = v),

        Toggle("confetti-finish", "Confetti finish",
            "Adds a per-session 'confetti finish' arming to the right-click menu.",
            SettingSurface.Whimsy, ["confetti", "celebrate", "finish", "party", "fun"],
            PreviewTarget.None, nameof(AppSettings.ConfettiFinish),
            s => s.ConfettiFinish, (s, v) => s.ConfettiFinish = v),

        Toggle("notify-achievement", "Achievement reveal card",
            "Full-screen card reveal when new achievement badges unlock.",
            SettingSurface.Whimsy, ["achievement", "unlock", "badge", "reveal", "card", "trophy"],
            PreviewTarget.None, nameof(AppSettings.NotifyOnAchievement),
            s => s.NotifyOnAchievement, (s, v) => s.NotifyOnAchievement = v),

        Toggle("achievement-toasts", "Achievement toasts",
            "Also pop a desktop toast for each unlock.",
            SettingSurface.Whimsy, ["achievement", "toast", "unlock", "badge", "popup"],
            PreviewTarget.None, nameof(AppSettings.AchievementToasts),
            s => s.AchievementToasts, (s, v) => s.AchievementToasts = v),

        Toggle("upside-down-quick-links", "Upside-down quick links",
            "Rotate the quick-link icons 180 degrees. For fun.",
            SettingSurface.Whimsy, ["upside", "down", "rotate", "quick", "links", "flip", "fun"],
            PreviewTarget.QuickLinks, nameof(AppSettings.UpsideDownQuickLinks),
            s => s.UpsideDownQuickLinks, (s, v) => s.UpsideDownQuickLinks = v),

        // ── Integrations ─────────────────────────────────────────────────────
        Info("quick-links", "Quick links",
            "The launcher icons shown below the usage bars.",
            SettingSurface.Integrations, SettingKind.List,
            ["quick", "links", "launch", "apps", "shortcut", "icons", "tools"], PreviewTarget.QuickLinks,
            nameof(AppSettings.QuickLinks)),

        Toggle("hypertree", "Hypertree",
            "The Hypertree branch section under the quick links.",
            SettingSurface.Integrations, ["hypertree", "branch", "worktree", "desktop", "integration"],
            PreviewTarget.None, nameof(AppSettings.HypertreeEnabled),
            s => s.HypertreeEnabled, (s, v) => s.HypertreeEnabled = v),

        Toggle("pull-requests", "Pull requests",
            "A GitHub PR merge glyph on a session whose branch has a PR.",
            SettingSurface.Integrations, ["github", "pr", "pull", "request", "merge", "branch"],
            PreviewTarget.PullRequest, nameof(AppSettings.ShowPullRequests),
            s => s.ShowPullRequests, (s, v) => s.ShowPullRequests = v),

        Stepper("pull-request-interval", "PR re-check interval",
            "How often (minutes) to re-check a branch's PR with the gh CLI.",
            SettingSurface.Integrations, ["pr", "pull", "request", "interval", "minutes", "recheck"],
            PreviewTarget.PullRequest, nameof(AppSettings.PullRequestIntervalMinutes),
            s => s.PullRequestIntervalMinutes, (s, v) => s.PullRequestIntervalMinutes = v),

        // ── Advanced ─────────────────────────────────────────────────────────
        Info("theme", "Theme",
            "The app's colour theme — pick a preset or design your own on the Appearance page.",
            SettingSurface.Advanced, SettingKind.Dropdown,
            ["theme", "colour", "color", "palette", "dark", "appearance", "contrast", "accessibility", "ember", "midnight"],
            PreviewTarget.None, nameof(AppSettings.ActiveThemeId)),

        Info("start-mode", "Start Perch",
            "When Perch launches itself - off, on session start, or at login.",
            SettingSurface.Advanced, SettingKind.Dropdown,
            ["start", "startup", "launch", "login", "boot", "autostart", "session"], PreviewTarget.None,
            nameof(AppSettings.StartMode)),

        // Opens the drag-to-place editor (also reachable from the overlay header's right-click menu).
        // Backs both placement properties so the coverage test is satisfied without a NotSettings entry.
        Info("overlay-placement", "Initial overlay placement",
            "Choose where the overlay and the dense strip first appear, by dragging a preview.",
            SettingSurface.Advanced, SettingKind.List,
            ["placement", "position", "corner", "dock", "move", "initial", "location", "overlay", "dense", "where"],
            PreviewTarget.None,
            nameof(AppSettings.FloatingPlacement), nameof(AppSettings.DensePlacement)),

        // Rendered as a two-option segmented toggle (see SettingsCatalogView.DropdownEditor), not a combo.
        Info("dense-status-style", "Dense strip status changes",
            "How the collapsed dense strip reacts when a session finishes, blocks, or errors: expand the panel, or float a fading speech bubble off the logo.",
            SettingSurface.Advanced, SettingKind.Dropdown,
            ["dense", "strip", "status", "change", "expand", "bubble", "speech", "popup", "notification", "toast"],
            PreviewTarget.None, nameof(AppSettings.DenseStatusChangeStyle)),

        Toggle("auto-close", "Auto-close after last session",
            "Exit a short while after the last session ends (only when auto-started).",
            SettingSurface.Advanced, ["auto", "close", "exit", "quit", "shutdown", "last", "session"],
            PreviewTarget.None, nameof(AppSettings.AutoCloseAfterLastSession),
            s => s.AutoCloseAfterLastSession, (s, v) => s.AutoCloseAfterLastSession = v),

        // Keyboard shortcuts and the reopen-terminal choice live on the dedicated Shortcuts page (they want
        // per-binding enable + key capture, not a catalogue card), so they're intentionally not registry
        // entries — see the NotSettings exclusion in SettingsRegistryTests.

        Toggle("changelog-on-update", "Show changelog after update",
            "Pop the 'what's new' window on the first launch after an update.",
            SettingSurface.Advanced, ["changelog", "whats", "new", "update", "release", "notes"],
            PreviewTarget.None, nameof(AppSettings.ShowChangelogOnUpdate),
            s => s.ShowChangelogOnUpdate, (s, v) => s.ShowChangelogOnUpdate = v),

        // Not an AppSettings flag — this writes Claude Code's own settings.json env var, so it binds through
        // the raw accessors and carries no Backing.
        new SettingDescriptor("agent-teams", "Enable Agent Teams in Claude Code",
            "Turn on Claude Code's experimental Agent Teams (multi-agent) feature via its settings.json.",
            SettingSurface.Advanced, SettingKind.Toggle,
            ["agent", "teams", "multi", "experimental", "claude code", "env"], PreviewTarget.None, Backing: [],
            GetBoolRaw: ClaudeUserSettings.IsAgentTeamsEnabled,
            SetBoolRaw: v => ClaudeUserSettings.SetAgentTeamsEnabled(v)),
    ];

    /// <summary>Finds a descriptor by its stable <see cref="SettingDescriptor.Id"/>, or null.</summary>
    public static SettingDescriptor? ById(string id)
    {
        foreach (var d in All)
            if (d.Id == id) return d;
        return null;
    }

    /// <summary>Descriptors whose name/keywords/surface match <paramref name="query"/> (all match when blank).</summary>
    public static IEnumerable<SettingDescriptor> Search(string query)
    {
        foreach (var d in All)
            if (d.MatchesQuery(query))
                yield return d;
    }

    private static SettingDescriptor Toggle(string id, string name, string desc, SettingSurface surface,
        string[] keywords, PreviewTarget preview, string backing,
        Func<AppSettings, bool> get, Action<AppSettings, bool> set)
        => new(id, name, desc, surface, SettingKind.Toggle, keywords, preview, [backing],
            GetBool: get, SetBool: set);

    private static SettingDescriptor Stepper(string id, string name, string desc, SettingSurface surface,
        string[] keywords, PreviewTarget preview, string backing,
        Func<AppSettings, int> get, Action<AppSettings, int> set)
        => new(id, name, desc, surface, SettingKind.Stepper, keywords, preview, [backing],
            GetInt: get, SetInt: set);

    // A findable-but-not-yet-live-bound entry (slider / dropdown / hotkey / field / list). Editing happens
    // in the entry's bespoke control as the catalogue is built out; search only needs it to be listed.
    private static SettingDescriptor Info(string id, string name, string desc, SettingSurface surface,
        SettingKind kind, string[] keywords, PreviewTarget preview, params string[] backing)
        => new(id, name, desc, surface, kind, keywords, preview, backing);
}
