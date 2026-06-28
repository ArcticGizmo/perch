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

    // Quick links. Icons displayed below the usage bars; each opens the app or focuses it. The list
    // is the source of truth; null means "never configured" and triggers a one-time seed (see
    // MigrateQuickLinks) with the well-known presets, honouring the legacy switches below. An empty
    // (non-null) list means the user deliberately removed every link and is left alone.
    public List<QuickLink>? QuickLinks { get; set; }

    // Renders the quick-link icons rotated 180°. Purely for fun — the icons happen to look upside down
    // already, so this leans in. Off by default.
    public bool UpsideDownQuickLinks { get; set; }

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
