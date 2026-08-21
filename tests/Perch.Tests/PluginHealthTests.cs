using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginHealthTests
{
    private static InstalledPluginRecord Enabled() => new() { Id = "x", Enabled = true };

    private static PluginPollResult Ok() => new(null, [], [], TimedOut: false, ExitCode: 0);
    private static PluginPollResult TimedOut() => new(null, [], [], TimedOut: true, ExitCode: -1);

    [Fact]
    public void A_clean_run_resets_the_fault_counter()
    {
        var r = Enabled();
        r.ConsecutiveFaults = 2;
        Assert.False(PluginHealth.RecordResult(r, Ok()));
        Assert.Equal(0, r.ConsecutiveFaults);
        Assert.True(r.Enabled);
    }

    [Fact]
    public void Faults_accumulate_and_auto_disable_at_the_threshold()
    {
        var r = Enabled();
        for (int i = 1; i < PluginHealth.MaxConsecutiveFaults; i++)
        {
            Assert.False(PluginHealth.RecordResult(r, TimedOut()));
            Assert.True(r.Enabled);   // not yet
        }

        // The threshold-th consecutive fault disables it and reports true exactly once.
        Assert.True(PluginHealth.RecordResult(r, TimedOut()));
        Assert.False(r.Enabled);
        Assert.Equal(PluginHealth.MaxConsecutiveFaults, r.ConsecutiveFaults);
    }

    [Fact]
    public void Denied_actions_alone_are_not_a_fault()
    {
        var r = Enabled();
        var ranButMisbehaved = new PluginPollResult(null, [], ["tried to notify"], TimedOut: false, ExitCode: 0);
        Assert.False(PluginHealth.RecordResult(r, ranButMisbehaved));
        Assert.Equal(0, r.ConsecutiveFaults);
        Assert.True(r.Enabled);
    }
}
