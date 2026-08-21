using Perch.Plugins;
using Xunit;

namespace Perch.Tests;

public class PluginSessionTests
{
    private static PluginRequest Poll() =>
        new(PluginRequest.PollType, "0.9.0", [], new Dictionary<string, string>());

    [Fact]
    public async Task Sends_the_request_then_collects_response_lines()
    {
        var proc = new FakePluginProcess(
        [
            """{"type":"log","level":"info","message":"starting"}""",
            """{"type":"render","glyph":{"glyph":"☀","text":"24°"}}""",
        ]);

        var result = await PluginSession.RunOnceAsync(proc, Poll(), TimeSpan.FromSeconds(5));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Messages.Count);
        Assert.Contains(result.Messages, m => m is PluginRenderMessage);
        Assert.Contains("\"type\":\"poll\"", proc.CapturedStdin);  // the request went out
    }

    [Fact]
    public async Task Skips_garbage_lines_but_keeps_valid_ones()
    {
        var proc = new FakePluginProcess(
        [
            "this is not json",
            "",
            """{"type":"notify","title":"hi","body":"there"}""",
        ]);

        var result = await PluginSession.RunOnceAsync(proc, Poll(), TimeSpan.FromSeconds(5));
        Assert.Single(result.Messages);
        Assert.IsType<PluginNotifyMessage>(result.Messages[0]);
    }

    [Fact]
    public async Task A_hung_plugin_is_killed_and_reported_as_timed_out()
    {
        var proc = new FakePluginProcess([], hang: true);

        var result = await PluginSession.RunOnceAsync(proc, Poll(), TimeSpan.FromMilliseconds(100));

        Assert.True(result.TimedOut);
        Assert.True(proc.Killed);
    }
}
