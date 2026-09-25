using System.Text;
using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// Guards <see cref="TranscriptTailReader"/> against the ways a live transcript actually grows: whole-line
/// appends, a record split across two writes (including mid-UTF-8-character), truncation, replacement with a
/// different file, a still-missing file, and the initial "last N lines" seek.
/// </summary>
public sealed class TranscriptTailReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("perch-tail-").FullName;
    private readonly string _path;

    public TranscriptTailReaderTests() => _path = Path.Combine(_dir, "session.jsonl");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Append(string text)
    {
        using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(text);
        fs.Write(bytes);
    }

    private void AppendBytes(byte[] bytes)
    {
        using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        fs.Write(bytes);
    }

    [Fact]
    public void FirstReadReturnsEveryCompleteLine()
    {
        Append("{\"a\":1}\n{\"b\":2}\n");
        var r = new TranscriptTailReader(_path).Read();
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], r.Lines);
        Assert.False(r.Reset);
        Assert.False(r.SkippedEarlier);
    }

    [Fact]
    public void LaterReadsReturnOnlyWhatWasAppended()
    {
        Append("one\ntwo\n");
        var tail = new TranscriptTailReader(_path);
        tail.Read();

        Assert.Empty(tail.Read().Lines);          // nothing new
        Append("three\n");
        Assert.Equal(["three"], tail.Read().Lines);
        Append("four\nfive\n");
        Assert.Equal(["four", "five"], tail.Read().Lines);
    }

    [Fact]
    public void PartialTrailingLineWaitsUntilItsNewlineArrives()
    {
        Append("one\n{\"half\":");
        var tail = new TranscriptTailReader(_path);
        Assert.Equal(["one"], tail.Read().Lines);
        Assert.Empty(tail.Read().Lines);            // still half-written
        Append("true}\n");
        Assert.Equal(["{\"half\":true}"], tail.Read().Lines);
    }

    [Fact]
    public void RecordSplitMidMultibyteCharacterDecodesWhole()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"t\":\"café — 🐦\"}\n");
        int split = Array.IndexOf(bytes, (byte)0xF0) + 2;   // inside the 4-byte bird emoji
        var tail = new TranscriptTailReader(_path);

        AppendBytes(bytes[..split]);
        Assert.Empty(tail.Read().Lines);
        AppendBytes(bytes[split..]);
        Assert.Equal(["{\"t\":\"café — 🐦\"}"], tail.Read().Lines);
    }

    [Fact]
    public void CrLfAndBlankLinesAreNormalised()
    {
        Append("one\r\n\r\n   \ntwo\r\n");
        Assert.Equal(["one", "two"], new TranscriptTailReader(_path).Read().Lines);
    }

    [Fact]
    public void LeadingBomIsSkipped()
    {
        AppendBytes([0xEF, 0xBB, 0xBF]);
        Append("{\"a\":1}\n");
        Assert.Equal(["{\"a\":1}"], new TranscriptTailReader(_path).Read().Lines);
    }

    [Fact]
    public void TruncationResetsToTheTop()
    {
        Append("one\ntwo\nthree\n");
        var tail = new TranscriptTailReader(_path);
        tail.Read();

        File.WriteAllText(_path, "fresh\n");
        var r = tail.Read();
        Assert.True(r.Reset);
        Assert.Equal(["fresh"], r.Lines);

        Append("next\n");
        var after = tail.Read();
        Assert.False(after.Reset);
        Assert.Equal(["next"], after.Lines);
    }

    [Fact]
    public void ReplacementWithALongerDifferentFileResets()
    {
        Append("one\ntwo\n");
        var tail = new TranscriptTailReader(_path);
        tail.Read();

        File.WriteAllText(_path, "XXXXXXXX\nyyyyyyyy\nzzz\n");   // longer, but the bytes before the offset differ
        var r = tail.Read();
        Assert.True(r.Reset);
        Assert.Equal(["XXXXXXXX", "yyyyyyyy", "zzz"], r.Lines);
    }

    [Fact]
    public void MissingFileReadsAsNothingThenPicksUpOnceCreated()
    {
        var tail = new TranscriptTailReader(_path);
        Assert.Empty(tail.Read().Lines);
        Append("hello\n");
        var r = tail.Read();
        Assert.Equal(["hello"], r.Lines);
        Assert.False(r.Reset);
    }

    [Fact]
    public void InitialSeekReturnsOnlyTheLastNLines()
    {
        Append(string.Concat(Enumerable.Range(1, 10).Select(i => $"line{i}\n")) + "partial");
        var tail = new TranscriptTailReader(_path, initialLines: 3);
        var r = tail.Read();
        Assert.Equal(["line8", "line9", "line10"], r.Lines);
        Assert.True(r.SkippedEarlier);

        Append("-done\n");
        var next = tail.Read();
        Assert.Equal(["partial-done"], next.Lines);
        Assert.False(next.SkippedEarlier);
    }

    [Fact]
    public void InitialSeekOnAShortFileReturnsEverything()
    {
        Append("a\nb\n");
        var r = new TranscriptTailReader(_path, initialLines: 5).Read();
        Assert.Equal(["a", "b"], r.Lines);
        Assert.False(r.SkippedEarlier);
    }

    [Fact]
    public void InitialSeekSpansManyBackScanChunks()
    {
        // ~200KB of lines forces the backwards scan across several 64KB chunks.
        var sb = new StringBuilder();
        for (int i = 0; i < 4000; i++) sb.Append($"{{\"n\":{i},\"pad\":\"{new string('x', 40)}\"}}\n");
        Append(sb.ToString());

        var r = new TranscriptTailReader(_path, initialLines: 600).Read();
        Assert.Equal(600, r.Lines.Count);
        Assert.StartsWith("{\"n\":3400,", r.Lines[0]);
        Assert.StartsWith("{\"n\":3999,", r.Lines[^1]);
        Assert.True(r.SkippedEarlier);
    }

    [Fact]
    public void ResetReappliesTheInitialSeek()
    {
        Append("a\nb\nc\n");
        var tail = new TranscriptTailReader(_path, initialLines: 2);
        Assert.Equal(["b", "c"], tail.Read().Lines);

        File.WriteAllText(_path, "1\n2\n3\n4\n5\n6\n");   // replaced with a longer, different file
        var r = tail.Read();
        Assert.True(r.Reset);
        Assert.True(r.SkippedEarlier);
        Assert.Equal(["5", "6"], r.Lines);

        Append("7\n");
        Assert.Equal(["7"], tail.Read().Lines);           // plain appends after the reset
    }
}
