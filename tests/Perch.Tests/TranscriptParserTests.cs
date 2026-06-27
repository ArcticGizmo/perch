using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TranscriptParserTests
{
    private static TranscriptParser Parse(string sessionId)
    {
        var path = TranscriptLocator.Resolve(sessionId, TestEnvironment.FixtureCwd);
        Assert.NotNull(path);
        var parser = new TranscriptParser(path!);
        parser.Ingest();
        return parser;
    }

    [Fact]
    public void Ingest_StitchesToolResultOntoItsCall()
    {
        var parser = Parse("sessA");

        var read = parser.Events.Single(e => e.Kind == HistoryEventKind.ToolCall && e.Summary == "Reading Foo.cs");
        Assert.Contains("namespace Foo", read.Result);

        var bash = parser.Events.Single(e => e.Kind == HistoryEventKind.ToolCall && e.Summary == "Running: npm test");
        Assert.Equal("ok", bash.Result);
    }

    [Fact]
    public void Ingest_FirstEventIsTheUserPrompt()
    {
        var parser = Parse("sessA");
        Assert.Equal(HistoryEventKind.UserText, parser.Events[0].Kind);
        Assert.Equal("Please add a feature", parser.Events[0].Summary);
    }

    [Fact]
    public void Ingest_ParsesThinkingSidechainAndImageBlocks()
    {
        var parser = Parse("sessParser");

        var thinking = parser.Events.Single(e => e.Kind == HistoryEventKind.Thinking);
        Assert.Equal("let me think", thinking.Detail);

        var sidechain = parser.Events.Single(e => e.Kind == HistoryEventKind.AssistantText && e.IsSidechain);
        Assert.Equal("sub-agent says hi", sidechain.Summary);

        var image = parser.Events.Single(e => e.Kind == HistoryEventKind.Image);
        Assert.Equal("image/png", image.ImageMedia);
        Assert.False(string.IsNullOrEmpty(image.ImageData));
    }

    [Fact]
    public void Ingest_IsIdempotent_NoNewEventsOnSecondCall()
    {
        var path = TranscriptLocator.Resolve("sessA", TestEnvironment.FixtureCwd);
        var parser = new TranscriptParser(path!);
        var first = parser.Ingest();
        int count = parser.Events.Count;
        var second = parser.Ingest();
        Assert.True(first.HasNew);
        Assert.False(second.HasNew);
        Assert.Equal(count, parser.Events.Count);
    }
}
