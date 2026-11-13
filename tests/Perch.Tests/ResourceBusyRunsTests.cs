using Perch.Data.Resources;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The runaway-process finding walks each process's own samples (perf review PF16). It must find exactly the runs the
/// old whole-recording scan found: maximal consecutive ticks at or above the threshold (a tick the process is absent
/// from counts as idle), at least the minimum length, each with its first highest tick and its average.
/// </summary>
public sealed class ResourceBusyRunsTests
{
    // The old form: a tick → CPU map, then a scan of every tick of the recording.
    private static List<(int Start, int End, int Peak, double Avg)> Reference(
        List<(int tick, ProcSample sample)> series, int tickCount, double threshold, int minTicks)
    {
        var byTick = series.ToDictionary(x => x.tick, x => x.sample.Cpu);
        var result = new List<(int, int, int, double)>();
        int start = -1;
        for (int i = 0; i <= tickCount; i++)
        {
            bool on = i < tickCount && byTick.GetValueOrDefault(i) >= threshold;
            if (on && start < 0) start = i;
            else if (!on && start >= 0)
            {
                int end = i - 1;
                if (end - start + 1 >= minTicks)
                {
                    int peak = start;
                    for (int k = start; k <= end; k++) if (byTick.GetValueOrDefault(k) > byTick.GetValueOrDefault(peak)) peak = k;
                    result.Add((start, end, peak, Enumerable.Range(start, end - start + 1).Average(k => byTick.GetValueOrDefault(k))));
                }
                start = -1;
            }
        }
        return result;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Matches_the_whole_recording_scan(int seed)
    {
        var rng = new Random(seed);
        const int ticks = 400;
        for (int proc = 0; proc < 30; proc++)
        {
            // A process that comes and goes, with bursts of load.
            var series = new List<(int tick, ProcSample sample)>();
            for (int t = 0; t < ticks; t++)
            {
                if (rng.NextDouble() < 0.1) continue;   // absent this tick
                double cpu = rng.NextDouble() < 0.6 ? 20 + rng.Next(0, 60) : rng.Next(0, 20);
                if (rng.NextDouble() < 0.1) cpu = 25;   // ties for the peak, and exactly the threshold
                series.Add((t, new ProcSample(proc, cpu, 0, 0, 0, 0)));
            }
            foreach (int minTicks in new[] { 1, 3, 12 })
            {
                var expected = Reference(series, ticks, 25, minTicks);
                var actual = ResourceAnalysis.BusyRuns(series, 25, minTicks).ToList();
                Assert.Equal(expected.Count, actual.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Equal((expected[i].Start, expected[i].End, expected[i].Peak), (actual[i].Start, actual[i].End, actual[i].Peak));
                    Assert.Equal(expected[i].Avg, actual[i].Avg, 9);
                }
            }
        }
    }
}
