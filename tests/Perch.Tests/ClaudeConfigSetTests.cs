using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="ClaudeConfigSet"/>'s live surface: the pinned-hermetic guard (the suite always runs
/// under a pinned <c>CLAUDE_CONFIG_DIR</c>, so discovery must stay collapsed to the primary), the
/// test-only <see cref="ClaudeConfigSet.SetForTesting"/> swap + <see cref="ClaudeConfigSet.Changed"/>
/// event, distinct-dir dedup, and <see cref="ClaudeConfigSet.ForRoot"/> attribution (which must never
/// fall back to the primary). Each test restores the real set in <see cref="Dispose"/>.
/// </summary>
public sealed class ClaudeConfigSetTests : IDisposable
{
    public void Dispose() => ClaudeConfigSet.ResetForTesting();

    [Fact]
    public void PinnedByTestEnv_SoDiscoveryStaysCollapsed()
    {
        Assert.True(ClaudeConfigSet.IsPinned);

        var before = ClaudeConfigSet.Instance;
        ClaudeConfigSet.RefreshNow();       // no-op while pinned
        ClaudeConfigSet.RefreshIfStale();   // no-op while pinned

        Assert.Same(before, ClaudeConfigSet.Instance);
        Assert.Single(ClaudeConfigSet.Instance.All);
    }

    [Fact]
    public void SetForTesting_SwapsMembership_AndFiresChanged()
    {
        var fired = 0;
        void Handler() => fired++;
        ClaudeConfigSet.Changed += Handler;
        try
        {
            var a = new ClaudeConfigDir(@"C:\envs\a\.claude");
            var b = new ClaudeConfigDir(@"C:\envs\b\.claude");
            ClaudeConfigSet.SetForTesting(new[] { a, b });

            Assert.True(ClaudeConfigSet.Instance.IsMulti);
            Assert.Equal(2, ClaudeConfigSet.Instance.All.Count);
            Assert.Same(a, ClaudeConfigSet.Instance.Primary);
            Assert.Equal(1, fired);
        }
        finally
        {
            ClaudeConfigSet.Changed -= Handler;
        }
    }

    [Fact]
    public void DistinctSessionsAndProjectsDirs_OneEntryPerUnsharedDir()
    {
        using var sandbox = new TempDir();
        var a = new ClaudeConfigDir(sandbox.Sub("a"));
        var b = new ClaudeConfigDir(sandbox.Sub("b"));
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        var sessions = ClaudeConfigSet.Instance.DistinctSessionsDirs();
        Assert.Equal(2, sessions.Count);
        Assert.Contains(a.SessionsDir, sessions);
        Assert.Contains(b.SessionsDir, sessions);

        var projects = ClaudeConfigSet.Instance.DistinctProjectsDirs();
        Assert.Equal(2, projects.Count);
        Assert.Contains(a.ProjectsDir, projects);
    }

    [Fact]
    public void ForRoot_ResolvesKnownDir_ButNeverFallsBackToPrimary()
    {
        var a = new ClaudeConfigDir(@"C:\envs\a\.claude");
        var b = new ClaudeConfigDir(@"C:\envs\b\.claude");
        ClaudeConfigSet.SetForTesting(new[] { a, b });

        Assert.Equal(b, ClaudeConfigSet.Instance.ForRoot(@"C:\envs\b\.claude"));
        Assert.Null(ClaudeConfigSet.Instance.ForRoot(@"C:\envs\unknown\.claude"));
        Assert.Null(ClaudeConfigSet.Instance.ForRoot(null));
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("perch-cfgset-").FullName;
        public string Sub(string name)
        {
            var p = Path.Combine(_root, name);
            Directory.CreateDirectory(p);
            return p;
        }
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    }
}
