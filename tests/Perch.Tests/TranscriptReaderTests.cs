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
}
