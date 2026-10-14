using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers config-dir membership and the suggestion scan: <see cref="ClaudeConfigDiscovery.Discover"/> builds
/// the set from the primary plus declared roots only (nothing joins by pattern-matching), deduped by real
/// path; <see cref="ClaudeConfigDiscovery.Suggest"/> recommends what the convention scan finds — siblings-only
/// home scan (which must not match a scheme's container/shared store), the claude-envs <c>envs/*</c> source and
/// session self-reports — minus what's already in the set; and the <see cref="ClaudeConfigDiscovery.LooksLikeConfigDir"/>
/// marker gate. Each test builds its own throwaway directory tree under the OS temp dir, so it never depends
/// on the pinned fixture <c>CLAUDE_CONFIG_DIR</c> or on the developer's real <c>~/.claude</c>.
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

        var set = ClaudeConfigDiscovery.Discover(primary, new[] { declared });

        Assert.Equal(primary.RealRoot, set[0].RealRoot);
        Assert.Equal(ConfigDirProvenance.Primary, set[0].Provenance);
        Assert.Contains(set, d => d.Root == declared && d.Provenance == ConfigDirProvenance.Declared);
    }

    [Fact]
    public void Discover_NeverAddsUndeclaredSiblings()
    {
        // A ~/.claude-alt sibling that looks exactly like a config dir stays out until the user adds it.
        var primary = Primary();
        MakeConfigDir(".claude-alt", marker: "settings+projects");

        var set = ClaudeConfigDiscovery.Discover(primary, declaredRoots: null);

        Assert.Single(set);
    }

    [Fact]
    public void Suggest_PicksUpSiblingConfigDirs()
    {
        var primary = Primary();
        MakeConfigDir(".claude-alt", marker: "settings+projects");

        var found = ClaudeConfigDiscovery.Suggest(_sandbox, new[] { primary });

        Assert.Contains(found, s =>
            Path.GetFileName(s.Root) == ".claude-alt" && s.Source == ConfigDirSuggestionSource.HomeFolder);
        Assert.DoesNotContain(found, s => s.RealRoot == primary.RealRoot); // already in the set
    }

    [Fact]
    public void Suggest_SkipsWhatIsAlreadyDeclared()
    {
        var primary = Primary();
        var sibling = MakeConfigDir(".claude-work");
        var set = ClaudeConfigDiscovery.Discover(primary, new[] { sibling });

        var found = ClaudeConfigDiscovery.Suggest(_sandbox, set);

        Assert.DoesNotContain(found, s => Path.GetFileName(s.Root) == ".claude-work");
    }

    [Fact]
    public void Suggest_SiblingsScan_DoesNotMatchSchemeContainer_ButEnvsSourceDoes()
    {
        // ~/.claude-envs is a container (no config-dir markers of its own) — the siblings scan must NOT
        // suggest it, and must NOT descend into its children. The env dir under envs/ comes only through the
        // claude-envs source, gated by the marker.
        var primary = Primary();
        MakeConfigDir(".claude-envs", marker: "none");                 // the container itself: no markers
        var env = MakeConfigDir(Path.Combine(".claude-envs", "envs", "acme")); // a real env config dir

        var found = ClaudeConfigDiscovery.Suggest(_sandbox, new[] { primary });

        Assert.DoesNotContain(found, s => Path.GetFileName(s.Root) == ".claude-envs");
        var acme = Assert.Single(found, s => s.Root == env);
        Assert.Equal("acme", acme.Label);
        Assert.Equal(ConfigDirSuggestionSource.Scheme, acme.Source);
    }

    [Fact]
    public void Suggest_EnvsSource_NeedsNoManifest_ButStillGatesOnMarkers()
    {
        // No manifest.json/envs.json: every envs/* child is still looked at, and only the one that looks like a
        // config dir (here, just a .claude.json) is suggested.
        var primary = Primary();
        var env = MakeConfigDir(Path.Combine(".claude-envs", "envs", "incontrol"), marker: "claudejson");
        var empty = MakeConfigDir(Path.Combine(".claude-envs", "envs", "empty"), marker: "none");

        var found = ClaudeConfigDiscovery.Suggest(_sandbox, new[] { primary });

        Assert.Contains(found, s => s.Root == env && s.Source == ConfigDirSuggestionSource.Scheme);
        Assert.DoesNotContain(found, s => s.Root == empty);
    }

    [Fact]
    public void EnvsSource_RequireManifest_MatchesTheOldDiscovery()
    {
        // The migration's view: without a manifest the old discovery never looked in envs/, so neither does this.
        var env = MakeConfigDir(Path.Combine(".claude-envs", "envs", "acme"));
        var legacy = new ClaudeEnvsSource(_sandbox, requireManifest: true);

        Assert.Empty(legacy.Enumerate());
        File.WriteAllText(Path.Combine(_sandbox, ".claude-envs", "envs.json"), "{}");
        Assert.Contains(legacy.Enumerate(), e => e.Root == env);
    }

    [Fact]
    public void Suggest_ReportedRoots_ComeFirst_AndAreMarkerGated()
    {
        // A session-reported dir is suggested ahead of the scan's own find for the same folder; a reported
        // path that no longer looks like a config dir is dropped.
        var primary = Primary();
        var sibling = MakeConfigDir(".claude-x");
        var gone = Path.Combine(_sandbox, "deleted", ".claude");

        var found = ClaudeConfigDiscovery.Suggest(_sandbox, new[] { primary }, reportedRoots: new[] { sibling, gone });

        var x = Assert.Single(found, s => s.Root == sibling);
        Assert.Equal(ConfigDirSuggestionSource.ReportedBySession, x.Source);
        Assert.DoesNotContain(found, s => s.Root == gone);
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

        var set = ClaudeConfigDiscovery.Discover(primary, new[] { a, b }, linkResolver: Resolver);

        Assert.Single(set, d => ClaudeConfigDir.PathComparer.Equals(d.RealRoot, shared));
        // Primary + one shared entry (alpha wins, declared before beta).
        Assert.Equal(a, set.Single(d => d.RealRoot == shared).Root);
    }
}
