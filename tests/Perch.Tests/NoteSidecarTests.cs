using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers the pinned-note sidecar: <see cref="SessionMonitor.SetNote"/> (write/clear the
/// <c>{sessionId}.note</c> file) and <see cref="SessionMonitor.ReadNote"/> (parse it back). The full
/// scan can't run against fixtures (it gates every session on a live OS process), so the note
/// read/write is exercised directly — the same isolation-of-a-pure-helper approach as
/// <see cref="SessionStatusTests"/>.
/// </summary>
public class NoteSidecarTests
{
    // A note written by SetNote reads back verbatim (trimmed) through ReadNote — the round trip the
    // overlay + editor rely on.
    [Fact]
    public void SetNote_ThenRead_RoundTrips()
    {
        var sid = "note-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ClaudePaths.SessionsDir, sid + ".note");
        using var monitor = new SessionMonitor();
        try
        {
            monitor.SetNote(sid, "  risky refactor — waiting on review  ");
            Assert.Equal("risky refactor — waiting on review", SessionMonitor.ReadNote(path));

            // It's the canonical JSON payload, not bare text, so pin/colour can be layered on later.
            var obj = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
            Assert.Equal("risky refactor — waiting on review", (string?)obj["text"]);
            Assert.NotNull(obj["updatedAt"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Clearing a note (null or blank) deletes the sidecar, so the row drops its glyph + second line.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetNote_WithBlank_ClearsTheSidecar(string? blank)
    {
        var sid = "note-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ClaudePaths.SessionsDir, sid + ".note");
        using var monitor = new SessionMonitor();
        try
        {
            monitor.SetNote(sid, "temporary");
            Assert.True(File.Exists(path));

            monitor.SetNote(sid, blank);
            Assert.False(File.Exists(path));
            Assert.Null(SessionMonitor.ReadNote(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // The scratch pad is multi-line: newlines survive the JSON round trip (only outer whitespace is
    // trimmed), so a pad written over several lines reads back with its line breaks intact.
    [Fact]
    public void SetNote_MultiLine_RoundTripsPreservingNewlines()
    {
        var sid = "note-test-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(ClaudePaths.SessionsDir, sid + ".note");
        using var monitor = new SessionMonitor();
        try
        {
            const string text = "line one\nline two\n\n- a todo\n- another";
            monitor.SetNote(sid, text);
            Assert.Equal(text, SessionMonitor.ReadNote(path));

            var obj = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
            Assert.Equal(text, (string?)obj["text"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

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
    // reads back verbatim (trimmed) — the round trip the row-note editor's project section relies on. It's
    // shared by every session with that cwd, which is why it's keyed by cwd rather than session id.
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
