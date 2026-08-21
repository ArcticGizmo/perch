using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the project-note sidecar: <see cref="SessionMonitor.SetProjectNote"/> (write/clear the
/// <c>project.note</c> file) and <see cref="SessionMonitor.ReadNote"/> (parse it back). The full
/// scan can't run against fixtures (it gates every session on a live OS process), so the note
/// read/write is exercised directly — the same isolation-of-a-pure-helper approach as
/// <see cref="SessionStatusTests"/>.
/// </summary>
public class NoteSidecarTests
{
    [Fact]
    public void ReadNote_MissingFile_IsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".note");
        Assert.Null(SessionMonitor.ReadNote(path));
    }

    [Theory]
    [InlineData("")]            // empty file
    [InlineData("   \n ")]      // whitespace only
    [InlineData("{}")]          // JSON object with no text field
    [InlineData("{\"text\":\"\"}")] // JSON with an empty text field
    [InlineData("{\"text\":\"   \"}")] // JSON with a blank text field
    public void ReadNote_BlankOrEmptyText_IsNull(string content)
    {
        var path = WriteTemp(content);
        try { Assert.Null(SessionMonitor.ReadNote(path)); }
        finally { File.Delete(path); }
    }

    // A hand-edited plain-text file (not JSON) is tolerated as a fallback so a note dropped in by hand
    // still shows, rather than being silently ignored because it isn't the canonical JSON shape.
    [Fact]
    public void ReadNote_PlainTextFallback_ReturnsTrimmedText()
    {
        var path = WriteTemp("  just some plain text  ");
        try { Assert.Equal("just some plain text", SessionMonitor.ReadNote(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadNote_JsonPayload_ReturnsText()
    {
        var path = WriteTemp("{\"text\":\"waiting on ops\",\"pinned\":true,\"updatedAt\":\"2026-07-18T09:14:00Z\"}");
        try { Assert.Equal("waiting on ops", SessionMonitor.ReadNote(path)); }
        finally { File.Delete(path); }
    }

    // A project note is written to a project.note sidecar in the cwd's encoded transcript directory and
    // reads back verbatim (trimmed) — the round trip the note editor relies on. It's shared by every
    // session with that cwd, which is why it's keyed by cwd rather than session id.
    [Fact]
    public void SetProjectNote_ThenRead_RoundTrips()
    {
        var cwd = @"C:\fixtures\note-proj-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd), "project.note");
        using var monitor = new SessionMonitor();
        try
        {
            monitor.SetProjectNote(cwd, "  shared: freeze main before the release  ");
            Assert.Equal("shared: freeze main before the release", SessionMonitor.ReadProjectNote(cwd));
            Assert.True(File.Exists(path));

            // Multi-line notes survive the JSON round trip (only outer whitespace is trimmed).
            const string multi = "line one\nline two\n\n- a todo\n- another";
            monitor.SetProjectNote(cwd, multi);
            Assert.Equal(multi, SessionMonitor.ReadProjectNote(cwd));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetProjectNote_WithBlank_ClearsTheSidecar(string? blank)
    {
        var cwd = @"C:\fixtures\note-proj-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd), "project.note");
        using var monitor = new SessionMonitor();
        try
        {
            monitor.SetProjectNote(cwd, "temporary");
            Assert.True(File.Exists(path));

            monitor.SetProjectNote(cwd, blank);
            Assert.False(File.Exists(path));
            Assert.Null(SessionMonitor.ReadProjectNote(cwd));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // A blank cwd has no project to key against, so the read is null and the write is a harmless no-op.
    [Fact]
    public void ProjectNote_WithBlankCwd_IsNullAndNoOp()
    {
        using var monitor = new SessionMonitor();
        monitor.SetProjectNote("", "ignored"); // must not throw
        Assert.Null(SessionMonitor.ReadProjectNote(""));
    }

    private static string WriteTemp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".note");
        File.WriteAllText(path, content);
        return path;
    }
}
