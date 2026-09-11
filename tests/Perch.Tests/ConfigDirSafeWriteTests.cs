using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the M4 safe-write policy and self-report persistence. The policy (encoded as
/// <see cref="ClaudeConfigDir.IsWritable"/> / <see cref="ConfigDirProvenance"/>) is that Perch reads from
/// every discovered dir but only ever writes a hook into the primary, a declared dir, or a self-reported
/// dir — never a dir found by the convention scan alone. Discovery is tested directly (hermetic); the
/// sticky self-report persistence is exercised through the pinned-opt-in test seam with a redirected file.
/// </summary>
public sealed class ConfigDirSafeWriteTests : IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("perch-safewrite-").FullName;

    public void Dispose()
    {
        ClaudeConfigSet.ResetForTesting();
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private string MakeConfigDir(string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(_sandbox, relative));
        Directory.CreateDirectory(Path.Combine(dir, "sessions")); // a marker so LooksLikeConfigDir accepts it
        return dir;
    }

    [Fact]
    public void SafeWrite_OnlyPrimaryDeclaredAndSelfReported_AreWritable()
    {
        var primary = new ClaudeConfigDir(MakeConfigDir(".claude"));
        var declared = MakeConfigDir("work/.claude");
        var selfReported = MakeConfigDir("envs/acme/.claude");
        var convention = MakeConfigDir(".claude-scanonly"); // a sibling the scan will pick up

        var set = ClaudeConfigDiscovery.Discover(
            _sandbox, primary, new[] { declared }, new[] { selfReported });

        ClaudeConfigDir Find(string root) =>
            set.Single(d => ClaudeConfigDir.PathComparer.Equals(d.RealRoot, ClaudeConfigSet.ResolveReal(root)));

        Assert.True(Find(primary.Root).IsWritable);
        Assert.Equal(ConfigDirProvenance.Declared, Find(declared).Provenance);
        Assert.True(Find(declared).IsWritable);
        Assert.Equal(ConfigDirProvenance.SelfReported, Find(selfReported).Provenance);
        Assert.True(Find(selfReported).IsWritable);

        var conv = Find(convention);
        Assert.Equal(ConfigDirProvenance.Convention, conv.Provenance);
        Assert.False(conv.IsWritable); // the safety property: never written to
    }

    [Fact]
    public void SelfReport_PromotesAConventionSiblingToWritable()
    {
        // A sibling ~/.claude-x that the convention scan would find, but which is ALSO self-reported: the
        // self-reported tier ranks above the convention scan, so it lands writable, not convention-only.
        var primary = new ClaudeConfigDir(MakeConfigDir(".claude"));
        var sibling = MakeConfigDir(".claude-x");

        var withoutReport = ClaudeConfigDiscovery.Discover(_sandbox, primary, declaredRoots: null);
        Assert.Equal(ConfigDirProvenance.Convention,
            withoutReport.Single(d => Path.GetFileName(d.Root) == ".claude-x").Provenance);

        var withReport = ClaudeConfigDiscovery.Discover(
            _sandbox, primary, declaredRoots: null, selfReportedRoots: new[] { sibling });
        var promoted = withReport.Single(d => Path.GetFileName(d.Root) == ".claude-x");
        Assert.Equal(ConfigDirProvenance.SelfReported, promoted.Provenance);
        Assert.True(promoted.IsWritable);
    }

    [Fact]
    public void SelfReportPersistence_RoundTrips()
    {
        var persistenceFile = Path.Combine(_sandbox, "config-dirs.json");
        ClaudeConfigSet.PersistencePathForTesting = persistenceFile;
        ClaudeConfigSet.AllowSelfReportWhenPinnedForTesting = true;

        var root = Path.GetFullPath(Path.Combine(_sandbox, "envs", "beta", ".claude"));
        ClaudeConfigSet.NoteSelfReport(root);

        Assert.True(File.Exists(persistenceFile));
        Assert.Contains(root, ClaudeConfigSet.SelfReportedForTesting());

        // Reload from disk into a cleared in-memory set — the sticky root survives.
        ClaudeConfigSet.ReloadSelfReportedForTesting();
        Assert.Contains(root, ClaudeConfigSet.SelfReportedForTesting());
    }
}
