namespace Perch.Plugins;

/// <summary>
/// The persisted state of one installed plugin (lives in <c>AppSettings.InstalledPlugins</c>): its
/// provenance (where it came from, which release tag, the verified payload hash — the audit trail), and
/// the capabilities the user actually consented to. The granted set is the source of truth for what the
/// running plugin is allowed to do, and for detecting when an update needs re-consent.
/// </summary>
internal sealed class InstalledPluginRecord
{
    public string Id { get; set; } = "";

    /// <summary><c>owner/repo</c> the plugin was installed from (empty for a local sideload).</summary>
    public string Source { get; set; } = "";

    /// <summary>The release tag installed.</summary>
    public string Tag { get; set; } = "";

    /// <summary>The manifest version installed.</summary>
    public string Version { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the verified payload zip — provenance/audit.</summary>
    public string AssetSha256 { get; set; } = "";

    /// <summary>Whether the plugin is currently allowed to run. False until the user consents; also the
    /// per-plugin disable toggle.</summary>
    public bool Enabled { get; set; }

    /// <summary>Capability keys the user granted (<see cref="PluginCapabilityKeys"/> values).</summary>
    public List<string> GrantedCapabilities { get; set; } = [];

    /// <summary>Hostnames the user granted network egress to (meaningful when <c>network</c> is granted).</summary>
    public List<string> GrantedNetwork { get; set; } = [];

    /// <summary>The number of consecutive faults (timeout/crash/launch failure) — the host disables a
    /// plugin that keeps faulting so a broken plugin can't keep costing launches.</summary>
    public int ConsecutiveFaults { get; set; }
}
