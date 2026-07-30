using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Exercises <see cref="DaemonRosterReader"/> against the fixture roster
/// (<c>fixtures/claude/daemon/roster.json</c>): a dispatched (named) worker, a pre-warmed spare, and a
/// worker whose pid is dead — three real shapes captured from a live daemon. The probe is faked so the
/// fixture's recorded pids read as alive or dead deterministically.
/// </summary>
public class DaemonRosterReaderTests
{
    private const int SparePid = 62548, NamedPid = 61112, DeadPid = 99999;

    private sealed class FakeProbe(params int[] alive) : IProcessProbe
    {
        public bool IsAlive(int pid) => alive.Contains(pid);
    }

    [Fact]
    public void Read_ReturnsLiveWorkers_OldestFirst_AndDropsDeadPids()
    {
        var workers = DaemonRosterReader.Read(new FakeProbe(SparePid, NamedPid));

        Assert.Equal(2, workers.Count);
        // The dead-pid worker (startedAt latest) is dropped entirely.
        Assert.DoesNotContain(workers, w => w.Pid == DeadPid);
        // Stable order: oldest started first.
        Assert.Equal("b98bfb1f", workers[0].ShortId);
        Assert.Equal("f7d0b5fc", workers[1].ShortId);
    }

    [Fact]
    public void Read_ParsesDispatchedWorker_PreferringSeedNameOverIntent()
    {
        var workers = DaemonRosterReader.Read(new FakeProbe(SparePid, NamedPid));
        var named = Assert.Single(workers, w => w.ShortId == "f7d0b5fc");

        Assert.Equal("f7d0b5fc-e679-492f-9fee-18a29f41602a", named.SessionId);
        Assert.Equal(NamedPid, named.Pid);
        Assert.Equal("slash", named.Source);
        Assert.False(named.IsSpare);
        Assert.Equal("hypertree", named.ProjectName);
        // seed.name wins over seed.intent, and is what the strip shows.
        Assert.Equal("Implement streamlined PowerShell install pathway", named.Name);
        Assert.Equal("Implement streamlined PowerShell install pathway", named.DisplayName);
    }

    [Fact]
    public void Read_ParsesSpareWorker_WithNoName_FallingBackToProject()
    {
        var workers = DaemonRosterReader.Read(new FakeProbe(SparePid, NamedPid));
        var spare = Assert.Single(workers, w => w.ShortId == "b98bfb1f");

        Assert.True(spare.IsSpare);
        // A blank seed intent must not become an empty-string name.
        Assert.Null(spare.Name);
        Assert.Equal("hypertree", spare.DisplayName);
    }

    [Fact]
    public void Read_ReturnsEmpty_WhenEveryPidIsDead()
    {
        Assert.Empty(DaemonRosterReader.Read(new FakeProbe()));
    }
}
