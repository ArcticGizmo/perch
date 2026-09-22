using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers config-dir <see cref="ConfigDirProvenance"/> assignment and self-report persistence. Provenance
/// is a labelling signal and the input to the automatic-hook-install policy (<c>HookInstaller</c> skips a
/// convention-scan-only dir) — it is <b>not</b> a read-only flag; any dir can be a deliberate write target.
/// Discovery is tested directly (hermetic); the sticky self-report persistence is exercised through the
/// pinned-opt-in test seam with a redirected file.
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
    public void Discovery_assigns_provenance_by_tier()
    {
        var primary = new ClaudeConfigDir(MakeConfigDir(".claude"));
        var declared = MakeConfigDir("work/.claude");
        var selfReported = MakeConfigDir("envs/acme/.claude");
        var convention = MakeConfigDir(".claude-scanonly"); // a sibling the scan will pick up

        var set = ClaudeConfigDiscovery.Discover(
            _sandbox, primary, new[] { declared }, new[] { selfReported });

        ClaudeConfigDir Find(string root) =>
            set.Single(d => ClaudeConfigDir.PathComparer.Equals(d.RealRoot, ClaudeConfigSet.ResolveReal(root)));

        Assert.Equal(ConfigDirProvenance.Primary, Find(primary.Root).Provenance);
        Assert.Equal(ConfigDirProvenance.Declared, Find(declared).Provenance);
        Assert.Equal(ConfigDirProvenance.SelfReported, Find(selfReported).Provenance);
        // A scan-only sibling is Convention — the auto-hook policy skips it, but it's not read-only.
        Assert.Equal(ConfigDirProvenance.Convention, Find(convention).Provenance);
    }

    [Fact]
    public void SelfReport_promotes_a_convention_sibling_above_the_scan()
    {
        // A sibling ~/.claude-x the convention scan would find, but which is ALSO self-reported: the
        // self-reported tier ranks above the convention scan, so it lands SelfReported, not Convention.
        var primary = new ClaudeConfigDir(MakeConfigDir(".claude"));
        var sibling = MakeConfigDir(".claude-x");

        var withoutReport = ClaudeConfigDiscovery.Discover(_sandbox, primary, declaredRoots: null);
        Assert.Equal(ConfigDirProvenance.Convention,
            withoutReport.Single(d => Path.GetFileName(d.Root) == ".claude-x").Provenance);

        var withReport = ClaudeConfigDiscovery.Discover(
            _sandbox, primary, declaredRoots: null, selfReportedRoots: new[] { sibling });
        var promoted = withReport.Single(d => Path.GetFileName(d.Root) == ".claude-x");
        Assert.Equal(ConfigDirProvenance.SelfReported, promoted.Provenance);
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
