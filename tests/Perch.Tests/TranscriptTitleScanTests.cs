using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>Efficient, append-only /rename detection: <see cref="TranscriptReader.ReadTitleFrom"/> reads the
/// last <c>custom-title</c> record in a byte range, so a listing can rescan only newly-appended records rather
/// than the whole transcript (docs — resume list showing renamed values).</summary>
public class TranscriptTitleScanTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perch-title-{Guid.NewGuid():N}.jsonl");

    private void Append(params string[] lines) => File.AppendAllText(_path, string.Concat(lines.Select(l => l + "\n")));
    private long Len() => new FileInfo(_path).Length;

    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    public void FindsLastCustomTitle_FromStart()
    {
        Append(
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"custom-title","customTitle":"First name"}""",
            """{"type":"assistant","message":{"content":[]}}""",
            """{"type":"custom-title","customTitle":"Renamed again"}""");
        Assert.Equal("Renamed again", TranscriptReader.ReadTitleFrom(_path, 0));
    }

    [Fact]
    public void ScanningOnlyAppendedBytes_CatchesANewRename_AndIgnoresOldOne()
    {
        Append("""{"type":"custom-title","customTitle":"Old name"}""");
        long afterRename = Len();

        // More activity, no rename → scanning only the new bytes finds nothing.
        Append("""{"type":"assistant","message":{"content":[]}}""");
        Assert.Null(TranscriptReader.ReadTitleFrom(_path, afterRename));

        // A fresh /rename appends → scanning from the previously-seen length catches it (no old-byte re-read).
        long beforeNewRename = Len();
        Append("""{"type":"custom-title","customTitle":"Brand new name"}""");
        Assert.Equal("Brand new name", TranscriptReader.ReadTitleFrom(_path, beforeNewRename));
    }

    [Fact]
    public void NeverRenamed_IsNull()
    {
        Append(
            """{"type":"user","message":{"role":"user","content":"hi"}}""",
            """{"type":"assistant","message":{"content":[]}}""");
        Assert.Null(TranscriptReader.ReadTitleFrom(_path, 0));
    }
}
