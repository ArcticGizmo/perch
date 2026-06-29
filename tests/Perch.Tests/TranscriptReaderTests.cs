using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptReaderTests
{
    private const string Cwd = TestEnvironment.FixtureCwd;

    [Fact]
    public void GetActivity_ReturnsMostRecentToolCallPhrase()
    {
        var reader = new TranscriptReader();
        // The last tool_use in sessA is the Bash "npm test" call; later records carry no tool_use.
        Assert.Equal("Running: npm test", reader.GetActivity("sessA", Cwd));
    }

    [Fact]
    public void GetTitle_ReturnsCustomRenameTitle()
    {
        var reader = new TranscriptReader();
        Assert.Equal("Feature work", reader.GetTitle("sessA", Cwd));
    }

    [Fact]
    public void GetTitle_NullWhenNeverRenamed()
    {
        var reader = new TranscriptReader();
        Assert.Null(reader.GetTitle("sessB", Cwd));
    }

    [Fact]
    public void GetContextFill_UsesModelSwitchWindowAndLatestUsage()
    {
        var reader = new TranscriptReader();
        var (fill, window) = reader.GetContextFill("sessA", Cwd);
        Assert.Equal(ModelContext.ExtendedWindow, window);         // "(1M context)" model switch
        Assert.NotNull(fill);
        // latest usage = input 50000 + cache_read 150000 = 200000 over a 1,000,000 window.
        Assert.Equal(0.2, (double)fill!.Value, 3);
    }

    [Fact]
    public void LastTurnWasBareCommand_TrueWhenLastTurnIsASlashCommand()
    {
        var reader = new TranscriptReader();
        Assert.True(reader.LastTurnWasBareCommand("sessCmd", Cwd));
    }

    [Fact]
    public void LastTurnWasBareCommand_FalseWhenLastTurnIsModelWork()
    {
        var reader = new TranscriptReader();
        Assert.False(reader.LastTurnWasBareCommand("sessA", Cwd));
    }

    [Fact]
    public void GetArtifacts_ReturnsPublishedArtifact()
    {
        var reader = new TranscriptReader();
        var artifacts = reader.GetArtifacts("sessA", Cwd);
        var artifact = Assert.Single(artifacts);
        Assert.Equal("Sales Chart", artifact.Title);
        Assert.Contains("/code/artifact/abc123", artifact.Url);
    }

    [Fact]
    public void GetArtifacts_EmptyWhenNonePublished()
    {
        var reader = new TranscriptReader();
        Assert.Empty(reader.GetArtifacts("sessB", Cwd));
    }

    [Fact]
    public void GetTasks_ReconstructsListInCreationOrderWithLatestStatus()
    {
        var reader = new TranscriptReader();
        var tasks = reader.GetTasks("sessTasks", Cwd);

        Assert.Equal(3, tasks.Count);

        // Creation order is the id order; the latest TaskUpdate per id wins.
        Assert.Equal("Phase 0 — Scaffold", tasks[0].Subject);
        Assert.Equal(TaskState.Completed, tasks[0].State);

        Assert.Equal("Phase 1 — Slash commands", tasks[1].Subject);
        Assert.Equal("Building slash commands", tasks[1].ActiveForm);
        Assert.Equal(TaskState.InProgress, tasks[1].State);

        // Never updated → stays pending. (Records a malformed trailing line + an out-of-range
        // taskId, both of which must be tolerated without affecting the result.)
        Assert.Equal(TaskState.Pending, tasks[2].State);
    }

    [Fact]
    public void GetTasks_CompletedCountAndCurrentTaskReflectProgress()
    {
        var reader = new TranscriptReader();
        var session = new ClaudeSession(
            "1", "sessTasks", SessionStatus.Running, Cwd, "proj", DateTime.Now,
            Tasks: reader.GetTasks("sessTasks", Cwd));

        Assert.Equal(1, session.CompletedTaskCount);
        Assert.Equal(3, session.Tasks.Count);
        Assert.NotNull(session.CurrentTask);
        Assert.Equal("Building slash commands", session.CurrentTask!.ActiveForm);
    }

    [Fact]
    public void GetTasks_EmptyWhenSessionHasNoTasks()
    {
        var reader = new TranscriptReader();
        Assert.Empty(reader.GetTasks("sessB", Cwd));
    }
}
