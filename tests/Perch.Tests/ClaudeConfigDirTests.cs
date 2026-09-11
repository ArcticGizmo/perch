using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the M0 config-dir value model: <see cref="ClaudeConfigDir"/>'s derived paths, its
/// identity-by-<c>RealRoot</c> equality, <see cref="ClaudeConfigSet.ResolveReal"/> on an ordinary
/// directory, and the golden check that <see cref="ClaudePaths"/> still resolves to exactly the
/// strings it did before it became a delegate (the whole suite runs under a pinned
/// <c>CLAUDE_CONFIG_DIR</c>, so the primary is the fixture tree).
/// </summary>
public class ClaudeConfigDirTests
{
    [Fact]
    public void DerivedPaths_MatchLayout()
    {
        var root = @"C:\envs\work\.claude";
        var dir = new ClaudeConfigDir(root);

        Assert.Equal(root, dir.Root);
        Assert.Equal(Path.Combine(root, "sessions"), dir.SessionsDir);
        Assert.Equal(Path.Combine(root, "projects"), dir.ProjectsDir);
        Assert.Equal(Path.Combine(root, "plugins"), dir.PluginsDir);
        Assert.Equal(Path.Combine(root, "daemon"), dir.DaemonDir);
        Assert.Equal(Path.Combine(root, "daemon", "roster.json"), dir.DaemonRosterFile);
        Assert.Equal(Path.Combine(root, ".credentials.json"), dir.CredentialsFile);
        Assert.Equal(Path.Combine(root, "settings.json"), dir.UserSettingsFile);
        Assert.Equal(Path.Combine(root, "image-cache"), dir.ImageCacheDir);
    }

    [Fact]
    public void Label_PrefersSlug_ThenDirName()
    {
        Assert.Equal("work", new ClaudeConfigDir(@"C:\envs\work\.claude", slug: "work").Label);
        Assert.Equal(".claude", new ClaudeConfigDir(@"C:\Users\me\.claude").Label);
    }

    [Fact]
    public void Equality_IsByRealRoot_NotRoot()
    {
        // Two different declared roots that resolve to one physical tree are the same config dir.
        var a = new ClaudeConfigDir(@"C:\envs\alpha\.claude", realRoot: @"C:\shared\store");
        var b = new ClaudeConfigDir(@"C:\envs\beta\.claude", realRoot: @"C:\shared\store");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var c = new ClaudeConfigDir(@"C:\envs\alpha\.claude", realRoot: @"C:\shared\other");
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void RealRoot_TrimsTrailingSeparator()
    {
        var a = new ClaudeConfigDir(@"C:\envs\work\.claude\");
        var b = new ClaudeConfigDir(@"C:\envs\work\.claude");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveReal_OnOrdinaryDir_ReturnsItself()
    {
        var real = ClaudeConfigSet.ResolveReal(TestEnvironment.FixtureConfigDir);
        // A plain (non-link) directory resolves to itself; a non-existent path also returns unchanged.
        Assert.Equal(TestEnvironment.FixtureConfigDir, real);
        Assert.Equal(@"C:\does\not\exist", ClaudeConfigSet.ResolveReal(@"C:\does\not\exist"));
    }

    [Fact]
    public void ClaudePaths_StillResolveThroughTheFixturePrimary()
    {
        var claude = TestEnvironment.FixtureConfigDir;
        Assert.Equal(claude, ClaudePaths.ClaudeDir);
        Assert.Equal(Path.Combine(claude, "sessions"), ClaudePaths.SessionsDir);
        Assert.Equal(Path.Combine(claude, "projects"), ClaudePaths.ProjectsDir);
        Assert.Equal(Path.Combine(claude, "plugins"), ClaudePaths.PluginsDir);
        Assert.Equal(Path.Combine(claude, "daemon"), ClaudePaths.DaemonDir);
        Assert.Equal(Path.Combine(claude, "daemon", "roster.json"), ClaudePaths.DaemonRosterFile);
        Assert.Equal(Path.Combine(claude, ".credentials.json"), ClaudePaths.CredentialsFile);
        Assert.Equal(Path.Combine(claude, "settings.json"), ClaudePaths.UserSettingsFile);
    }

    [Fact]
    public void ConfigSet_M0_IsPrimaryOnly()
    {
        var set = ClaudeConfigSet.Instance;
        Assert.Single(set.All);
        Assert.False(set.IsMulti);
        Assert.Equal(TestEnvironment.FixtureConfigDir, set.Primary.Root);
        Assert.Equal(ConfigDirProvenance.Primary, set.Primary.Provenance);
    }
}
