using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginServiceTests
{
    // Builds a DiscoveredPlugin around a manifest with the given extension points / capabilities.
    private static DiscoveredPlugin Plugin(
        string[] points, PluginCapabilities? caps = null, string dir = @"C:\plugins\p")
    {
        var m = new PluginManifest
        {
            Schema = 1, Id = "dev.test.p", Name = "P", Version = "1.0.0",
            Entry = new PluginEntry { Type = "process", Command = "x", Args = [], Mode = PluginEntryMode.OneShot },
            ExtensionPoints = points,
            Capabilities = caps ?? new PluginCapabilities(),
        };
        return new DiscoveredPlugin(dir, m, []);
    }

    private static async Task<(PluginPollResult result, FakePluginSandbox sandbox)> Run(
        DiscoveredPlugin plugin, IEnumerable<string> stdout, PluginPollContext? ctx = null)
    {
        var sandbox = new FakePluginSandbox(new FakePluginProcess(stdout));
        var svc = new PluginService(sandbox, "0.9.0", TimeSpan.FromSeconds(5));
        var result = await svc.PollAsync(plugin, ctx ?? PluginPollContext.Empty);
        return (result, sandbox);
    }

    [Fact]
    public async Task Surfaces_a_glyph_from_a_plugin_that_declared_overlay_glyph()
    {
        var (result, _) = await Run(
            Plugin([PluginExtensionPoints.OverlayGlyph, PluginExtensionPoints.Poll]),
            ["""{"type":"render","glyph":{"glyph":"☀","text":"24°","tooltip":"Sunny"}}"""]);

        Assert.NotNull(result.Glyph);
        Assert.Equal("24°", result.Glyph!.Text);
        Assert.True(result.Ok);
        Assert.Empty(result.DeniedActions);
    }

    [Fact]
    public async Task Drops_a_render_from_a_plugin_that_did_not_declare_overlay_glyph()
    {
        var (result, _) = await Run(
            Plugin([PluginExtensionPoints.Command]),   // no overlay.glyph
            ["""{"type":"render","glyph":{"text":"sneaky"}}"""]);

        Assert.Null(result.Glyph);
        Assert.Contains(result.DeniedActions, d => d.Contains("overlay.glyph"));
    }

    [Fact]
    public async Task Drops_a_notify_when_the_notify_capability_was_not_granted()
    {
        var (result, _) = await Run(
            Plugin([PluginExtensionPoints.Event]),      // notify capability absent
            ["""{"type":"notify","title":"t","body":"b"}"""]);

        Assert.Empty(result.Notifications);
        Assert.Contains(result.DeniedActions, d => d.Contains("notify"));
    }

    [Fact]
    public async Task Passes_a_notify_through_when_granted()
    {
        var (result, _) = await Run(
            Plugin([PluginExtensionPoints.Event], new PluginCapabilities { Notify = true }),
            ["""{"type":"notify","title":"Done","body":"ok"}"""]);

        Assert.Single(result.Notifications);
        Assert.Equal("Done", result.Notifications[0].Title);
    }

    [Fact]
    public async Task Forwards_cwd_in_the_request_when_read_cwd_is_granted()
    {
        var capturing = new FakePluginProcess([]);
        var sandbox = new FakePluginSandbox(capturing);
        var svc = new PluginService(sandbox, "0.9.0", TimeSpan.FromSeconds(5));

        await svc.PollAsync(
            Plugin([PluginExtensionPoints.Poll], new PluginCapabilities { ReadCwd = true }),
            new PluginPollContext(Cwd: @"C:\proj"));

        Assert.Contains("\"cwd\"", capturing.CapturedStdin);
    }

    [Fact]
    public async Task Withholds_cwd_from_the_request_without_the_grant()
    {
        var capturing = new FakePluginProcess([]);
        var sandbox = new FakePluginSandbox(capturing);
        var svc = new PluginService(sandbox, "0.9.0", TimeSpan.FromSeconds(5));
        await svc.PollAsync(Plugin([PluginExtensionPoints.Poll]), new PluginPollContext(Cwd: @"C:\proj"));
        Assert.DoesNotContain("\"cwd\"", capturing.CapturedStdin);
    }

    [Fact]
    public async Task Sanitises_over_long_and_control_char_glyph_text()
    {
        var (result, _) = await Run(
            Plugin([PluginExtensionPoints.OverlayGlyph]),
            ["""{"type":"render","glyph":{"text":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\ttab"}}"""]);

        Assert.NotNull(result.Glyph);
        Assert.True(result.Glyph!.Text.Length <= 24);
        Assert.DoesNotContain('\t', result.Glyph.Text);
    }

    [Fact]
    public async Task The_launch_spec_pins_the_plugin_directory()
    {
        var (_, sandbox) = await Run(Plugin([PluginExtensionPoints.Poll], dir: @"C:\plugins\weather"), []);
        Assert.Equal(@"C:\plugins\weather", sandbox.LastSpec!.WorkingDirectory);
        Assert.Equal("x", sandbox.LastSpec.Command);
    }

    [Fact]
    public async Task Consented_grants_override_the_declared_capabilities()
    {
        // Manifest declares notify, but the user's consent (grants) withholds it: the notify is dropped.
        var capturing = new FakePluginProcess(["""{"type":"notify","title":"t","body":"b"}"""]);
        var sandbox = new FakePluginSandbox(capturing);
        var svc = new PluginService(sandbox, "0.9.0", TimeSpan.FromSeconds(5));

        var plugin = Plugin([PluginExtensionPoints.Event], new PluginCapabilities { Notify = true });
        var result = await svc.PollAsync(plugin, PluginPollContext.Empty, grants: PluginGrants.None);

        Assert.Empty(result.Notifications);
        Assert.Contains(result.DeniedActions, d => d.Contains("notify"));
    }

    [Fact]
    public async Task RaiseEvent_sends_the_event_name_and_surfaces_a_permitted_notify()
    {
        var capturing = new FakePluginProcess(["""{"type":"notify","title":"Attn","body":"look"}"""]);
        var sandbox = new FakePluginSandbox(capturing);
        var svc = new PluginService(sandbox, "0.9.0", TimeSpan.FromSeconds(5));

        var plugin = Plugin([PluginExtensionPoints.Event], new PluginCapabilities { Notify = true });
        var grants = new PluginGrants(Notify: true, ReadCwd: false, ReadSessions: false, Network: []);

        var result = await svc.RaiseEventAsync(plugin, SessionEvents.Attention, PluginPollContext.Empty, grants);

        Assert.Contains("\"type\":\"event\"", capturing.CapturedStdin);
        Assert.Contains(SessionEvents.Attention, capturing.CapturedStdin);
        Assert.Single(result.Notifications);
        Assert.Equal("Attn", result.Notifications[0].Title);
    }
}
