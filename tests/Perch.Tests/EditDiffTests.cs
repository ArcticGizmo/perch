using System.Text.Json.Nodes;
using Perch.Data;
using Perch.Data.Control;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="EditDiff"/>: turning an Edit/MultiEdit/Write tool input into a unified line diff for the
/// session UI's terminal-style diff view.
/// </summary>
public class EditDiffTests
{
    private static JsonNode Json(string s) => JsonNode.Parse(s)!;

    [Fact]
    public void Edit_produces_context_removed_added()
    {
        var diff = EditDiff.Build("Edit", Json("""{"old_string":"a\nb\nc","new_string":"a\nB\nc"}"""));
        Assert.NotNull(diff);
        Assert.Collection(diff!,
            l => Assert.Equal((GitDiffLineKind.Context, "a"), (l.Kind, l.Text)),
            l => Assert.Equal((GitDiffLineKind.Removed, "b"), (l.Kind, l.Text)),
            l => Assert.Equal((GitDiffLineKind.Added, "B"), (l.Kind, l.Text)),
            l => Assert.Equal((GitDiffLineKind.Context, "c"), (l.Kind, l.Text)));
        Assert.Equal((1, 1), EditDiff.Counts(diff!));
    }

    [Fact]
    public void Edit_pure_insertion_adds_lines_keeping_context()
    {
        // Mirrors the CHANGELOG case: one line added into an existing block.
        var diff = EditDiff.Build("Edit", Json("""{"old_string":"one\ntwo","new_string":"one\nadded\ntwo"}"""));
        Assert.NotNull(diff);
        Assert.Equal((1, 0), EditDiff.Counts(diff!));
        Assert.Contains(diff!, l => l.Kind == GitDiffLineKind.Added && l.Text == "added");
    }

    [Fact]
    public void Edit_that_changes_nothing_is_null()
    {
        Assert.Null(EditDiff.Build("Edit", Json("""{"old_string":"same","new_string":"same"}""")));
    }

    [Fact]
    public void Write_shows_content_as_all_added()
    {
        var diff = EditDiff.Build("Write", Json("""{"file_path":"x.txt","content":"line1\nline2"}"""));
        Assert.NotNull(diff);
        Assert.All(diff!, l => Assert.Equal(GitDiffLineKind.Added, l.Kind));
        Assert.Equal((2, 0), EditDiff.Counts(diff!));
    }

    [Fact]
    public void Trailing_newline_does_not_add_a_phantom_blank_line()
    {
        var diff = EditDiff.Build("Write", Json("""{"content":"only\n"}"""));
        Assert.NotNull(diff);
        var line = Assert.Single(diff!);
        Assert.Equal("only", line.Text);
    }

    [Fact]
    public void MultiEdit_walks_each_edit_with_a_separator()
    {
        var diff = EditDiff.Build("MultiEdit", Json("""
            {"edits":[
              {"old_string":"a","new_string":"A"},
              {"old_string":"b","new_string":"B"}
            ]}
            """));
        Assert.NotNull(diff);
        Assert.Equal((2, 2), EditDiff.Counts(diff!));
        Assert.Contains(diff!, l => l.Kind == GitDiffLineKind.Meta);   // a divider between the two edits
    }

    [Fact]
    public void Non_edit_tool_and_missing_input_are_null()
    {
        Assert.Null(EditDiff.Build("Bash", Json("""{"command":"ls"}""")));
        Assert.Null(EditDiff.Build("Edit", null));
        Assert.False(EditDiff.IsEditTool("Bash"));
        Assert.True(EditDiff.IsEditTool("MultiEdit"));
    }

    [Fact]
    public void Crlf_is_normalised_so_line_endings_do_not_show_as_diffs()
    {
        var diff = EditDiff.Build("Edit", Json("""{"old_string":"a\r\nb","new_string":"a\nb"}"""));
        Assert.Null(diff);   // identical content once newlines are normalised → nothing to show
    }
}
