using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class SubAgentReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    [Fact]
    public void GetRunning_Legacy_ReturnsOnlyTasksWithoutAResult()
    {
        // sessB has two Task tool_uses; tk2 has a matching tool_result (finished), tk1 does not (running).
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessB", Cwd);
        var sub = Assert.Single(running);
        Assert.Equal("tk1", sub.AgentId);
        Assert.Equal("explore code", sub.Description);
        Assert.Equal("Explore", sub.AgentType);
    }

    [Fact]
    public void GetRunning_BackgroundLayout_ReadsSubagentsDirAndMeta()
    {
        // sessBg uses the 2.1+ layout: a {sessionId}/subagents/agent-*.jsonl whose latest turn is an
        // unfinished tool_use, described by its agent-*.meta.json sidecar.
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessBg", Cwd);
        var sub = Assert.Single(running);
        Assert.Equal("bg1", sub.AgentId);
        Assert.Equal("background work", sub.Description);
        Assert.Equal("general-purpose", sub.AgentType);
    }

    [Fact]
    public void GetRunning_EmptyWhenNoSubAgents()
    {
        var reader = new SubAgentReader();
        Assert.Empty(reader.GetRunning("sessA", Cwd));
    }
}
