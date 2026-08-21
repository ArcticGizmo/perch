namespace Perch.Plugins;

/// <summary>
/// Resolves which installed plugins should actually run right now, pairing each with the grants the user
/// consented to. This is the join between three sources of truth — the master kill switch, what's on disk
/// (<see cref="DiscoveredPlugin"/>), and the persisted consent (<see cref="InstalledPluginRecord"/>) — and
/// it is deliberately conservative: a plugin runs only when the master switch is on, it has a valid
/// manifest on disk, it has a consent record, and that record is enabled. A plugin sitting on disk with no
/// record has never been consented to, so it does not run. UI-free; a head's poll loop consumes the list.
/// </summary>
internal static class PluginHost
{
    public static IReadOnlyList<RunnablePlugin> Resolve(
        bool masterEnabled,
        IReadOnlyList<DiscoveredPlugin> discovered,
        IReadOnlyList<InstalledPluginRecord> records)
    {
        if (!masterEnabled) return [];

        var byId = new Dictionary<string, InstalledPluginRecord>(StringComparer.Ordinal);
        foreach (var r in records) byId[r.Id] = r; // last record for an id wins

        var runnable = new List<RunnablePlugin>();
        foreach (var d in discovered)
        {
            if (!d.Ok || d.Id is null) continue;                     // invalid manifest on disk
            if (!byId.TryGetValue(d.Id, out var record)) continue;   // installed but never consented
            if (!record.Enabled) continue;                           // disabled or pending consent

            runnable.Add(new RunnablePlugin(d, record, PluginConsent.GrantsFor(record)));
        }
        return runnable;
    }
}

/// <summary>A plugin cleared to run: its on-disk form, its consent record, and the resolved grants the
/// host will enforce for it.</summary>
internal sealed record RunnablePlugin(
    DiscoveredPlugin Discovered,
    InstalledPluginRecord Record,
    PluginGrants Grants);
