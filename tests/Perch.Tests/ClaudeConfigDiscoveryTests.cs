using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers M1 config-dir discovery: <see cref="ClaudeConfigDiscovery.Discover"/>'s declaration-first,
/// deduped ordering; the <see cref="ClaudeConfigDiscovery.LooksLikeConfigDir"/> marker gate; the
/// siblings-only home scan (which must not match a scheme's container/shared store); the claude-envs
/// manifest source; and link-resolver-injected dedup that proves junction collapsing without minting a
/// real junction. Each test builds its own throwaway directory tree under the OS temp dir, so it never
/// depends on the pinned fixture <c>CLAUDE_CONFIG_DIR</c> or on the developer's real <c>~/.claude</c>.
/// </summary>
public sealed class ClaudeConfigDiscoveryTests : IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("perch-cfgdir-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private string MakeConfigDir(string relative, string marker = "sessions")
    {
        // Normalised (GetFullPath) so it matches the form Discover stores after its own GetFullPath.
        var dir = Path.GetFullPath(Path.Combine(_sandbox, relative));
        Directory.CreateDirectory(dir);
        switch (marker)
        {
            case "sessions": Directory.CreateDirectory(Path.Combine(dir, "sessions")); break;
            case "claudejson": File.WriteAllText(Path.Combine(dir, ".claude.json"), "{}"); break;
            case "settings+projects":
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{}");
                Directory.CreateDirectory(Path.Combine(dir, "projects"));
                break;
            case "none": break;
        }
        return dir;
    }

    private ClaudeConfigDir Primary() => new(MakeConfigDir(".claude"));

    [Fact]
    public void Discover_IncludesPrimaryThenDeclared_WithProvenance()
    {
        var primary = Primary();
        var declared = MakeConfigDir("work/.claude");

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, new[] { declared });

        Assert.Equal(primary.RealRoot, set[0].RealRoot);
        Assert.Equal(ConfigDirProvenance.Primary, set[0].Provenance);
        Assert.Contains(set, d => d.Root == declared && d.Provenance == ConfigDirProvenance.Declared);
    }

    [Fact]
    public void Discover_DeclaredWinsOverConvention_WhenSameRealPath()
    {
        // A ~/.claude-work sibling is also declared explicitly: it dedups to one entry, and because the
        // declared tier is scanned before the convention tier, that one entry is Declared (writable).
        var primary = Primary();
        var sibling = MakeConfigDir(".claude-work");

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, new[] { sibling });

        var match = set.Where(d => ClaudeConfigDir.PathComparer.Equals(d.RealRoot,
            ClaudeConfigSet.ResolveReal(sibling))).ToList();
        Assert.Single(match);
        Assert.Equal(ConfigDirProvenance.Declared, match[0].Provenance);
    }

    [Fact]
    public void Discover_ConventionScan_PicksUpSiblingConfigDirs()
    {
        var primary = Primary();
        MakeConfigDir(".claude-alt", marker: "settings+projects");

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, declaredRoots: null);

        Assert.Contains(set, d =>
            Path.GetFileName(d.Root) == ".claude-alt" && d.Provenance == ConfigDirProvenance.Convention);
    }

    [Fact]
    public void Discover_SiblingsScan_DoesNotMatchSchemeContainer_ButSourceDoes()
    {
        // ~/.claude-envs is a container (no config-dir markers of its own) — the siblings scan must NOT
        // add it, and must NOT descend into its children. The env dir under envs/ comes only through the
        // manifest source, gated by the marker.
        var primary = Primary();
        MakeConfigDir(".claude-envs", marker: "none");                 // the container itself: no markers
        File.WriteAllText(Path.Combine(_sandbox, ".claude-envs", "manifest.json"), "{}");
        var env = MakeConfigDir(Path.Combine(".claude-envs", "envs", "acme")); // a real env config dir

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, declaredRoots: null);

        Assert.DoesNotContain(set, d => Path.GetFileName(d.Root) == ".claude-envs");
        var acme = Assert.Single(set, d => d.Root == env);
        Assert.Equal("acme", acme.Slug);
        Assert.Equal("acme", acme.Label);
    }

    [Fact]
    public void Discover_ManifestSource_Ignored_WhenNoManifest()
    {
        // Without a manifest, the scheme's envs/ children are not descended into (siblings-only scan).
        var primary = Primary();
        var env = MakeConfigDir(Path.Combine(".claude-envs", "envs", "acme"));

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, declaredRoots: null);

        Assert.DoesNotContain(set, d => d.Root == env);
    }

    [Theory]
    [InlineData("sessions", true)]
    [InlineData("claudejson", true)]
    [InlineData("settings+projects", true)]
    [InlineData("none", false)]
    public void LooksLikeConfigDir_GatesOnMarkers(string marker, bool expected)
    {
        var dir = MakeConfigDir("candidate", marker);
        Assert.Equal(expected, ClaudeConfigDiscovery.LooksLikeConfigDir(dir));
    }

    [Fact]
    public void LooksLikeConfigDir_RejectsSettingsWithoutProjects()
    {
        var dir = Path.Combine(_sandbox, "half");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{}"); // settings alone is not enough
        Assert.False(ClaudeConfigDiscovery.LooksLikeConfigDir(dir));
    }

    [Fact]
    public void Discover_LinkResolverInjection_DedupsWithoutARealJunction()
    {
        // Two distinct declared roots that a scheme has junctioned onto one physical tree: with the link
        // resolver mapping both onto the same "real" path, they collapse to a single set entry.
        var primary = Primary();
        var a = MakeConfigDir("alpha/.claude");
        var b = MakeConfigDir("beta/.claude");
        var shared = Path.Combine(_sandbox, "shared", "store");

        string Resolver(string p) =>
            (p == a || p == b) ? shared : p;

        var set = ClaudeConfigDiscovery.Discover(_sandbox, primary, new[] { a, b }, selfReportedRoots: null, linkResolver: Resolver);

        Assert.Single(set, d => ClaudeConfigDir.PathComparer.Equals(d.RealRoot, shared));
        // Primary + one shared entry (alpha wins, declared before beta).
        Assert.Equal(a, set.Single(d => d.RealRoot == shared).Root);
    }
}
