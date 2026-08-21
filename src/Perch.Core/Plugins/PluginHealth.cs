namespace Perch.Plugins;

/// <summary>
/// The fault policy: a plugin that keeps faulting (timing out, crashing, or failing to launch) is
/// auto-disabled so it can't keep costing launches or wedging the poll loop. A clean run resets the
/// counter. Denied actions are <em>not</em> faults — the plugin ran, it just tried something it wasn't
/// allowed to and the host dropped it; that's audited, not health-penalised.
/// </summary>
internal static class PluginHealth
{
    /// <summary>Consecutive faults before a plugin is disabled.</summary>
    public const int MaxConsecutiveFaults = 3;

    /// <summary>Updates <paramref name="record"/>'s fault counter from a run result and auto-disables at
    /// the threshold. Returns true if this call just disabled the plugin (so the caller can surface it).</summary>
    public static bool RecordResult(InstalledPluginRecord record, PluginPollResult result)
    {
        if (result.Ok)
        {
            record.ConsecutiveFaults = 0;
            return false;
        }

        record.ConsecutiveFaults++;
        if (record.Enabled && record.ConsecutiveFaults >= MaxConsecutiveFaults)
        {
            record.Enabled = false;
            return true;
        }
        return false;
    }
}
