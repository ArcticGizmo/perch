using System.Text.Json.Nodes;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Covers <see cref="ProjectNoteCatalog.Enumerate"/>: recovering each project's real cwd from a transcript
/// head (the project-folder name is a lossy encoding), ordering most-recently-active first, and flagging
/// the projects that already carry a note. Each test seeds its own project directories under the fixture
/// <c>projects</c> tree and removes them again, so it never depends on (or dirties) the checked-in fixtures.
/// </summary>
public class ProjectNoteCatalogTests
{
    // Two seeded projects come back with their cwd recovered, newest-active first, and the one with a
    // project note is flagged and carries the note text.
    [Fact]
    public void Enumerate_RecoversCwd_OrdersByActivity_AndFlagsNotes()
    {
        var older = @"C:\fixtures\cat-older-" + Guid.NewGuid().ToString("N");
        var newer = @"C:\fixtures\cat-newer-" + Guid.NewGuid().ToString("N");
        using var monitor = new SessionMonitor();
        try
        {
            // `older` was active earlier and has a note; `newer` was active more recently and has none.
            SeedProject(older, DateTime.Now.AddHours(-3));
            SeedProject(newer, DateTime.Now.AddMinutes(-5));
            monitor.SetProjectNote(older, "  freeze main before the release  ");

            var all = ProjectNoteCatalog.Enumerate();

            var a = Assert.Single(all, p => p.Cwd == older);
            var b = Assert.Single(all, p => p.Cwd == newer);

            Assert.Equal(PathLeaf.Of(older), a.ProjectName);
            Assert.True(a.HasNote);
            Assert.Equal("freeze main before the release", a.Note);

            Assert.False(b.HasNote);
            Assert.Null(b.Note);

            // Newest-active first: `newer` precedes `older` in the returned order.
            Assert.True(all.ToList().IndexOf(b) < all.ToList().IndexOf(a));
        }
        finally
        {
            RemoveProject(older);
            RemoveProject(newer);
        }
    }

    // A project directory with a note but no transcript can't have its cwd recovered, so it's skipped
    // rather than surfaced with a bogus (undecodable) working directory.
    [Fact]
    public void Enumerate_SkipsProjectWithNoTranscript()
    {
        var cwd = @"C:\fixtures\cat-noxcript-" + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd));
        using var monitor = new SessionMonitor();
        try
        {
            Directory.CreateDirectory(dir);
            monitor.SetProjectNote(cwd, "orphan note");

            Assert.DoesNotContain(ProjectNoteCatalog.Enumerate(), p => p.Cwd == cwd);
        }
        finally
        {
            RemoveProject(cwd);
        }
    }

    private static void SeedProject(string cwd, DateTime lastActivity)
    {
        var dir = Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        var line = new JsonObject
        {
            ["type"] = "user",
            ["timestamp"] = "2026-01-01T00:00:00Z",
            ["cwd"] = cwd,
        };
        File.WriteAllText(transcript, line.ToJsonString() + "\n");
        File.SetLastWriteTime(transcript, lastActivity);
    }

    private static void RemoveProject(string cwd)
    {
        var dir = Path.Combine(ClaudePaths.ProjectsDir, TranscriptLocator.EncodeProjectDir(cwd));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
