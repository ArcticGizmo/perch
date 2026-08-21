using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginConsentTests
{
    private static InstalledPluginRecord Record(bool enabled, string[] caps, string[]? net = null) => new()
    {
        Id = "dev.x", Enabled = enabled,
        GrantedCapabilities = [.. caps], GrantedNetwork = [.. (net ?? [])],
    };

    [Fact]
    public void Disabled_plugin_gets_no_grants_even_if_capabilities_were_recorded()
    {
        var g = PluginConsent.GrantsFor(Record(enabled: false, [PluginCapabilityKeys.Notify]));
        Assert.False(g.Notify);
        Assert.Equal(PluginGrants.None, g);
    }

    [Fact]
    public void Enabled_grants_map_from_the_consented_set()
    {
        var g = PluginConsent.GrantsFor(Record(true,
            [PluginCapabilityKeys.ReadCwd, PluginCapabilityKeys.Network], net: ["example.com"]));
        Assert.True(g.ReadCwd);
        Assert.False(g.Notify);
        Assert.Equal(["example.com"], g.Network);
    }

    [Fact]
    public void Requires_consent_when_a_new_capability_is_requested()
    {
        var rec = Record(true, [PluginCapabilityKeys.ReadCwd]);
        var requested = new PluginCapabilities { ReadCwd = true, Notify = true };  // notify is new
        Assert.True(PluginConsent.RequiresConsent(rec, requested));
    }

    [Fact]
    public void No_reconsent_when_capabilities_are_unchanged_or_narrowed()
    {
        var rec = Record(true, [PluginCapabilityKeys.ReadCwd, PluginCapabilityKeys.Notify]);
        Assert.False(PluginConsent.RequiresConsent(rec, new PluginCapabilities { ReadCwd = true, Notify = true }));
        Assert.False(PluginConsent.RequiresConsent(rec, new PluginCapabilities { ReadCwd = true })); // dropped notify
    }

    [Fact]
    public void A_newly_requested_network_host_requires_reconsent_even_if_network_was_granted()
    {
        var rec = Record(true, [PluginCapabilityKeys.Network], net: ["a.com"]);
        var requested = new PluginCapabilities { Network = ["a.com", "b.com"] };  // b.com is new
        Assert.True(PluginConsent.RequiresConsent(rec, requested));
    }

    [Fact]
    public void GrantAll_records_every_requested_capability_and_enables()
    {
        var rec = Record(false, []);
        PluginConsent.GrantAll(rec, new PluginCapabilities { Notify = true, Network = ["x.com"] });

        Assert.True(rec.Enabled);
        Assert.Contains(PluginCapabilityKeys.Notify, rec.GrantedCapabilities);
        Assert.Contains(PluginCapabilityKeys.Network, rec.GrantedCapabilities);
        Assert.Equal(["x.com"], rec.GrantedNetwork);
        // and after granting, no re-consent is needed for the same manifest
        Assert.False(PluginConsent.RequiresConsent(rec, new PluginCapabilities { Notify = true, Network = ["x.com"] }));
    }
}
