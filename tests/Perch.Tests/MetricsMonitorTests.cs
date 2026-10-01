using Perch.Data;
using Perch.Platform;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP24: per-session sampling reads each process through the platform seam
/// (<see cref="ISystemMetrics.ReadProcess"/>) once per tick. It used to call
/// <c>Process.GetProcessById(pid).WorkingSet64</c> per process, which on Windows snapshots every process
/// on the machine each time.
/// </summary>
public sealed class MetricsMonitorSamplingTests
{
    private sealed class FakeMetrics : ISystemMetrics
    {
        public readonly Dictionary<int, int> Parents = new();
        public readonly Dictionary<int, long> WorkingSets = new();
        public readonly Dictionary<int, int> Reads = new();

        public (ulong idle, ulong kernel, ulong user)? ReadCpuTimes() => (0, 0, 0);
        public (long used, long total) ReadMemory() => (1, 2);
        public IReadOnlyDictionary<int, int> ReadParentMap() => Parents;

        public (long workingSet, TimeSpan cpu)? ReadProcess(int pid)
        {
            lock (Reads) Reads[pid] = Reads.GetValueOrDefault(pid) + 1;
            return WorkingSets.TryGetValue(pid, out var ws) ? (ws, TimeSpan.Zero) : null;
        }
    }

    [Fact]
    public void Tree_is_summed_and_each_pid_read_once_per_tick()
    {
        var fake = new FakeMetrics();
        // Session 10 spawns 11, which spawns 12. Session 20 is alone. Pid 13 (a child of 10) has gone.
        fake.Parents[10] = 1; fake.Parents[11] = 10; fake.Parents[12] = 11; fake.Parents[13] = 10;
        fake.Parents[20] = 1;
        fake.WorkingSets[10] = 100; fake.WorkingSets[11] = 20; fake.WorkingSets[12] = 3;
        fake.WorkingSets[20] = 7;

        using var monitor = new MetricsMonitor(fake);
        // Session 10 is listed twice, so its whole tree would be read twice without the per-tick memo.
        monitor.SetSessionPids(["10", "20", "10"]);

        IReadOnlyDictionary<string, SessionMetrics>? first = null;
        using var got = new ManualResetEventSlim();
        monitor.Updated += (_, sessions) =>
        {
            if (first != null) return;
            first = sessions;
            got.Set();
        };
        monitor.Configure(system: false, perSession: true, subprocess: true);
        Assert.True(got.Wait(TimeSpan.FromSeconds(10)));
        monitor.Configure(system: false, perSession: false, subprocess: false);

        Assert.Equal(123, first!["10"].RamBytes);
        Assert.Equal(3, first["10"].ProcessCount);
        Assert.Equal(7, first["20"].RamBytes);
        Assert.Equal(1, first["20"].ProcessCount);
        lock (fake.Reads)
            foreach (var pid in new[] { 10, 11, 12, 13, 20 })
                Assert.Equal(1, fake.Reads[pid]);
    }
}

public class MetricsMathTests
{
    [Fact]
    public void SystemCpuPercent_HalfIdle_IsFifty()
    {
        // kernelDelta includes idle; busy = kernel + user − idle. 60 kernel (40 of it idle) + 20 user
        // = 80 total, 40 idle → 40 busy → 50%.
        Assert.Equal(50, MetricsMath.SystemCpuPercent(idleDelta: 40, kernelDelta: 60, userDelta: 20), 3);
    }

    [Fact]
    public void SystemCpuPercent_FullyIdle_IsZero()
    {
        Assert.Equal(0, MetricsMath.SystemCpuPercent(idleDelta: 100, kernelDelta: 100, userDelta: 0), 3);
    }

    [Fact]
    public void SystemCpuPercent_NoElapsedTime_IsZero()
    {
        // The priming sample: no ticks elapsed yet, so there's nothing to divide by.
        Assert.Equal(0, MetricsMath.SystemCpuPercent(0, 0, 0));
    }

    [Fact]
    public void ProcessCpuPercent_OneCoreFullyBusy_IsPerCoreShare()
    {
        // 1s of CPU over a 1s window on a 4-core box = one core pegged = 25% of the machine.
        var pct = MetricsMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), cores: 4);
        Assert.Equal(25, pct, 3);
    }

    [Fact]
    public void ProcessCpuPercent_CannotExceedHundred()
    {
        // More CPU time than the wall-clock × cores would allow is clamped rather than overflowing.
        var pct = MetricsMath.ProcessCpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), cores: 4);
        Assert.Equal(100, pct);
    }

    [Fact]
    public void ProcessCpuPercent_ZeroWindow_IsZero()
    {
        Assert.Equal(0, MetricsMath.ProcessCpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, cores: 4));
    }
}

public class ProcessTreeTests
{
    [Fact]
    public void SelfAndDescendants_CollectsWholeTree()
    {
        // 100 (root) → 200, 300 ; 200 → 400 ; 999 is unrelated.
        var parents = new Dictionary<int, int>
        {
            [100] = 1,
            [200] = 100,
            [300] = 100,
            [400] = 200,
            [999] = 1,
        };

        var tree = ProcessTree.SelfAndDescendants(100, parents);

        Assert.Equal(new[] { 100, 200, 300, 400 }, tree.OrderBy(x => x));
        Assert.DoesNotContain(999, tree);
    }

    [Fact]
    public void SelfAndDescendants_LeafRoot_IsJustItself()
    {
        var parents = new Dictionary<int, int> { [100] = 1, [200] = 1 };
        Assert.Equal(new[] { 100 }, ProcessTree.SelfAndDescendants(100, parents));
    }

    [Fact]
    public void SelfAndDescendants_SelfParentCycle_DoesNotLoop()
    {
        // A pid listed as its own parent (or a recycled-pid cycle) must not spin the walk.
        var parents = new Dictionary<int, int> { [100] = 100, [200] = 100 };
        var tree = ProcessTree.SelfAndDescendants(100, parents);
        Assert.Equal(new[] { 100, 200 }, tree.OrderBy(x => x));
    }
}
