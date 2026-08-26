namespace Perch.Data;

using System.Text.Json.Serialization;

/// <summary>
/// A starter feature set the first-run Quick Start offers. A tier is a <em>preset</em> — it seeds which
/// features are on — not a locked bundle: the wizard lets the user fine-tune individual toggles from the
/// seeded set before applying (see <see cref="OnboardingTiers"/> and docs/onboarding-quickstart-plan.md).
/// Ordered least-to-most, and the presets are strict supersets (Basic ⊆ Intermediate ⊆ KitchenSink), so the
/// ordinal doubles as "how much is on". Persisted by <em>name</em> (via the converter) as
/// <see cref="AppSettings.OnboardingTierChosen"/>, so the member order can change without breaking a file.
/// </summary>
[JsonConverter(typeof(TolerantStringEnumConverter<OnboardingTier>))]
internal enum OnboardingTier
{
    Basic = 0,
    Intermediate = 1,
    KitchenSink = 2,
}

/// <summary>One feature the wizard governs: a <see cref="SettingsRegistry"/> toggle id and the lowest tier
/// whose preset turns it on.</summary>
internal sealed record OnboardingItem(string Id, OnboardingTier MinTier);

/// <summary>A logical grouping of features shown as one block in the chooser (label + emoji + its items).</summary>
internal sealed record OnboardingGroup(string Name, string Icon, IReadOnlyList<OnboardingItem> Items);

/// <summary>
/// The onboarding tier model: the <em>managed toggle universe</em> the Quick Start governs, grouped for
/// display, plus the logic to turn a tier (or a hand-customised set of ids) into concrete
/// <see cref="AppSettings"/> changes. Data-driven off <see cref="SettingsRegistry"/> — every id here must be a
/// real boolean toggle descriptor, which <c>OnboardingTiersTests</c> enforces.
///
/// <para>Deliberately <b>excluded</b> from the managed universe (so <see cref="Apply(AppSettings,IReadOnlySet{string})"/>
/// never touches them): non-boolean config (thresholds, fields, the quick-links list); the always-openable
/// dashboards (Stats / History / Flight — they have no on/off setting and are only <em>described</em> in the
/// wizard); the overlay presentation mode (<c>overlay-mode</c>, chosen on its own placement step); the git-tree
/// window's display sub-preferences and the stuck-detection sub-heuristics (left at their defaults); and Agent
/// Teams (writes Claude Code's own settings.json, not an <see cref="AppSettings"/> property).</para>
/// </summary>
internal static class OnboardingTiers
{
    // Six logical groups, mirroring the chooser. Each item names a real SettingsRegistry toggle id and the
    // lowest tier that turns it on. Order within a group is the display order.
    public static IReadOnlyList<OnboardingGroup> Groups { get; } =
    [
        new OnboardingGroup("At a glance", "👁️",
        [
            new OnboardingItem("permission-mode-badges", OnboardingTier.Basic),
            new OnboardingItem("task-progress",          OnboardingTier.Basic),
            new OnboardingItem("context-pressure",       OnboardingTier.Basic),
            new OnboardingItem("waiting-timer",          OnboardingTier.Basic),
            new OnboardingItem("burn-rate",              OnboardingTier.Intermediate),
            new OnboardingItem("stuck-detection",        OnboardingTier.Intermediate),
            new OnboardingItem("artifacts",              OnboardingTier.Intermediate),
            new OnboardingItem("markdown",               OnboardingTier.Intermediate),
            new OnboardingItem("notes",                  OnboardingTier.Intermediate),
            new OnboardingItem("git-stats",              OnboardingTier.Intermediate),
            new OnboardingItem("media-controller",       OnboardingTier.KitchenSink),
            new OnboardingItem("mic-presence",           OnboardingTier.KitchenSink),
            new OnboardingItem("daemon-processes",       OnboardingTier.KitchenSink),
        ]),

        new OnboardingGroup("Usage & machine", "📊",
        [
            new OnboardingItem("usage-bars",      OnboardingTier.Basic),
            new OnboardingItem("system-metrics",  OnboardingTier.Basic),
            new OnboardingItem("expected-rate",   OnboardingTier.Intermediate),
            new OnboardingItem("monthly-spend",   OnboardingTier.Intermediate),
            new OnboardingItem("session-metrics", OnboardingTier.Intermediate),
            new OnboardingItem("service-status",  OnboardingTier.KitchenSink),
        ]),

        new OnboardingGroup("Alerts", "🔔",
        [
            new OnboardingItem("notifications-enabled", OnboardingTier.Basic),
            new OnboardingItem("notify-done",           OnboardingTier.Basic),
            new OnboardingItem("notify-waiting",        OnboardingTier.Basic),
            new OnboardingItem("chime-done",            OnboardingTier.Intermediate),
            new OnboardingItem("chime-waiting",         OnboardingTier.Intermediate),
            new OnboardingItem("notify-api-error",      OnboardingTier.Intermediate),
            new OnboardingItem("notify-pr-finished",    OnboardingTier.Intermediate),
            new OnboardingItem("notify-pr-reviewed",    OnboardingTier.Intermediate),
            new OnboardingItem("notify-pr-approved",    OnboardingTier.Intermediate),
            new OnboardingItem("todo-reminders",        OnboardingTier.Intermediate),
            new OnboardingItem("chime-api-error",       OnboardingTier.KitchenSink),
            new OnboardingItem("chime-pr-finished",     OnboardingTier.KitchenSink),
            new OnboardingItem("chime-pr-reviewed",     OnboardingTier.KitchenSink),
            new OnboardingItem("chime-pr-approved",     OnboardingTier.KitchenSink),
            new OnboardingItem("pr-finished-banner",    OnboardingTier.KitchenSink),
            new OnboardingItem("external-notifications", OnboardingTier.KitchenSink),
            new OnboardingItem("notify-when-locked",    OnboardingTier.KitchenSink),
            new OnboardingItem("notify-friend-post",    OnboardingTier.KitchenSink),
            new OnboardingItem("notify-game-invite",    OnboardingTier.KitchenSink),
        ]),

        new OnboardingGroup("Stats & tools", "🪟",
        [
            new OnboardingItem("today-stats-tray", OnboardingTier.Basic),
            new OnboardingItem("estimated-cost",   OnboardingTier.Intermediate),
            new OnboardingItem("todos",            OnboardingTier.Intermediate),
        ]),

        new OnboardingGroup("Integrations", "🔗",
        [
            new OnboardingItem("hypertree",     OnboardingTier.Intermediate),
            new OnboardingItem("pull-requests", OnboardingTier.Intermediate),
            new OnboardingItem("jira-ticket",   OnboardingTier.Intermediate),
        ]),

        new OnboardingGroup("Fun & social", "🎈",
        [
            new OnboardingItem("perch-reacts",          OnboardingTier.Intermediate),
            new OnboardingItem("notify-achievement",    OnboardingTier.Intermediate),
            new OnboardingItem("achievement-toasts",    OnboardingTier.KitchenSink),
            new OnboardingItem("upside-down-quick-links", OnboardingTier.KitchenSink),
            new OnboardingItem("social",                OnboardingTier.KitchenSink),
            new OnboardingItem("social-large-reactions", OnboardingTier.KitchenSink),
        ]),
    ];

    /// <summary>The three tiers, least-to-most.</summary>
    public static IReadOnlyList<OnboardingTier> AllTiers { get; } =
        [OnboardingTier.Basic, OnboardingTier.Intermediate, OnboardingTier.KitchenSink];

    /// <summary>Every toggle id the wizard governs, in group/display order — the managed universe.</summary>
    public static IReadOnlyList<string> Managed { get; } =
        Groups.SelectMany(g => g.Items).Select(i => i.Id).ToList();

    /// <summary>The set of managed ids a tier's preset turns on (its items and every lower tier's).</summary>
    public static IReadOnlySet<string> Preset(OnboardingTier tier) =>
        Groups.SelectMany(g => g.Items).Where(i => i.MinTier <= tier).Select(i => i.Id).ToHashSet();

    /// <summary>How many features a tier's preset turns on.</summary>
    public static int Count(OnboardingTier tier) => Preset(tier).Count;

    /// <summary>The tier whose preset exactly equals <paramref name="enabledIds"/>, or null when the set is a
    /// custom mix that matches no preset. Used to label / pre-select the chooser and to record
    /// <see cref="AppSettings.OnboardingTierChosen"/>.</summary>
    public static OnboardingTier? MatchedTier(IReadOnlySet<string> enabledIds)
    {
        foreach (var t in AllTiers)
            if (Preset(t).SetEquals(enabledIds)) return t;
        return null;
    }

    /// <summary>Applies a tier's preset — shorthand for <c>Apply(s, Preset(tier))</c>.</summary>
    public static void Apply(AppSettings s, OnboardingTier tier) => Apply(s, Preset(tier));

    /// <summary>
    /// Writes the chosen set into <paramref name="s"/>: every managed toggle is set on when its id is in
    /// <paramref name="enabledIds"/> and off otherwise, through its registry <c>SetBool</c>. Only the managed
    /// universe is touched — no other <see cref="AppSettings"/> property (field, threshold, list) is changed —
    /// so a re-run can lower a tier without clobbering the user's other configuration. Ids not in the managed
    /// universe are ignored.
    /// </summary>
    public static void Apply(AppSettings s, IReadOnlySet<string> enabledIds)
    {
        foreach (var id in Managed)
            SettingsRegistry.ById(id)?.SetBool?.Invoke(s, enabledIds.Contains(id));
    }
}
