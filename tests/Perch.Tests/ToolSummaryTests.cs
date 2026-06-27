using Perch.Data;
using System.Text.Json.Nodes;
using Xunit;

namespace Perch.Tests;

public class ToolSummaryTests
{
    private static JsonNode? Input(string json) => JsonNode.Parse(json);

    [Fact]
    public void Describe_Read_UsesFileName() =>
        Assert.Equal("Reading Foo.cs", ToolSummary.Describe("Read", Input("{\"file_path\":\"C:\\\\a\\\\Foo.cs\"}")));

    [Fact]
    public void Describe_Edit_UsesFileName() =>
        Assert.Equal("Editing Bar.cs", ToolSummary.Describe("Edit", Input("{\"file_path\":\"x/Bar.cs\"}")));

    [Fact]
    public void Describe_Bash_UsesCommand() =>
        Assert.Equal("Running: npm test", ToolSummary.Describe("Bash", Input("{\"command\":\"npm test\"}")));

    [Fact]
    public void Describe_Grep_UsesPattern() =>
        Assert.Equal("Searching: foo", ToolSummary.Describe("Grep", Input("{\"pattern\":\"foo\"}")));

    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void Describe_SubAgent_UsesDescription(string tool) =>
        Assert.Equal("Delegating: explore", ToolSummary.Describe(tool, Input("{\"description\":\"explore\"}")));

    [Fact]
    public void Describe_TodoWrite_IsFixedPhrase() =>
        Assert.Equal("Updating todos", ToolSummary.Describe("TodoWrite", null));

    [Fact]
    public void Describe_UnknownTool_FallsBackToName() =>
        Assert.Equal("SomethingNew", ToolSummary.Describe("SomethingNew", null));

    [Fact]
    public void FileLabel_TakesFileName_OrFallsBack()
    {
        Assert.Equal("Foo.cs", ToolSummary.FileLabel("C:\\a\\b\\Foo.cs"));
        Assert.Equal("file", ToolSummary.FileLabel(null));
        Assert.Equal("file", ToolSummary.FileLabel("   "));
    }

    [Fact]
    public void Clip_TruncatesLongStringsWithEllipsis()
    {
        var clipped = ToolSummary.Clip(new string('x', 200));
        Assert.EndsWith("…", clipped);
        Assert.True(clipped.Length <= 61, $"expected <=61 chars, got {clipped.Length}");
        Assert.Equal("short", ToolSummary.Clip("short"));
    }
}
