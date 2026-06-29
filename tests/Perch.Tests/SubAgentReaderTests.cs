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

    // sessTeam's subagents/ holds four agents: a working teammate (ux-explorer, tail = tool_use), an
    // idle teammate (devils-advocate, tail = finished assistant turn), a working plain sub-agent
    // (Explore, tail = tool_use) and an idle plain sub-agent (Explore, finished). Teammates are
    // persistent so both show regardless of idle/working; the plain sub-agent only shows while working.

    [Fact]
    public void GetRunning_Teammates_AreFlaggedWithNameTeamAndColour()
    {
        var reader = new SubAgentReader();
        var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

        Assert.True(ux.IsTeammate);
        Assert.Equal("ux-explorer", ux.AgentType);
        Assert.Equal("UX analysis of Perch", ux.Description);
        Assert.Equal("session-team", ux.TeamName);
        Assert.Equal("blue", ux.Color);
        Assert.Equal("aux-explorer-1111", ux.AgentId);
    }

    [Fact]
    public void GetRunning_WorkingTeammate_IsNotIdleAndCarriesActivity()
    {
        var reader = new SubAgentReader();
        var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

        Assert.False(ux.IsIdle);
        Assert.Equal("Searching: OverlayForm", ux.Activity);
    }

    [Fact]
    public void GetRunning_IdleTeammate_StaysOnRosterMarkedIdleWithNoActivity()
    {
        // The idle teammate's transcript ends on a finished assistant turn — it must still be surfaced
        // (teammates persist) but flagged idle, so SessionMonitor's "any working sub" gate skips it.
        var reader = new SubAgentReader();
        var devil = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "devils-advocate");

        Assert.True(devil.IsTeammate);
        Assert.True(devil.IsIdle);
        Assert.Null(devil.Activity);
        Assert.Equal("yellow", devil.Color);
    }

    [Fact]
    public void GetRunning_PlainSubAgent_ShownOnlyWhileWorking()
    {
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessTeam", Cwd);

        // The working Explore sub-agent is present, not flagged as a teammate.
        var work = Assert.Single(running, s => s.AgentId == "plainwork3333");
        Assert.False(work.IsTeammate);
        Assert.Equal("Explore", work.AgentType);
        Assert.Equal("explore code", work.Description);

        // The idle Explore sub-agent is dropped entirely (transient lifecycle).
        Assert.DoesNotContain(running, s => s.AgentId == "plainidle4444");
    }

    [Fact]
    public void GetRunning_Team_SurfacesBothTeammatesPlusWorkingSubAgentOnly()
    {
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessTeam", Cwd);

        // Two teammates (idle + working) + one working plain sub-agent = 3; the idle plain one is gone.
        Assert.Equal(3, running.Count);
        Assert.Equal(2, running.Count(s => s.IsTeammate));
    }
}
