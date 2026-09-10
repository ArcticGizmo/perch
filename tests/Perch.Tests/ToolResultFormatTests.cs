using System.Text.Json.Nodes;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="ToolResultFormat"/>: the per-tool one-line summaries the collapsed session tool card
/// shows (counts for Read/Grep/Glob) and the full command it surfaces for Bash/PowerShell.
/// </summary>
public class ToolResultFormatTests
{
    private static JsonNode? In(string? s) => s is null ? null : JsonNode.Parse(s);
    private static string? Sum(string tool, string result, string? input = null)
        => ToolResultFormat.CollapsedSummary(tool, In(input), result);

    [Fact]
    public void Read_counts_lines()
    {
        Assert.Equal("Read 3 lines", Sum("Read", "1\ta\n2\tb\n3\tc"));
        Assert.Equal("Read 1 line", Sum("Read", "only one line"));
    }

    [Fact]
    public void Read_thousands_get_a_grouping_separator()
    {
        var body = string.Join("\n", System.Linq.Enumerable.Range(0, 1234));
        Assert.Equal("Read 1,234 lines", Sum("Read", body));
    }

    [Fact]
    public void Glob_counts_files_and_recognises_empty()
    {
        Assert.Equal("3 files", Sum("Glob", "a.cs\nb.cs\nc.cs"));
        Assert.Equal("1 file", Sum("Glob", "only.cs"));
        Assert.Equal("no files", Sum("Glob", "No files found"));
    }

    [Fact]
    public void Grep_labels_by_output_mode()
    {
        // Default (files_with_matches): one path per line → files.
        Assert.Equal("2 files", Sum("Grep", "a.cs\nb.cs", """{"pattern":"x"}"""));
        // content mode: matching lines.
        Assert.Equal("3 matching lines", Sum("Grep", "a.cs:1:x\na.cs:2:x\nb.cs:9:x", """{"pattern":"x","output_mode":"content"}"""));
        // count mode: sums the per-file counts.
        Assert.Equal("12 matches", Sum("Grep", "a.cs:5\nb.cs:7", """{"pattern":"x","output_mode":"count"}"""));
    }

    [Fact]
    public void Grep_no_matches()
    {
        Assert.Equal("no matches", Sum("Grep", "No matches found", """{"pattern":"zzz"}"""));
    }

    [Fact]
    public void Truncated_result_marks_the_count_as_a_floor()
    {
        Assert.Equal("Read 2+ lines", Sum("Read", "line a\nline b\n… (+900 more characters)"));
    }

    [Fact]
    public void Bash_and_unknown_tools_have_no_count_summary()
    {
        Assert.Null(Sum("Bash", "some output"));
        Assert.Null(Sum("SomeMcpTool", "x"));
        Assert.Null(Sum("Read", "   "));   // blank result → nothing to summarise
    }

    [Fact]
    public void Command_returns_full_shell_command_for_bash_like_tools_only()
    {
        Assert.Equal("dotnet test", ToolResultFormat.Command("Bash", In("""{"command":"dotnet test"}""")));
        Assert.Equal("Get-Process", ToolResultFormat.Command("PowerShell", In("""{"command":"Get-Process"}""")));
        Assert.Null(ToolResultFormat.Command("Read", In("""{"file_path":"x"}""")));
        Assert.Null(ToolResultFormat.Command("Bash", In("{}")));
    }
}
