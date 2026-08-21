using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginCapabilityGateTests
{
    [Fact]
    public void Notify_is_denied_without_the_notify_grant()
    {
        var allowed = PluginCapabilityGate.IsAllowed(
            new PluginNotifyMessage("t", "b"), PluginGrants.None, out var reason);
        Assert.False(allowed);
        Assert.Contains("notify", reason);
    }

    [Fact]
    public void Notify_is_allowed_with_the_notify_grant()
    {
        var grants = new PluginGrants(Notify: true, ReadCwd: false, ReadSessions: false, Network: []);
        Assert.True(PluginCapabilityGate.IsAllowed(new PluginNotifyMessage("t", "b"), grants, out _));
    }

    [Fact]
    public void Render_and_log_need_no_capability()
    {
        Assert.True(PluginCapabilityGate.IsAllowed(
            new PluginRenderMessage(new PluginGlyph("x", "y", "z")), PluginGrants.None, out _));
        Assert.True(PluginCapabilityGate.IsAllowed(
            new PluginLogMessage("info", "hello"), PluginGrants.None, out _));
    }

    [Fact]
    public void Declared_grants_map_from_capabilities()
    {
        var caps = new PluginCapabilities { Notify = true, ReadCwd = true, Network = ["example.com"] };
        var g = PluginGrants.FromDeclared(caps);
        Assert.True(g.Notify);
        Assert.True(g.ReadCwd);
        Assert.False(g.ReadSessions);
        Assert.Contains(PluginCapabilityKeys.Notify, g.ToWire());
        Assert.Contains(PluginCapabilityKeys.Network, g.ToWire());
        Assert.DoesNotContain(PluginCapabilityKeys.ReadSessions, g.ToWire());
    }
}
