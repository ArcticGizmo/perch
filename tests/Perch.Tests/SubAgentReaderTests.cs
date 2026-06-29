using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class SubAgentReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    // The staleness guard treats a "working" agent whose transcript has gone silent as quiesced, keyed
    // on the file's last-write time. The deployed fixtures carry their copy-time mtime, so a test that
    // asserts a *working* sub-agent first stamps its transcript fresh, and a staleness test stamps it
    // old. The path is relative to the fixture project dir (projects/C--fixtures-proj).
    private static void StampFixtureAge(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(
            TestEnvironment.FixtureConfigDir, "projects", "C--fixtures-proj", relativePath);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

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
        StampFixtureAge(Path.Combine("sessBg", "subagents", "agent-bg1.jsonl"), TimeSpan.Zero);
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
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"), TimeSpan.Zero);
        var reader = new SubAgentReader();
        var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

        Assert.False(ux.IsIdle);
        Assert.False(ux.IsStale);
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
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-plainwork3333.jsonl"), TimeSpan.Zero);
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
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"), TimeSpan.Zero);
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-plainwork3333.jsonl"), TimeSpan.Zero);
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessTeam", Cwd);

        // Two teammates (idle + working) + one working plain sub-agent = 3; the idle plain one is gone.
        Assert.Equal(3, running.Count);
        Assert.Equal(2, running.Count(s => s.IsTeammate));
    }

    [Fact]
    public void GetRunning_TeamsDisabled_TreatsTeammatesAsOrdinarySubAgents()
    {
        // With the experimental teams switch off, teammates lose their distinct lifecycle: a working
        // teammate is surfaced as a plain sub-agent (no IsTeammate/name/colour) and an idle teammate is
        // dropped entirely, exactly like an ordinary transient sub-agent.
        SubAgentReader.TeamsEnabled = false;
        try
        {
            StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"), TimeSpan.Zero);
            StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-plainwork3333.jsonl"), TimeSpan.Zero);
            var reader = new SubAgentReader();
            var running = reader.GetRunning("sessTeam", Cwd);

            // No teammate rows at all; the idle teammate is gone, the working one is now plain.
            Assert.DoesNotContain(running, s => s.IsTeammate);
            Assert.DoesNotContain(running, s => s.Name == "devils-advocate");

            var ux = Assert.Single(running, s => s.AgentId == "aux-explorer-1111");
            Assert.False(ux.IsTeammate);
            Assert.Null(ux.Name);

            // The two working agents remain (teammate-as-plain + plain sub-agent); both idle ones dropped.
            Assert.Equal(2, running.Count);
        }
        finally
        {
            SubAgentReader.TeamsEnabled = true;   // restore the test-run default (see TestEnvironment)
        }
    }

    [Fact]
    public void GetRunning_StaleWorkingTeammate_IsDemotedToIdleButStaysOnRoster()
    {
        // A teammate left frozen mid-turn by an interrupt keeps a "working" tail forever. Once its
        // transcript has gone silent past the staleness window it must be surfaced as idle (so it stops
        // pegging the parent as Running) yet remain on the roster — and be flagged stale so the monitor
        // can tell this interrupt from a clean completion.
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"),
            TimeSpan.FromMinutes(10));
        var reader = new SubAgentReader();
        var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

        Assert.True(ux.IsTeammate);
        Assert.True(ux.IsIdle);
        Assert.True(ux.IsStale);
        Assert.Null(ux.Activity);
    }

    [Fact]
    public void GetRunning_StalePlainSubAgent_IsDropped()
    {
        // A transient sub-agent frozen mid-turn by an interrupt drops off the roster once stale, the
        // same as one that finished cleanly.
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-plainwork3333.jsonl"),
            TimeSpan.FromMinutes(10));
        var reader = new SubAgentReader();
        var running = reader.GetRunning("sessTeam", Cwd);

        Assert.DoesNotContain(running, s => s.AgentId == "plainwork3333");
    }

    [Fact]
    public void GetRunning_StalenessWindowIsConfigurable()
    {
        // A transcript silent for 2 minutes is working under a 5-minute window, stale under a 1-minute one.
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"),
            TimeSpan.FromMinutes(2));

        var lenient = new SubAgentReader(TimeSpan.FromMinutes(5));
        Assert.False(Assert.Single(lenient.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer").IsStale);

        var strict = new SubAgentReader(TimeSpan.FromMinutes(1));
        Assert.True(Assert.Single(strict.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer").IsStale);
    }
}
