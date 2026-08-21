namespace Perch.Plugins;

/// <summary>
/// Pure operations over the persisted installed-plugin list (<c>AppSettings.InstalledPlugins</c>), kept
/// out of the UI so the Plugins page is a thin shell over tested rules. Ids are unique: an install of an
/// already-present id updates that record in place (an update), preserving nothing stale.
/// </summary>
internal static class PluginStore
{
    public static InstalledPluginRecord? Find(IReadOnlyList<InstalledPluginRecord> list, string id) =>
        list.FirstOrDefault(r => r.Id == id);

    /// <summary>Adds <paramref name="record"/>, replacing any existing record with the same id. Returns the
    /// same list for chaining.</summary>
    public static List<InstalledPluginRecord> Upsert(List<InstalledPluginRecord> list, InstalledPluginRecord record)
    {
        list.RemoveAll(r => r.Id == record.Id);
        list.Add(record);
        return list;
    }

    /// <summary>Removes the record with <paramref name="id"/>; returns true if one was removed.</summary>
    public static bool Remove(List<InstalledPluginRecord> list, string id) =>
        list.RemoveAll(r => r.Id == id) > 0;

    /// <summary>Sets the enabled flag on a record (no-op if absent); returns true if a record changed.</summary>
    public static bool SetEnabled(IReadOnlyList<InstalledPluginRecord> list, string id, bool enabled)
    {
        var rec = Find(list, id);
        if (rec is null || rec.Enabled == enabled) return false;
        rec.Enabled = enabled;
        if (enabled) rec.ConsecutiveFaults = 0; // a manual re-enable clears the fault strike
        return true;
    }
}
