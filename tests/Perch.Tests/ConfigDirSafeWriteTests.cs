using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers session self-reports: a <c>.configdir</c> marker naming a dir outside the set is remembered (and
/// persisted) as a scan suggestion, never added to the set. The sticky persistence is exercised through the
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

    [Fact]
    public void SelfReport_IsRemembered_ButNeverJoinsTheSet()
    {
        ClaudeConfigSet.PersistencePathForTesting = Path.Combine(_sandbox, "config-dirs.json");
        ClaudeConfigSet.AllowSelfReportWhenPinnedForTesting = true;
        var before = ClaudeConfigSet.Instance.All.Count;

        ClaudeConfigSet.NoteSelfReport(Path.Combine(_sandbox, "reported", ".claude"));

        Assert.Single(ClaudeConfigSet.SelfReportedForTesting());
        Assert.Equal(before, ClaudeConfigSet.Instance.All.Count);
    }

    [Fact]
    public void SelfReport_OfADirAlreadyInTheSet_IsNotRecorded()
    {
        ClaudeConfigSet.PersistencePathForTesting = Path.Combine(_sandbox, "config-dirs.json");
        ClaudeConfigSet.AllowSelfReportWhenPinnedForTesting = true;

        ClaudeConfigSet.NoteSelfReport(ClaudeConfigSet.Instance.Primary.Root);

        Assert.Empty(ClaudeConfigSet.SelfReportedForTesting());
    }
}
