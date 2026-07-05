namespace Perch.Data;

using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Perch", "settings.json");

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

    // Per-type sound switches: play the built-in Windows system chime when that notification type
    // fires (Done -> Asterisk, WaitingForInput -> Exclamation). Independent of the balloon switches
    // above but gated by NotificationsEnabled. Off by default — the chime opts in per type. External
    // (ntfy) pushes never chime; sound is for the local desktop only.
    public bool ChimeOnDone { get; set; }
    public bool ChimeOnWaitingInput { get; set; }

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

    // Automation. AutoStart: the plugin's SessionStart hook reads this value (the tray usually
    // isn't running when a session opens) and launches the installed perch when on. AutoClose:
    // the running tray exits a short grace period after the last session ends — but only when it was
    // itself auto-started, so a manually-opened window never vanishes under the user. Both off by
    // default. See the plugin's invoke.ps1 ("start" action) and [[OverlayApplicationContext]].
    public bool AutoStartOnFirstSession  { get; set; }
    public bool AutoCloseAfterLastSession { get; set; }

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

    // Ambient screen-edge glow. When on, a soft coloured glow pulses around the edge of the screen the
    // overlay lives on while any session needs attention or is awaiting input — a peripheral cue you
    // can catch without watching the overlay. The window is click-through and never activates, so it
    // can't get in the way, and it fades out the moment the session is dealt with. Off by default
    // (experimental); a missing key keeps it off.
    public bool ScreenEdgeGlow { get; set; }

    // Quick links. Icons displayed below the usage bars; each opens the app or focuses it. The list
    // is the source of truth; null means "never configured" and triggers a one-time seed (see
    // MigrateQuickLinks) with the well-known presets, honouring the legacy switches below. An empty
    // (non-null) list means the user deliberately removed every link and is left alone.
    public List<QuickLink>? QuickLinks { get; set; }

    // Renders the quick-link icons rotated 180°. Purely for fun — the icons happen to look upside down
    // already, so this leans in. Off by default.
    public bool UpsideDownQuickLinks { get; set; }

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

    // Update checking. The version string of an update that has been detected and surfaced to the user
    // (via the "update available" notification, overlay button, tray menu and About highlight). Null
    // means no update is currently pending. Its presence is what suppresses re-notifying on subsequent
    // checks (even if a still-newer version appears) and what restores the "update available" UI across
    // restarts. Cleared the moment an update is actually applied, so a stale entry can't stick and any
    // drift is re-caught by the post-update startup check. See OverlayApplicationContext's update flow.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateVersion { get; set; }

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
                return s;
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.MigrateQuickLinks();
        return fresh;
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
