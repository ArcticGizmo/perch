namespace Perch.Data;

using System.Text.Json;
using System.Text.Json.Serialization;
using Perch.Platform;

/// <summary>
/// The terminal Perch launches to reopen a closed session (<c>claude --resume</c>). The ordinal is what's
/// persisted in settings, so keep the order stable and append new members at the end.
/// </summary>
public enum TerminalApp
{
    /// <summary>Best available: Windows Terminal if present, otherwise Command Prompt.</summary>
    Auto = 0,
    WindowsTerminal = 1,
    PowerShell = 2,
    CommandPrompt = 3,
}

/// <summary>
/// When Perch launches itself. Persisted by <em>name</em> (see the converter) rather than ordinal, because
/// perch-hook reads the raw settings.json with a string-only mini-parser — so keep the member names stable.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StartMode
{
    /// <summary>Never launch on its own — Perch only runs when you start it.</summary>
    Off = 0,
    /// <summary>perch-hook launches the tray when a Claude Code session opens and none is running.</summary>
    OnSessionStart = 1,
    /// <summary>The OS launches the tray when you log in (Run key on Windows, LaunchAgent on macOS).</summary>
    OnLogin = 2,
}

/// <summary>
/// How the dense strip announces a session status change (finished / awaiting input / API error).
/// Persisted by <em>name</em> so the member order can change without breaking an older settings file.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DenseStatusChangeStyle
{
    /// <summary>Pop the hover panel open (the original behaviour) so the changed session is visible.</summary>
    Expand = 0,
    /// <summary>Float a small speech bubble off the perch-logo row that fades away after a couple of seconds,
    /// leaving the strip collapsed.</summary>
    Bubble = 1,
}

internal sealed class AppSettings
{
    // Per-profile so a dev instance doesn't read/write the installed Perch's settings (see AppProfile).
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppProfile.DataFolderName, "settings.json");

    // Appearance. The active colour theme, by its stable id (see Perch.Theming.Themes). Defaults to
    // "midnight" — Perch's original palette — and an unknown/missing id falls back to it, so an older
    // settings file (or a custom theme that's since been deleted) never leaves the app uncoloured.
    public string ActiveThemeId { get; set; } = "midnight";

    // User-designed themes from the Appearance page's designer, appended to the built-in catalogue and
    // resolvable by id (see Perch.Theming.ThemeCatalog). Null/empty means none. Kept resilient: a saved
    // theme missing a role added in a later version simply degrades that one role (not the whole load).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Theming.Theme>? CustomThemes { get; set; }

    // Whether to show (and fetch, via the OAuth /usage endpoint) the session/weekly usage bars.
    // Defaults to true; a missing key in an older settings file keeps this default.
    public bool ShowUsage { get; set; } = true;

    // When on, a thin marker on each usage bar shows where consumption should be given the
    // elapsed time in the current window (e.g. after 2 days of a 7-day period the marker sits
    // at ~28%). Defaults to true; only visible while ShowUsage is also true.
    public bool ShowExpectedUsageRate { get; set; } = true;

    // Whether to surface per-session context-window pressure — the thermometer glyph that appears on
    // a session row once its context fill crosses the warning threshold. Off hides the glyph entirely
    // (the fill is still computed; it just isn't drawn). Defaults to true; a missing key keeps it on.
    public bool ShowContextPressure { get; set; } = true;

    // Context-pressure thresholds, as whole percentages of the context window. The thermometer is
    // hidden below Yellow, then warms Yellow -> Orange -> Red as the fill climbs. Kept ordered
    // (Yellow < Orange < Red) by the settings slider. Defaults match the original hard-coded bands.
    public int ContextPressureYellowPercent { get; set; } = 50;
    public int ContextPressureOrangePercent { get; set; } = 65;
    public int ContextPressureRedPercent    { get; set; } = 80;

    // Whether to draw a green thermometer for the "first segment" — the below-yellow band that is
    // normally left blank. On makes the glyph appear as soon as any context is known (green until it
    // reaches Yellow), so low-but-nonzero fill is visible instead of hidden. Handy for confirming the
    // fill is being read at all. Defaults to false; a missing key keeps it off.
    public bool ShowContextGreenSegment { get; set; } = false;

    // Whether to draw the permission-mode badge (Plan / Accept edits / Auto / Bypass) next to a
    // session in the overlay. Off hides the badge and lets the session name reclaim its width; the
    // mode itself is still tracked, just not shown. Defaults to true; a missing key keeps it on.
    public bool ShowPermissionModeBadges { get; set; } = true;

    // Whether to draw the task-list "n/m" progress count (from a session's native TaskCreate/TaskUpdate
    // checklist) next to a session in the overlay. Off hides the count and lets the session name reclaim
    // its width; the checklist is still tracked, just not shown. Defaults to true; a missing key keeps it on.
    public bool ShowTaskProgress { get; set; } = true;

    // Whether to show a session's pinned note on its overlay row — the clickable note glyph and its text
    // line. Off (the default) hides the indicator entirely; the note is still stored and editable from the
    // session's right-click menu. A missing key keeps it off.
    public bool ShowNotes { get; set; }

    // Whether to list the Claude Code background daemon's headless worker sessions in their own "daemon"
    // section under the overlay's rows. Off hides the section and stops watching the roster — display
    // only; the workers themselves keep running. Defaults to true; a missing key keeps it on.
    public bool ShowDaemonProcesses { get; set; } = true;

    // The global scratch pad — free-form multi-line text opened from the note button leading the overlay's
    // quick-links row. Not tied to any session; persisted here so it survives a restart. Null/empty means
    // the pad is empty (nothing written to the file when empty). See StickyNoteWindow.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScratchText { get; set; }

    // Whether to draw the live token burn rate (tokens/min) next to a running session in the overlay —
    // measured over its most recent burst of assistant turns. A glanceable read on how hard a session is
    // leaning on the plan's token limits. Off by default (opt-in); a missing key keeps it off.
    public bool ShowBurnRate { get; set; } = false;

    // Whether to draw the "waiting on you" timer next to a session that's blocked awaiting input —
    // how long it's been waiting, warming from yellow toward red as it grows. Off hides it; the session
    // still shows its "input ↩" status. Defaults to true; a missing key keeps it on.
    public bool ShowWaitingTimer { get; set; } = true;

    // How many minutes a session must sit blocked before the "waiting on you" timer reaches full red;
    // the colour ramps yellow -> red linearly over this span. Clamped to a sane floor when applied.
    // Defaults to 10; a missing key keeps that.
    public int WaitingTimerRedMinutes { get; set; } = 10;

    // Whether to draw the clickable artifact glyph next to a session that has published one or more web
    // artifacts. Off hides the glyph (the row click just focuses the terminal) and lets the session name
    // reclaim its width; the artifacts are still tracked. Defaults to true; a missing key keeps it on.
    public bool ShowArtifacts { get; set; } = true;

    // Whether to show the now-playing media controller strip on the overlay — what's currently playing
    // (from the Windows media session: Spotify, a browser media tab, etc.) plus previous / play-pause /
    // next controls. Off by default (opt-in); the strip is only visible while something is actually
    // playing. A missing key keeps it off.
    public bool ShowMediaController { get; set; } = false;

    // Whether to show the microphone strip on the overlay — which app currently holds the mic, whose name is a
    // link to its window. App-agnostic: Teams, Slack, Zoom, a browser tab and OBS all surface the same way. Off
    // by default (opt-in); the strip is only visible while the mic is in use.
    //
    // A companion TeamsCallControls/TeamsApiToken pair used to sit here, driving Teams' local API for in-app mute
    // and meeting state. Both are gone; a settings file still carrying them is simply ignored, and the keys
    // disappear the next time settings are saved.
    public bool ShowMicPresence { get; set; } = false;

    // Whether to show the outage footer at the bottom of the overlay when status.claude.com reports a
    // problem (clicking it lists the incidents + links to the status page). Off stops the poll entirely
    // and hides the footer. On by default; a missing key in an older settings file keeps it on.
    public bool ShowServiceStatus { get; set; } = true;

    // How often (minutes) to poll status.claude.com. Clamped to a sane range when applied. Polls are
    // conditional (ETag/If-None-Match), so an unchanged status costs an empty 304 — the interval mainly
    // bounds how quickly a new incident surfaces. Defaults to 2; a missing key keeps that.
    public int ServiceStatusIntervalMinutes { get; set; } = 2;

    // Stuck/runaway detection. When on, a session that's spinning — several tool calls failing in a
    // row, or the same action repeated and failing — gets an amber warning glyph in the overlay. The
    // two sub-switches scope which heuristics run, so a user plagued by false positives on one can
    // keep the other. On by default; a missing key in an older settings file keeps it on.
    public bool StuckDetectionEnabled { get; set; } = true;
    public bool DetectErrorStreaks   { get; set; } = true;
    public bool DetectFailingLoops   { get; set; } = true;

    // Master switch for Windows desktop (toast/balloon) notifications and chimes. When off, no
    // session balloon is ever shown and no chime is played; the overlay's own attention flash is
    // unaffected. The per-type switches below only take effect while this is on.
    public bool NotificationsEnabled { get; set; } = true;

    // Per-type switches: "Done" fires when a session finishes working (busy -> idle);
    // "WaitingForInput" fires when a session is blocked on a prompt (e.g. a permission request).
    public bool NotifyOnDone { get; set; } = true;
    public bool NotifyOnWaitingInput { get; set; } = true;
    // "ApiFailed" fires when a session's last request to the API errored (e.g. 529 Overloaded) and it
    // stopped — on by default, since a failed run is exactly the moment the user wants pulled to.
    public bool NotifyOnApiError { get; set; } = true;
    // "PrFinished" fires when a pull request Perch tracks for a session's directory reaches a finalised
    // state — merged or closed. On by default: a PR landing (or being closed) is a natural "you can move
    // on" moment. Only observable while the GitHub PR integration (ShowPullRequests) is on.
    public bool NotifyOnPrFinished { get; set; } = true;
    // "PrReviewed" fires when a new review (a comment or changes-requested) is added to a tracked PR;
    // "PrApproved" when someone approves it. Both name the reviewer/approver. On by default. Like the other
    // PR alerts, only observable while the GitHub PR integration (ShowPullRequests) is on.
    public bool NotifyOnPrReviewed { get; set; } = true;
    public bool NotifyOnPrApproved { get; set; } = true;

    // A second, independent surface for the PR state-change events (finished / reviewed / approved): a
    // transient banner flashed over the overlay row itself (the "front and centre" indicator), separate
    // from the desktop toasts above so either can be used alone. Like the attention flash it is an overlay
    // behaviour, so it is NOT gated by NotificationsEnabled — only by this toggle (and the PR integration
    // being on). On by default.
    public bool PrFinishedOverlayBanner { get; set; } = true;

    // Per-type sound switches: play the built-in Windows system chime when that notification type
    // fires (Done -> Asterisk, WaitingForInput -> Exclamation). Independent of the balloon switches
    // above but gated by NotificationsEnabled. Off by default — the chime opts in per type. External
    // (ntfy) pushes never chime; sound is for the local desktop only.
    public bool ChimeOnDone { get; set; }
    public bool ChimeOnWaitingInput { get; set; }
    public bool ChimeOnApiError { get; set; }
    public bool ChimeOnPrFinished { get; set; }
    public bool ChimeOnPrReviewed { get; set; }
    public bool ChimeOnPrApproved { get; set; }

    // External notifications via ntfy (https://ntfy.sh). The master switch gates whether any
    // external push is sent and whether the per-session toggle is offered in the overlay; the
    // host and topic stay saved and editable while it's off. Which sessions actually push is an
    // in-memory, per-session opt-in (right-click a session) and isn't persisted here.
    public bool ExternalNotificationsEnabled { get; set; }
    public string? NtfyHost  { get; set; }
    public string? NtfyTopic { get; set; }

    // Account-wide AFK override: when on, *any* session's external push fires while the Windows
    // session is locked, even sessions that haven't been individually opted in via the overlay's
    // right-click menu. Still gated by ExternalNotificationsEnabled (and the host/topic). Off by
    // default. See [[LockMonitor]].
    public bool NotifyWhenLocked { get; set; }

    // When on, a remote-controlled session's external push carries a "view" action that opens the
    // session on claude.ai (https://claude.ai/code/{bridgeSessionId}). Off by default — not
    // everyone wants the deep link in their notifications — and only relevant while the session
    // is actually connected via /remote-control. Gated by ExternalNotificationsEnabled.
    public bool ExternalNotificationsIncludeRemoteLink { get; set; }

    // Automation. StartMode: how Perch launches itself.
    //  • OnSessionStart — perch-hook reads this value straight out of settings.json (the tray usually isn't
    //    running when a session opens) and launches the installed perch. See Perch.Hook's HandleStart.
    //  • OnLogin — the OS starts the tray at login; the registration lives outside the app (a per-user Run
    //    key on Windows, a LaunchAgent on macOS) and is written/removed by ILoginItem as this value changes.
    //  • Off (the default) — Perch only ever runs when you start it.
    // AutoClose: the running tray exits a short grace period after the last session ends — but only when it
    // was itself auto-started (--autostarted), so neither a manually-opened nor a login-started window
    // vanishes under the user. See [[App]]'s auto-close flow.
    public StartMode StartMode { get; set; } = StartMode.Off;
    public bool AutoCloseAfterLastSession { get; set; }

    // Legacy auto-start switch (pre-StartMode). Kept only so an older settings file can be migrated;
    // nullable to tell "absent" from "false", and cleared once folded in so it's not re-written.
    // See MigrateStartMode.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoStartOnFirstSession { get; set; }

    // Session stats. ShowTodayStatsInTray: the "Today: N sessions · Hh Mm active" info line in the tray
    // right-click menu. ShowEstimatedCost: the equivalent-API-cost figure in the stats window.
    // StatsActiveIdleMinutes: the idle threshold for the "active time" estimate — gaps between transcript
    // records longer than this are capped (the user stepped away). Defaults match the original behaviour,
    // so an older settings file with these keys absent keeps the 5-minute window and both lines on.
    public bool ShowTodayStatsInTray { get; set; } = true;
    public bool ShowEstimatedCost    { get; set; } = true;
    public int  StatsActiveIdleMinutes { get; set; } = 5;

    // Monitoring. System resource metrics surfaced in the overlay:
    //  • ShowSystemMetrics    — the whole-machine CPU + RAM strip at the top of the panel.
    //  • ShowSessionMetrics   — a per-session CPU/RAM mini-bar on each session row (hover for numbers).
    //  • IncludeSubprocessMetrics — roll a session's number up over its whole process tree (the MCP
    //    servers, shells and tools its claude process spawns) rather than the claude process alone.
    // All off by default — monitoring is opt-in, so no counters are sampled until the user turns one on
    // (sampling runs only while system or per-session is enabled). A missing key keeps these defaults.
    public bool ShowSystemMetrics        { get; set; }
    public bool ShowSessionMetrics       { get; set; }
    public bool IncludeSubprocessMetrics { get; set; }

    // Experimental. When on, idle (waiting-for-the-lead) teammates are dropped from the overlay roster
    // instead of lingering as greyed rows — only teammates actively working are shown. Purely a display
    // filter: the teammates are still tracked, and a hidden one reappears the moment it starts working
    // again. Defaults to false (show the full roster); a missing key keeps the roster complete.
    public bool HideInactiveTeamMembers { get; set; }

    // Whether to draw the per-session unstaged git line-churn chip (+added / -deleted) next to a session
    // in the overlay, read from `git diff --numstat` in the session's working directory. Off by default
    // (experimental) and, importantly, load-bearing: while off no git process is ever launched, so the
    // feature costs nothing when disabled. A missing key keeps it off.
    public bool ShowGitStats { get; set; }

    // Whether the per-session right-click menu offers "Review changes…", which opens the read-only git
    // Change Review window for that session's working directory. On by default. Purely gates the menu
    // action — nothing runs in the background either way (and even when used, git is only invoked on demand
    // while the window is open), so it's cheap. A missing key defaults it on.
    public bool ShowGitReview { get; set; } = true;

    // The Change Review window's diff layout: false = unified (one column), true = side-by-side split.
    // Persisted so the choice sticks between openings; toggled from the window's own toolbar. A missing key
    // defaults to unified.
    public bool GitReviewSplitView { get; set; }

    // Whether the Change Review diff wraps long lines. On by default; toggled from the window's "Wrap"
    // checkbox and persisted. A missing key keeps wrapping on.
    public bool GitReviewWrap { get; set; } = true;

    // Whether the git Tree window renders in light mode (just that window — the rest of the app keeps its
    // theme). Off by default; toggled from the window's own light/dark button and persisted. A missing key
    // keeps it dark.
    public bool GitTreeLight { get; set; }

    // Whether the git Tree diff shows per-hunk (and line) staging controls. Off by default — staging a whole
    // file is the common case; hunk/line staging is opt-in. Toggled from the window's "Hunk staging"
    // checkbox and persisted. A missing key keeps it off.
    public bool GitTreeHunkStaging { get; set; }

    // Quick links. Icons displayed below the usage bars; each opens the app or focuses it. The list
    // is the source of truth; null means "never configured" and triggers a one-time seed (see
    // MigrateQuickLinks) with the well-known presets, honouring the legacy switches below. An empty
    // (non-null) list means the user deliberately removed every link and is left alone.
    public List<QuickLink>? QuickLinks { get; set; }

    // Renders the quick-link icons rotated 180°. Purely for fun — the icons happen to look upside down
    // already, so this leans in. Off by default.
    public bool UpsideDownQuickLinks { get; set; }

    // User-defined initial placement for the two overlay presentations, set from the "Set initial
    // placements…" editor (header right-click). Each is stored relative to the nearest monitor corner
    // (anchor + DIP offset; see OverlayPlacement) so it survives a resolution change. Null means "use
    // the computed default" — floating pins to the primary monitor's top-right, dense docks to the
    // right edge — so a settings file predating the feature simply keeps today's behaviour. The editor
    // is the sole source of truth: a normal drag of the overlay is not persisted here. See
    // PlacementMath, OverlayCanvas.PlaceAtDefaultFloating and DenseController.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayPlacement? FloatingPlacement { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverlayPlacement? DensePlacement { get; set; }

    // How the dense strip surfaces a session status change: Expand pops the hover panel open (the original
    // behaviour); Bubble floats a small speech bubble off the perch-logo row that fades after a couple of
    // seconds, leaving the strip collapsed. Defaults to Expand, so an older settings file keeps today's
    // behaviour. See OverlayCanvas.TriggerAttention and DenseController.ShowBubble.
    public DenseStatusChangeStyle DenseStatusChangeStyle { get; set; } = DenseStatusChangeStyle.Expand;

    // Integrations. Hypertree (the virtual-desktop branch manager) — when on, the overlay grows a
    // "Hypertree" section under the quick links listing its branches (main included), marking the one
    // you're on and jumping to any of them on click. Off by default and load-bearing while off: nothing
    // polls Hypertree's status file and no `htree` process is ever spawned until it's turned on, so the
    // integration costs nothing to the vast majority who don't have Hypertree. A missing key keeps it off.
    // See Perch.Data.Hypertree and docs/hypertree-integration.md.
    public bool HypertreeEnabled { get; set; }

    // GitHub pull requests. When on, each session whose working directory is a GitHub repo grows a merge
    // glyph on its overlay row once its current branch has a PR — clicking it opens a small flyout (title,
    // state, number) that links out to the PR in a browser. Off by default and load-bearing while off:
    // nothing runs the gh CLI until it's turned on, so it costs nothing to those who don't want it. A PR is
    // checked when a session first appears and then only every PullRequestIntervalMinutes, with "no PR"
    // cached just as long, so gh runs rarely. A missing key keeps it off. See Perch.Data.PrStatusService.
    public bool ShowPullRequests { get; set; }

    // How often (minutes) to re-check a working directory's PR with gh; also how long a "no PR" answer is
    // cached before the next check. Clamped to a sane floor when applied. Defaults to 5; a missing key
    // keeps that.
    public int PullRequestIntervalMinutes { get; set; } = 5;

    // Global keyboard shortcuts (system-wide, work even when Perch isn't focused). Each is registered on
    // startup and re-registered live when the Hotkeys settings page edits it. A binding that's disabled or
    // invalid simply isn't registered; the OS refusing a combo (another app owns it) is ignored. The
    // defaults are the house style (Alt+Shift + a key); a missing key in an older settings file keeps them.
    //  • Dense    — collapse/expand the overlay (this was the app's only shortcut before, Alt+Shift+W).
    //  • Cycle    — focus the next active session's terminal, round-robin.
    //  • Switcher — pop the centred keyboard session switcher (Alt+Shift+Space).
    public HotkeyBinding HotkeyToggleDense { get; set; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'W');
    public HotkeyBinding HotkeyCycleSessions { get; set; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'S');
    public HotkeyBinding HotkeyOpenSwitcher { get; set; } = new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, ' ');

    // Which terminal the session switcher launches when reopening a closed session (`claude --resume <id>`
    // in its working directory). Auto picks the best available (Windows Terminal, else Command Prompt); an
    // explicit choice still falls back to a plain console if it can't be launched, and reopening degrades to
    // copying the command when no terminal is available at all. Serialised as its enum ordinal; a missing key
    // keeps Auto. See <see cref="Perch.Platform.ISessionLauncher"/>.
    public TerminalApp ReopenTerminal { get; set; } = TerminalApp.Auto;

    // "Perch reacts": the tray and overlay bird wears the aggregate session mood — dozing (faded, a
    // trail of z's) when nothing's running, plainly alert while sessions work, a "!" badge when one
    // needs you, and visibly panicking (red bang + flying sweat) when a session looks stuck. Pure
    // whimsy layered over the existing status cues. On by default; a missing key keeps it on.
    public bool PerchReacts { get; set; } = true;

    // "Confetti finish": when on, a session's right-click menu gains a "Confetti finish 🎉" toggle.
    // Arm a session and, the instant it next finishes, a burst of confetti erupts across the screen — then
    // the arming is spent (it fires exactly once, and disarms itself). Only this master switch is
    // persisted; the per-session arming is deliberately in-memory only, so a celebration can never go off
    // by surprise after a restart. Off by default (experimental); a missing key keeps it off.
    public bool ConfettiFinish { get; set; }

    // "Celebrate new unlocks": when on, unlocking new achievement badges plays the full-screen card reveal
    // (up to a few cards side by side, plus a "+N more" card for a big batch). Off unlocks silently (the
    // badges are still recorded and show in the Achievements window). On by default; a missing key keeps it on.
    public bool NotifyOnAchievement { get; set; } = true;

    // "Unlock toast messages": when on, each new unlock also pops a desktop toast (a single summary toast
    // for a big batch). This is separate from — and on top of — the card reveal, and can get noisy, so it's
    // off by default; a missing key keeps it off. The card reveal is the primary celebration.
    public bool AchievementToasts { get; set; }

    // Update checking. The version string of an update that has been detected and surfaced to the user
    // (via the "update available" notification, overlay button, tray menu and About highlight). Null
    // means no update is currently pending. Its presence is what suppresses re-notifying on subsequent
    // checks (even if a still-newer version appears) and what restores the "update available" UI across
    // restarts. Cleared the moment an update is actually applied, so a stale entry can't stick and any
    // drift is re-caught by the post-update startup check. See OverlayApplicationContext's update flow.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateVersion { get; set; }

    // Pop the "what's new" changelog window on the first launch after an update, showing only the entries
    // newer than LastSeenVersion. On by default; the window's "Don't show changelogs again" button flips
    // this off. See ChangelogWindow + App.OnFrameworkInitializationCompleted.
    public bool ShowChangelogOnUpdate { get; set; } = true;

    // The app version that last ran on this machine, stamped every launch. Compared against
    // AppInfo.Version at startup to detect an update and pick which changelog entries are new. Null on a
    // fresh install (nothing to show — seeded silently on first run). See the startup changelog check.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSeenVersion { get; set; }

    // Legacy quick-link switches (pre-configurable links). Kept only so an older settings file can be
    // migrated into QuickLinks on load; nullable to tell "absent" from "false", and cleared once
    // folded in so they're not re-written. See MigrateQuickLinks.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowGitKraken { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowSlack     { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                s.MigrateQuickLinks();
                s.MigrateStartMode();
                return s;
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.MigrateQuickLinks();
        fresh.MigrateStartMode();
        return fresh;
    }

    // Folds the legacy AutoStartOnFirstSession switch into StartMode: it was on -> "on session start",
    // it was off (or the file predates both) -> "off". Only applies when the legacy key is present and
    // StartMode is still at its default, so a file already written by this version keeps its mode. The
    // legacy switch is then dropped either way, so it stops being persisted.
    internal void MigrateStartMode()
    {
        if (AutoStartOnFirstSession is { } legacy && StartMode == StartMode.Off)
            StartMode = legacy ? StartMode.OnSessionStart : StartMode.Off;
        AutoStartOnFirstSession = null;
    }

    // Seeds QuickLinks the first time (null list), one entry per well-known preset. Each preset is
    // enabled only if its legacy ShowGitKraken/ShowSlack switch was on, so an upgrade preserves the
    // user's previous choice; a clean install gets both presets present-but-off. Presets are name-only
    // (no pinned path): the icon and launch resolve through the Start Menu, so they show the real logo
    // and survive app updates. The legacy switches are then dropped so they stop being persisted.
    private void MigrateQuickLinks()
    {
        if (QuickLinks != null) return;

        QuickLinks =
        [
            new QuickLink { Name = "GitKraken", Enabled = ShowGitKraken == true },
            new QuickLink { Name = "Slack",     Enabled = ShowSlack     == true },
        ];
        ShowGitKraken = null;
        ShowSlack     = null;
    }

    // A detached deep copy, made through the same JSON round-trip used to persist — so the Settings live
    // preview can mutate a snapshot and render it against a throwaway overlay without touching (or saving)
    // the user's real settings. `this` is already migrated, so the copy needs no re-migration: the legacy
    // nullable keys are cleared (and skipped when writing null), and QuickLinks is non-null so its one-time
    // seed doesn't re-run on the clone.
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
