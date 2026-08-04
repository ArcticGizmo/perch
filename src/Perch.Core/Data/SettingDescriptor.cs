namespace Perch.Data;

/// <summary>
/// Where a setting shows up in the app — the overlay surface (or general area) it governs. The redesigned
/// Settings window groups the catalogue by this, and search shows it as a result's breadcrumb. Ordered
/// roughly the way they stack on the overlay; <see cref="Advanced"/> is the catch-all for config-heavy
/// settings that don't map to a single glyph (ntfy host, hotkeys, export, about…).
/// </summary>
internal enum SettingSurface
{
    SessionRow,
    UsageBars,
    TrayAndStats,
    Notifications,
    SystemMetrics,
    Whimsy,
    Integrations,
    Advanced,
}

/// <summary>The control a setting is edited with — drives which widget the catalogue/search row renders.</summary>
internal enum SettingKind
{
    Toggle,
    Slider,
    Stepper,
    Dropdown,
    Field,
    Hotkey,
    List,
}

/// <summary>
/// The overlay element a setting affects, so the live preview can spotlight (pulse) it when the setting
/// changes. <see cref="None"/> is for settings with no single visual target — notifications, behaviour
/// toggles, config fields — which the preview simply doesn't highlight.
/// </summary>
internal enum PreviewTarget
{
    None,
    UsageBars,
    ExpectedRate,
    ContextPressure,
    ModeBadge,
    TaskProgress,
    Note,
    BurnRate,
    WaitingTimer,
    Artifacts,
    GitStats,
    PullRequest,
    Stuck,
    ServiceStatus,
    SystemMetrics,
    SessionMetrics,
    MediaController,
    MicPresence,
    DaemonProcesses,
    QuickLinks,
    PerchReacts,
}

/// <summary>
/// One row in the <c>SettingsRegistry</c> — the single description of a setting that search, the surface
/// catalogue, and the live-preview linkage all read from. Bindings are typed by <see cref="Kind"/>: a
/// <see cref="SettingKind.Toggle"/> sets <see cref="GetBool"/>/<see cref="SetBool"/>, a stepper/slider sets
/// <see cref="GetInt"/>/<see cref="SetInt"/>, and so on. Richer kinds (dropdown/hotkey/field) gain their
/// own accessors as those controls are built out; the registry is populated in M2.
/// </summary>
/// <param name="Id">Stable identifier, kebab-case (e.g. <c>chime-on-done</c>). Also the search/test key.</param>
/// <param name="Name">The user-facing label ("Chime when done").</param>
/// <param name="Description">One-line explanation, shown under the label in a catalogue card.</param>
/// <param name="Surface">Which surface the catalogue files this under.</param>
/// <param name="Kind">Which control edits it.</param>
/// <param name="Keywords">Extra search terms/synonyms so "sound"/"beep" find a chime, "cost" finds pricing, etc.</param>
/// <param name="Preview">The overlay glyph the live preview should spotlight when this changes.</param>
/// <param name="Backing">
/// The <see cref="AppSettings"/> property name(s) this descriptor governs — one for most, several for a
/// grouped control (the context slider spans three thresholds). Used by the registry-coverage test to
/// prove every user-facing setting has an entry; leave empty for a descriptor not backed by an
/// <see cref="AppSettings"/> property (e.g. the Agent Teams env var).
/// </param>
internal sealed record SettingDescriptor(
    string Id,
    string Name,
    string Description,
    SettingSurface Surface,
    SettingKind Kind,
    string[] Keywords,
    PreviewTarget Preview = PreviewTarget.None,
    string[]? Backing = null,
    Func<AppSettings, bool>? GetBool = null,
    Action<AppSettings, bool>? SetBool = null,
    Func<AppSettings, int>? GetInt = null,
    Action<AppSettings, int>? SetInt = null)
{
    /// <summary>
    /// Whether every whitespace-separated token in <paramref name="query"/> is a substring of the setting's
    /// name, keywords or surface label — a forgiving token-AND match, so "chime error" narrows to the error
    /// chime while a blank query matches everything.
    /// </summary>
    public bool MatchesQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;

        var haystack = $"{Name} {string.Join(' ', Keywords)} {SurfaceLabel(Surface)}".ToLowerInvariant();
        foreach (var token in query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!haystack.Contains(token, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>The human-readable name for a surface, used for the catalogue heading and search breadcrumb.</summary>
    public static string SurfaceLabel(SettingSurface surface) => surface switch
    {
        SettingSurface.SessionRow     => "Session row",
        SettingSurface.UsageBars      => "Usage bars",
        SettingSurface.TrayAndStats   => "Tray & stats",
        SettingSurface.Notifications  => "Notifications",
        SettingSurface.SystemMetrics  => "System & metrics",
        SettingSurface.Whimsy         => "Whimsy",
        SettingSurface.Integrations   => "Integrations",
        SettingSurface.Advanced       => "Advanced",
        _                             => surface.ToString(),
    };
}
