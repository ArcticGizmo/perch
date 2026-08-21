using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginManifestParserTests
{
    // A minimal valid manifest reused across tests; individual tests mutate it via string replace.
    private const string Valid = """
        {
          "schema": 1,
          "id": "dev.jon.weather",
          "name": "Weather Badge",
          "version": "1.0.0",
          "entry": { "type": "process", "command": "powershell", "args": ["-File", "w.ps1"] },
          "extensionPoints": ["overlay.glyph", "poll"],
          "capabilities": { "network": ["Api.Open-Meteo.com"], "poll.intervalSec": 300 }
        }
        """;

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var r = PluginManifestParser.Parse(Valid);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var m = r.Manifest!;
        Assert.Equal("dev.jon.weather", m.Id);
        Assert.Equal("Weather Badge", m.Name);
        Assert.Equal(PluginEntryMode.OneShot, m.Entry.Mode);      // default
        Assert.Contains("overlay.glyph", m.ExtensionPoints);
        Assert.True(m.Capabilities.RequestsNetwork);
        Assert.Equal(["api.open-meteo.com"], m.Capabilities.Network); // normalised to lowercase
    }

    [Fact]
    public void Rejects_a_future_schema()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"schema\": 1", "\"schema\": 2"));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("unsupported schema"));
    }

    [Fact]
    public void Rejects_unknown_top_level_key()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"schema\": 1,", "\"schema\": 1, \"evil\": true,"));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("unknown top-level key 'evil'"));
    }

    [Fact]
    public void Rejects_unknown_capability()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"poll.intervalSec\": 300", "\"filesystem\": true"));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("unknown capability 'filesystem'"));
    }

    [Fact]
    public void Rejects_unknown_extension_point()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"poll\"", "\"exec.shell\""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("unknown extension point 'exec.shell'"));
    }

    [Theory]
    [InlineData("Weather")]          // no dot
    [InlineData("Dev.Jon.Weather")]  // uppercase
    [InlineData("dev..weather")]     // empty segment
    public void Rejects_a_bad_id(string id)
    {
        var r = PluginManifestParser.Parse(Valid.Replace("dev.jon.weather", id));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("reverse-DNS"));
    }

    [Fact]
    public void Clamps_a_too_small_poll_interval_up_to_the_floor()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"poll.intervalSec\": 300", "\"poll.intervalSec\": 1"));
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.Equal(PluginCapabilities.MinPollIntervalSec, r.Manifest!.Capabilities.PollIntervalSec);
    }

    [Fact]
    public void Rejects_a_non_process_entry_type()
    {
        var r = PluginManifestParser.Parse(Valid.Replace("\"type\": \"process\"", "\"type\": \"dll\""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("is not supported"));
    }

    [Fact]
    public void Enforces_the_minPerch_floor_when_a_host_version_is_given()
    {
        var withFloor = Valid.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\", \"minPerch\": \"2.0.0\",");
        Assert.False(PluginManifestParser.Parse(withFloor, hostVersion: "1.5.0").Ok);
        Assert.True(PluginManifestParser.Parse(withFloor, hostVersion: "2.1.0").Ok);
    }

    [Fact]
    public void Missing_required_keys_are_reported_not_thrown()
    {
        var r = PluginManifestParser.Parse("{ \"schema\": 1 }");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("'id'"));
        Assert.Contains(r.Errors, e => e.Contains("'entry'"));
        Assert.Contains(r.Errors, e => e.Contains("'extensionPoints'"));
    }

    [Fact]
    public void Garbage_json_is_an_error_not_an_exception()
    {
        var r = PluginManifestParser.Parse("not json at all {");
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Contains("not valid JSON"));
    }

    [Fact]
    public void Capabilities_default_to_nothing_when_absent()
    {
        var noCaps = """
            { "schema":1, "id":"a.b", "name":"n", "version":"1.0.0",
              "entry":{"type":"process","command":"x","args":[]}, "extensionPoints":["command"] }
            """;
        var r = PluginManifestParser.Parse(noCaps);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var c = r.Manifest!.Capabilities;
        Assert.False(c.Notify);
        Assert.False(c.ReadCwd);
        Assert.False(c.ReadSessions);
        Assert.Empty(c.Network);
    }
}
