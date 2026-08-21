namespace Perch.Plugins;

/// <summary>
/// The capabilities actually granted to a running plugin — the host's copy of what the user consented to.
/// Distinct from <see cref="PluginCapabilities"/> (what the manifest <em>requested</em>): a grant can be a
/// subset if the user declined some. In M1 (local-folder plugins, no consent UI yet) grants equal the
/// declared capabilities via <see cref="FromDeclared"/>; from M2 they come from persisted user consent.
/// </summary>
internal sealed record PluginGrants(
    bool Notify,
    bool ReadCwd,
    bool ReadSessions,
    IReadOnlyList<string> Network)
{
    public static readonly PluginGrants None = new(false, false, false, []);

    /// <summary>Grants everything the manifest asked for (the implicit-trust path for locally-installed
    /// plugins before the consent flow exists).</summary>
    public static PluginGrants FromDeclared(PluginCapabilities caps) =>
        new(caps.Notify, caps.ReadCwd, caps.ReadSessions, caps.Network);

    /// <summary>The wire form the host sends in a request's <c>grants</c> array, so a plugin can tailor its
    /// behaviour to what it was actually allowed.</summary>
    public IReadOnlyList<string> ToWire()
    {
        var list = new List<string>();
        if (Notify) list.Add(PluginCapabilityKeys.Notify);
        if (ReadCwd) list.Add(PluginCapabilityKeys.ReadCwd);
        if (ReadSessions) list.Add(PluginCapabilityKeys.ReadSessions);
        if (Network.Count > 0) list.Add(PluginCapabilityKeys.Network);
        return list;
    }
}
