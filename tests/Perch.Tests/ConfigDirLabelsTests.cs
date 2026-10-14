using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the config-dir label feature: <see cref="ClaudeConfigDir.DisplayLabel"/>'s rules (the primary shows
/// no chip unless named; others use their slug/dir-name; a custom label overrides), and
/// <see cref="ClaudeConfigDiscovery.Discover"/> threading those custom labels through by real path. Pure —
/// each disk test builds its own throwaway tree and injects an identity link resolver so real paths are
/// deterministic.
/// </summary>
public sealed class ConfigDirLabelsTests : IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("perch-cfglabel-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private string Make(string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(_sandbox, relative));
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        return dir;
    }

    private static string Identity(string p) => p;

    // ── DisplayLabel value-model rules ──────────────────────────────────────────────

    [Fact]
    public void DisplayLabel_Primary_IsEmpty_UnlessNamed()
    {
        var primary = new ClaudeConfigDir(@"C:\Users\me\.claude");
        Assert.Equal(ConfigDirProvenance.Primary, primary.Provenance);
        Assert.Equal("", primary.DisplayLabel);                                    // no chip by default
        Assert.Equal("Home", (primary with { CustomLabel = "Home" }).DisplayLabel); // opt-in rename shows
    }

    [Fact]
    public void DisplayLabel_NonPrimary_UsesSlug_ThenCustomOverride()
    {
        var env = new ClaudeConfigDir(@"C:\envs\work", slug: "work")
            { Provenance = ConfigDirProvenance.Declared };
        Assert.Equal("work", env.DisplayLabel);                                   // slug by default
        Assert.Equal("Acme", (env with { CustomLabel = "Acme" }).DisplayLabel);   // custom overrides
        Assert.Equal("work", (env with { CustomLabel = "   " }).DisplayLabel);    // blank custom ignored
    }

    // ── Discover threads labels by real path ────────────────────────────────────────

    [Fact]
    public void Discover_AppliesCustomLabel_ByRealRoot_IncludingPrimary()
    {
        var primary = new ClaudeConfigDir(Make(".claude"));
        var work = Make("work/.claude");
        var labels = new Dictionary<string, string>(ClaudeConfigDir.PathComparer)
        {
            [primary.RealRoot] = "Home",
            [Path.TrimEndingDirectorySeparator(work)] = "Acme",
        };

        var set = ClaudeConfigDiscovery.Discover(
            primary, new[] { work }, linkResolver: Identity, labels: labels);

        Assert.Equal("Home", set.Single(d => d.Provenance == ConfigDirProvenance.Primary).DisplayLabel);
        Assert.Equal("Acme", set.Single(d => d.Root == work).DisplayLabel);
    }

    [Fact]
    public void Discover_ClearsStaleLabel_WhenMapNoLongerNamesIt()
    {
        // A previously-labelled primary is passed back in; with an empty labels map it comes out cleared,
        // so removing a label in the editor actually takes effect.
        var primary = new ClaudeConfigDir(Make(".claude")) { CustomLabel = "OldName" };

        var set = ClaudeConfigDiscovery.Discover(
            primary, declaredRoots: null, linkResolver: Identity,
            labels: new Dictionary<string, string>(ClaudeConfigDir.PathComparer));

        Assert.Null(set[0].CustomLabel);
        Assert.Equal("", set[0].DisplayLabel);
    }
}
