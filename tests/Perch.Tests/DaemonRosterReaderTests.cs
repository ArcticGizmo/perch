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

    // Review fixes CP25: a field of an unexpected type used to throw out of the whole read, so one odd worker
    // emptied the daemon strip.
    [Fact]
    public void Parse_MistypedFields_SkipOnlyThatWorker_OrJustThatField()
    {
        const string json = """
            {"workers":{
              "aaaaaaaa":{"pid":"61112","sessionId":"aaaaaaaa-1"},
              "bbbbbbbb":{"pid":62548,"sessionId":12345},
              "cccccccc":{"pid":61112,"sessionId":"cccccccc-1","cwd":42,"startedAt":"soon",
                          "dispatch":{"source":["slash"],"seed":{"name":7,"intent":"fix the build"}}},
              "dddddddd":{"pid":62548.0,"sessionId":"dddddddd-1","startedAt":1759200000000},
              "eeeeeeee":"not an object"
            }}
            """;
        var workers = DaemonRosterReader.Parse(json, new FakeProbe(SparePid, NamedPid));

        // A string pid and a numeric sessionId disqualify their workers; the rest survive.
        Assert.Equal(["cccccccc", "dddddddd"], workers.Select(w => w.ShortId).Order());
        var odd = Assert.Single(workers, w => w.ShortId == "cccccccc");
        Assert.Equal("", odd.Cwd);                     // mistyped optional fields read as absent
        Assert.Equal("", odd.Source);
        Assert.Equal("fix the build", odd.Name);       // a non-string name falls through to the intent
        Assert.Equal(DateTime.MinValue, odd.StartedAt);
        Assert.Equal(SparePid, Assert.Single(workers, w => w.ShortId == "dddddddd").Pid);   // a pid written as a double
    }

    [Fact]
    public void Parse_PassesTheRecordedStartToTheProbe()
    {
        const string json = """{"workers":{"aaaaaaaa":{"pid":61112,"sessionId":"s","startedAt":1759200000000}}}""";
        var probe = new StartCapturingProbe();
        DaemonRosterReader.Parse(json, probe);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1759200000000).LocalDateTime, probe.Seen);
    }

    private sealed class StartCapturingProbe : IProcessProbe
    {
        public DateTime? Seen;
        public bool IsAlive(int pid) => true;
        public bool IsAlive(int pid, DateTime? startedAt) { Seen = startedAt; return true; }
    }
}
