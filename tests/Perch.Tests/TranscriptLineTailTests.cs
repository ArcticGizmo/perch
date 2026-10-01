using System.Text;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Review fixes CP24: the history viewer's live tail reads only what was appended since the last read (it used to
/// re-read and re-split the whole transcript on every change), and reports a reset when the file was truncated or
/// replaced so the viewer rebuilds.
/// </summary>
public sealed class TranscriptLineTailTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "perch-tail-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string P => Path.Combine(_dir, "t.jsonl");

    private static void Append(string path, string text)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var b = Encoding.UTF8.GetBytes(text);
        fs.Write(b, 0, b.Length);
    }

    private static string Rec(int i) => $$"""{"n":{{i}}}""";

    [Fact]
    public void First_read_is_every_complete_line_then_only_appends()
    {
        File.WriteAllText(P, Rec(1) + "\n" + Rec(2) + "\n");
        var tail = new TranscriptLineTail();

        var first = tail.Read(P);
        Assert.NotNull(first);
        Assert.False(first.Value.Reset);
        Assert.Equal([Rec(1), Rec(2)], first.Value.Lines);

        Assert.Empty(tail.Read(P)!.Value.Lines);   // unchanged

        Append(P, Rec(3) + "\n");
        var next = tail.Read(P)!.Value;
        Assert.False(next.Reset);
        Assert.Equal([Rec(3)], next.Lines);
    }

    [Fact]
    public void A_partial_line_waits_for_its_newline()
    {
        File.WriteAllText(P, Rec(1) + "\n");
        var tail = new TranscriptLineTail();
        tail.Read(P);

        Append(P, "{\"n\":");
        Assert.Empty(tail.Read(P)!.Value.Lines);
        Append(P, "2}\n");
        Assert.Equal([Rec(2)], tail.Read(P)!.Value.Lines);
    }

    [Fact]
    public void An_append_reads_only_the_new_bytes()
    {
        var big = string.Concat(Enumerable.Range(0, 20_000).Select(i => Rec(i) + "\n"));
        File.WriteAllText(P, big);
        var tail = new TranscriptLineTail();
        tail.Read(P);
        long before = tail.BytesRead;

        var add = Rec(99_999) + "\n";
        Append(P, add);
        Assert.Equal([Rec(99_999)], tail.Read(P)!.Value.Lines);
        // The append plus the 256-byte head check that spots a replaced file, not the ~200 KB file again.
        Assert.True(tail.BytesRead - before <= add.Length + 256, $"read {tail.BytesRead - before} bytes");
    }

    [Fact]
    public void Truncation_and_replacement_reset_with_the_whole_file()
    {
        File.WriteAllText(P, Rec(1) + "\n" + Rec(2) + "\n" + Rec(3) + "\n");
        var tail = new TranscriptLineTail();
        tail.Read(P);

        File.WriteAllText(P, Rec(7) + "\n");   // shorter: truncated
        var shrunk = tail.Read(P)!.Value;
        Assert.True(shrunk.Reset);
        Assert.Equal([Rec(7)], shrunk.Lines);

        File.WriteAllText(P, Rec(8) + "\n" + Rec(9) + "\n");   // longer, but a different head: replaced
        var replaced = tail.Read(P)!.Value;
        Assert.True(replaced.Reset);
        Assert.Equal([Rec(8), Rec(9)], replaced.Lines);

        Append(P, Rec(10) + "\n");
        var after = tail.Read(P)!.Value;
        Assert.False(after.Reset);
        Assert.Equal([Rec(10)], after.Lines);
    }

    [Fact]
    public void A_missing_file_reads_as_null()
    {
        Assert.Null(new TranscriptLineTail().Read(Path.Combine(_dir, "nope.jsonl")));
    }
}
