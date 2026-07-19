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

    // ----- Nesting: parent → sub-agent tree -------------------------------------------------
    //
    // sessNest's subagents/ holds two working agents that live flat in the one directory: a parent
    // (spawnDepth 1, toolUseId "toolu_parent") whose transcript launches an Agent tool_use with id
    // "toolu_child", and a child (spawnDepth 2, toolUseId "toolu_child"). The reader must reconstruct the
    // link from that tool_use ownership and return the child nested under the parent, not at the root.

    [Fact]
    public void GetRunning_NestedSubAgent_HangsUnderItsParentNotAtRoot()
    {
        StampFixtureAge(Path.Combine("sessNest", "subagents", "agent-anestpar.jsonl"), TimeSpan.Zero);
        StampFixtureAge(Path.Combine("sessNest", "subagents", "agent-anestkid.jsonl"), TimeSpan.Zero);
        var reader = new SubAgentReader();
        var roots = reader.GetRunning("sessNest", Cwd);

        // The parent is the session's only direct child; the sub-sub-agent hangs off it.
        var parent = Assert.Single(roots);
        Assert.Equal("anestpar", parent.AgentId);

        var child = Assert.Single(parent.Children);
        Assert.Equal("anestkid", child.AgentId);
        Assert.Equal("do a batch", child.Description);
        Assert.Empty(child.Children);
    }

    [Fact]
    public void GetRunning_NestedChild_WhoseParentHasGoneStale_FallsBackToRoot()
    {
        // If a nested child is still working but its parent's transcript has gone silent (stale, so the
        // parent drops off the transient roster), the orphaned child has no surfaced parent to hang under
        // and is returned at the root rather than vanishing with its parent.
        StampFixtureAge(Path.Combine("sessNest", "subagents", "agent-anestpar.jsonl"), TimeSpan.FromMinutes(10));
        StampFixtureAge(Path.Combine("sessNest", "subagents", "agent-anestkid.jsonl"), TimeSpan.Zero);
        var reader = new SubAgentReader();
        var roots = reader.GetRunning("sessNest", Cwd);

        var child = Assert.Single(roots);
        Assert.Equal("anestkid", child.AgentId);
    }

    // ----- Hook-driven turn markers (SubagentStop / TeammateIdle) -----------------------------
    //
    // The plugin drops agent-{id}.stopped / .idle beside an agent's transcript when Claude Code fires
    // the matching hook. The reader treats a marker as authoritative — provided it's at least as new as
    // the transcript — so a "working"-looking tail is retired immediately rather than after the 90s
    // staleness window. A later transcript write (a re-tasked teammate) ages the marker out.

    private static string FixtureSubagentPath(string session, string agentFile) => Path.Combine(
        TestEnvironment.FixtureConfigDir, "projects", "C--fixtures-proj", session, "subagents", agentFile);

    private static void WriteMarker(string session, string agentFile, string ext, TimeSpan age)
    {
        var marker = Path.ChangeExtension(FixtureSubagentPath(session, agentFile), null) + ext;
        File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - age);
    }

    private static void DeleteMarker(string session, string agentFile, string ext)
    {
        var marker = Path.ChangeExtension(FixtureSubagentPath(session, agentFile), null) + ext;
        if (File.Exists(marker))
            File.Delete(marker);
    }

    [Theory]
    [InlineData(".stopped")]
    [InlineData(".idle")]
    public void GetRunning_WorkingTeammate_WithFreshMarker_IsIdleNotStale(string ext)
    {
        // ux-explorer's tail is an unfinished tool_use (working). A marker newer than the transcript is
        // an explicit "turn ended" — the teammate goes idle at once, flagged clean (not stale) so the
        // monitor still treats it as a completion rather than an interrupt.
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"),
            TimeSpan.FromMinutes(1));
        WriteMarker("sessTeam", "agent-aux-explorer-1111.jsonl", ext, TimeSpan.Zero);
        try
        {
            var reader = new SubAgentReader();
            var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

            Assert.True(ux.IsTeammate);
            Assert.True(ux.IsIdle);
            Assert.False(ux.IsStale);
            Assert.Null(ux.Activity);
        }
        finally
        {
            DeleteMarker("sessTeam", "agent-aux-explorer-1111.jsonl", ext);
        }
    }

    [Fact]
    public void GetRunning_MarkerOlderThanTranscript_IsIgnored_RetaskedTeammateWorksAgain()
    {
        // A re-tasked teammate: the lead's new prompt appends to the transcript after the old .stopped
        // marker, so the marker is stale and the row flips straight back to working.
        WriteMarker("sessTeam", "agent-aux-explorer-1111.jsonl", ".stopped", TimeSpan.FromMinutes(5));
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-aux-explorer-1111.jsonl"),
            TimeSpan.Zero);
        try
        {
            var reader = new SubAgentReader();
            var ux = Assert.Single(reader.GetRunning("sessTeam", Cwd), s => s.Name == "ux-explorer");

            Assert.False(ux.IsIdle);
            Assert.False(ux.IsStale);
            Assert.Equal("Searching: OverlayForm", ux.Activity);
        }
        finally
        {
            DeleteMarker("sessTeam", "agent-aux-explorer-1111.jsonl", ".stopped");
        }
    }

    [Fact]
    public void GetRunning_WorkingPlainSubAgent_WithFreshStopMarker_IsDropped()
    {
        // A transient sub-agent with an explicit stop drops off the roster immediately, the same as one
        // that finished cleanly — no waiting for the staleness window.
        StampFixtureAge(Path.Combine("sessTeam", "subagents", "agent-plainwork3333.jsonl"),
            TimeSpan.FromMinutes(1));
        WriteMarker("sessTeam", "agent-plainwork3333.jsonl", ".stopped", TimeSpan.Zero);
        try
        {
            var reader = new SubAgentReader();
            Assert.DoesNotContain(reader.GetRunning("sessTeam", Cwd), s => s.AgentId == "plainwork3333");
        }
        finally
        {
            DeleteMarker("sessTeam", "agent-plainwork3333.jsonl", ".stopped");
        }
    }
}
