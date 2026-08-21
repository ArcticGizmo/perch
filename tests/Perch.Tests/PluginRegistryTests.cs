using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginRegistryTests : IDisposable
{
    private readonly string _root;

    public PluginRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "perch-plugins-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WritePlugin(string folder, string manifestJson)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginRegistry.ManifestFileName), manifestJson);
    }

    private static string ManifestFor(string id) => $$"""
        { "schema":1, "id":"{{id}}", "name":"N", "version":"1.0.0",
          "entry":{"type":"process","command":"x","args":[]}, "extensionPoints":["command"] }
        """;

    [Fact]
    public void Missing_directory_is_no_plugins_not_an_error()
    {
        var reg = new PluginRegistry(Path.Combine(_root, "does-not-exist"));
        Assert.Empty(reg.Discover());
    }

    [Fact]
    public void Finds_valid_plugins_and_ignores_folders_without_a_manifest()
    {
        WritePlugin("weather", ManifestFor("dev.jon.weather"));
        WritePlugin("pomodoro", ManifestFor("dev.jon.pomodoro"));
        Directory.CreateDirectory(Path.Combine(_root, "just-a-folder"));  // no manifest

        var found = new PluginRegistry(_root).Discover();

        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.True(p.Ok));
        Assert.Equal(["dev.jon.pomodoro", "dev.jon.weather"], found.Select(p => p.Id)); // sorted by id
    }

    [Fact]
    public void A_broken_manifest_is_captured_with_errors_and_does_not_hide_the_others()
    {
        WritePlugin("good", ManifestFor("dev.jon.good"));
        WritePlugin("bad", "{ this is not valid json");

        var found = new PluginRegistry(_root).Discover();

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.Ok && p.Id == "dev.jon.good");
        var broken = found.Single(p => !p.Ok);
        Assert.NotEmpty(broken.Errors);
    }
}
