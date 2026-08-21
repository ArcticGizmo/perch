namespace Perch.Plugins;

/// <summary>
/// The consent logic: turn a persisted <see cref="InstalledPluginRecord"/> into the runtime
/// <see cref="PluginGrants"/> the host enforces, decide when an update must be re-consented (its manifest
/// now asks for something the user never granted), and record a fresh "grant everything requested"
/// consent. Kept pure so the consent UI is a thin shell over tested rules.
/// </summary>
internal static class PluginConsent
{
    /// <summary>The runtime grants for a record: only what the user consented to, and only while enabled.
    /// A disabled plugin gets <see cref="PluginGrants.None"/> so nothing it emits is acted on.</summary>
    public static PluginGrants GrantsFor(InstalledPluginRecord record)
    {
        if (!record.Enabled) return PluginGrants.None;
        var keys = record.GrantedCapabilities;
        return new PluginGrants(
            Notify: keys.Contains(PluginCapabilityKeys.Notify),
            ReadCwd: keys.Contains(PluginCapabilityKeys.ReadCwd),
            ReadSessions: keys.Contains(PluginCapabilityKeys.ReadSessions),
            Network: keys.Contains(PluginCapabilityKeys.Network) ? record.GrantedNetwork : []);
    }

    /// <summary>Whether <paramref name="requested"/> asks for any capability — or network host — the user
    /// hasn't already granted in <paramref name="record"/>. True means an install/update must prompt for
    /// consent again; a plugin that drops or narrows capabilities does not.</summary>
    public static bool RequiresConsent(InstalledPluginRecord record, PluginCapabilities requested)
    {
        foreach (var key in RequestedKeys(requested))
            if (!record.GrantedCapabilities.Contains(key))
                return true;

        // A newly-requested egress host is a capability expansion even if 'network' was already granted.
        foreach (var host in requested.Network)
            if (!record.GrantedNetwork.Contains(host))
                return true;

        return false;
    }

    /// <summary>Records consent to everything a manifest requests (what the consent dialog's "Allow"
    /// writes back), enabling the plugin.</summary>
    public static void GrantAll(InstalledPluginRecord record, PluginCapabilities requested)
    {
        record.GrantedCapabilities = RequestedKeys(requested).ToList();
        record.GrantedNetwork = requested.Network.ToList();
        record.Enabled = true;
        record.ConsecutiveFaults = 0;
    }

    // The capability keys a manifest actually requests (a false bool / empty network isn't a request).
    private static IEnumerable<string> RequestedKeys(PluginCapabilities caps)
    {
        if (caps.Notify) yield return PluginCapabilityKeys.Notify;
        if (caps.ReadCwd) yield return PluginCapabilityKeys.ReadCwd;
        if (caps.ReadSessions) yield return PluginCapabilityKeys.ReadSessions;
        if (caps.RequestsNetwork) yield return PluginCapabilityKeys.Network;
    }
}
