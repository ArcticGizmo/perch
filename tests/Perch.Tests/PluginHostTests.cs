using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginHostTests
{
    private static DiscoveredPlugin OnDisk(string id, bool valid = true)
    {
        if (!valid) return new DiscoveredPlugin($@"C:\p\{id}", null, ["bad"]);
        var m = new PluginManifest
        {
            Schema = 1, Id = id, Name = id, Version = "1.0.0",
            Entry = new PluginEntry { Type = "process", Command = "x", Args = [], Mode = PluginEntryMode.OneShot },
            ExtensionPoints = [PluginExtensionPoints.Poll],
            Capabilities = new PluginCapabilities(),
        };
        return new DiscoveredPlugin($@"C:\p\{id}", m, []);
    }

    private static InstalledPluginRecord Rec(string id, bool enabled) =>
        new() { Id = id, Enabled = enabled };

    [Fact]
    public void Master_switch_off_yields_nothing()
    {
        var runnable = PluginHost.Resolve(false, [OnDisk("a")], [Rec("a", true)]);
        Assert.Empty(runnable);
    }

    [Fact]
    public void A_plugin_on_disk_with_no_consent_record_does_not_run()
    {
        var runnable = PluginHost.Resolve(true, [OnDisk("a")], records: []);
        Assert.Empty(runnable);
    }

    [Fact]
    public void A_disabled_record_does_not_run()
    {
        var runnable = PluginHost.Resolve(true, [OnDisk("a")], [Rec("a", enabled: false)]);
        Assert.Empty(runnable);
    }

    [Fact]
    public void An_invalid_manifest_on_disk_is_skipped_even_with_a_record()
    {
        var runnable = PluginHost.Resolve(true, [OnDisk("a", valid: false)], [Rec("a", true)]);
        Assert.Empty(runnable);
    }

    [Fact]
    public void An_enabled_consented_valid_plugin_runs_with_its_grants()
    {
        var rec = Rec("dev.x.notify", true);
        rec.GrantedCapabilities = [PluginCapabilityKeys.Notify];

        var runnable = PluginHost.Resolve(true, [OnDisk("dev.x.notify")], [rec]);

        var one = Assert.Single(runnable);
        Assert.Equal("dev.x.notify", one.Discovered.Id);
        Assert.True(one.Grants.Notify);
    }
}
