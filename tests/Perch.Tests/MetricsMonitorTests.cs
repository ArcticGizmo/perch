using Perch.Data;
using Xunit;

namespace Perch.Tests;

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
