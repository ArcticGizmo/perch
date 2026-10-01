using System.Text;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// <see cref="AtomicFile"/> is the CP14 write primitive (docs/review-fixes-plan.md): whole-file replacement through
/// a unique, flushed temp file and one rename, retried on a sharing violation. The lock cases hold a real handle,
/// so they're Windows-only (Unix locks are advisory and a rename succeeds regardless).
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-atomic-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P(string name = "f.json") => Path.Combine(_dir, name);
    private string[] Temps() => Directory.GetFiles(_dir, "*.tmp");
    private static readonly DateTime Old = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Write_creates_the_file_as_utf8_without_a_bom_and_leaves_no_temp()
    {
        AtomicFile.Write(P(), "{ \"a\": \"é\" }");

        var bytes = File.ReadAllBytes(P());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("{ \"a\": \"é\" }", Encoding.UTF8.GetString(bytes));
        Assert.Empty(Temps());
    }

    [Fact]
    public void Write_replaces_existing_content_and_creates_missing_directories()
    {
        var nested = Path.Combine(_dir, "a", "b", "f.json");
        AtomicFile.Write(nested, "one");
        AtomicFile.Write(nested, "two");
        Assert.Equal("two", File.ReadAllText(nested));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(nested)!, "*.tmp"));
    }

    [Fact]
    public void WriteIfChanged_skips_identical_content_without_touching_the_file()
    {
        File.WriteAllText(P(), "same");
        File.SetLastWriteTimeUtc(P(), Old);

        Assert.False(AtomicFile.WriteIfChanged(P(), "same"));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(P()));

        Assert.True(AtomicFile.WriteIfChanged(P(), "different"));
        Assert.Equal("different", File.ReadAllText(P()));
    }

    [Fact]
    public void Concurrent_writers_never_collide_on_a_temp_name_or_tear_the_file()
    {
        // The old AppSettings.Save shared one fixed ".tmp" name, so two saves at once could interleave.
        var payloads = Enumerable.Range(0, 16).Select(i => new string((char)('a' + i), 4096)).ToArray();
        // Losing the race is fine — Windows reports it as IOException or access denied — as long as nothing tears.
        Parallel.ForEach(payloads, p => { try { AtomicFile.Write(P(), p); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } });

        Assert.Contains(File.ReadAllText(P()), payloads);   // exactly one writer's whole content, never a mix
        Assert.Empty(Temps());
    }

    [Fact]
    public void Write_retries_while_another_process_briefly_holds_the_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(P(), "old");
        var holder = new FileStream(P(), FileMode.Open, FileAccess.Read, FileShare.Read);   // no FILE_SHARE_DELETE: blocks the rename
        // A dedicated thread, not the pool: under the parallel suite a pooled release could arrive after the
        // ~0.6s retry window and fail the test for reasons that have nothing to do with AtomicFile.
        var release = new Thread(() => { Thread.Sleep(100); holder.Dispose(); }) { IsBackground = true };
        release.Start();

        AtomicFile.Write(P(), "new");
        release.Join();

        Assert.Equal("new", File.ReadAllText(P()));
        Assert.Empty(Temps());
    }

    [Fact]
    public void Write_gives_up_on_a_held_target_without_touching_it_or_leaving_a_temp()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(P(), "keep me");
        using (new FileStream(P(), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => AtomicFile.Write(P(), "new"));

        Assert.Equal("keep me", File.ReadAllText(P()));
        Assert.Empty(Temps());
    }

    [Fact]
    public void TryRead_tells_missing_from_unreadable()
    {
        Assert.Equal(AtomicFile.ReadResult.Missing, AtomicFile.TryRead(P("nope.json"), out _));
        Assert.Equal(AtomicFile.ReadResult.Missing, AtomicFile.TryRead(Path.Combine(_dir, "no-dir", "x.json"), out _));

        File.WriteAllText(P(), "hi");
        Assert.Equal(AtomicFile.ReadResult.Ok, AtomicFile.TryRead(P(), out var text));
        Assert.Equal("hi", text);

        if (!OperatingSystem.IsWindows()) return;
        using (new FileStream(P(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(AtomicFile.ReadResult.Failed, AtomicFile.TryRead(P(), out _));
    }
}
