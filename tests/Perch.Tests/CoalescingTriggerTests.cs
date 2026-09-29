using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP4: the feed poll's coalescer. A burst of requests (e.g. a flood of inbox broadcasts) must cost
/// one run in flight plus one trailing run, runs must start at least the gap apart, and an isolated request must
/// run straight away. Time and delays are injected, so nothing here sleeps.
/// </summary>
public sealed class CoalescingTriggerTests
{
    // A manual clock plus a delay that just advances it, and work that stays "in flight" until released.
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public readonly List<DateTimeOffset> Starts = new();
        public TaskCompletionSource InFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CoalescingTrigger Trigger;

        public Harness(TimeSpan gap) => Trigger = new CoalescingTrigger(
            () => { Starts.Add(Now); return InFlight.Task; }, gap,
            now: () => Now,
            delay: d => { Now += d; return Task.CompletedTask; });

        // Lets the current run finish and waits for the trigger to settle.
        public async Task FinishRunAsync()
        {
            var done = InFlight;
            InFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.SetResult();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task An_isolated_request_runs_immediately()
    {
        var h = new Harness(TimeSpan.FromSeconds(2));
        h.Trigger.Request();
        Assert.Single(h.Starts);
        await h.FinishRunAsync();
        Assert.Single(h.Starts);
    }

    [Fact]
    public async Task A_burst_during_a_run_collapses_into_one_trailing_run()
    {
        var h = new Harness(TimeSpan.FromSeconds(2));
        h.Trigger.Request();
        for (int i = 0; i < 50; i++) h.Trigger.Request();   // the flood
        Assert.Single(h.Starts);

        await h.FinishRunAsync();
        Assert.Equal(2, h.Starts.Count);                    // exactly one trailing run...
        Assert.True(h.Starts[1] - h.Starts[0] >= TimeSpan.FromSeconds(2));   // ...started a full gap later

        await h.FinishRunAsync();
        Assert.Equal(2, h.Starts.Count);                    // and nothing after it
    }

    [Fact]
    public async Task A_later_request_runs_again_once_idle()
    {
        var h = new Harness(TimeSpan.FromSeconds(2));
        h.Trigger.Request();
        await h.FinishRunAsync();
        h.Now += TimeSpan.FromSeconds(10);
        h.Trigger.Request();
        Assert.Equal(2, h.Starts.Count);
        Assert.Equal(h.Now, h.Starts[1]);                   // no gap to wait out: ran at once
    }

    [Fact]
    public async Task A_throwing_run_does_not_wedge_the_trigger()
    {
        int runs = 0;
        var t = new CoalescingTrigger(() => { runs++; throw new InvalidOperationException(); }, TimeSpan.Zero);
        t.Request();
        await Task.Delay(50);
        t.Request();
        await Task.Delay(50);
        Assert.Equal(2, runs);
    }
}
