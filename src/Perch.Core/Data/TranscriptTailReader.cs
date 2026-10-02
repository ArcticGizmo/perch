using System.Text;

namespace Perch.Data;

/// <summary>One <see cref="TranscriptTailReader.Read"/>: the complete lines appended since the last read.</summary>
/// <param name="Lines">New complete (newline-terminated) lines, in file order. Blank lines are dropped.</param>
/// <param name="Reset">The file shrank or was replaced, so the reader started over: <paramref name="Lines"/> is
/// the file's (seeded) content afresh, and the consumer should rebuild rather than append.</param>
/// <param name="SkippedEarlier">This read started from an initial "last N lines" seek and there are earlier lines
/// it didn't return (the "load earlier" affordance). Only ever true on the first read or a reset.</param>
public readonly record struct TailRead(IReadOnlyList<string> Lines, bool Reset, bool SkippedEarlier);

/// <summary>
/// Incremental, byte-offset tailing of an append-only <c>.jsonl</c> transcript. Each <see cref="Read"/> returns
/// only the lines appended since the previous one, so following a live session costs IO proportional to what
/// was written rather than to the whole file (the old re-read-everything tail went quadratic with several
/// panes on multi-MB transcripts).
///
/// <list type="bullet">
/// <item><b>Partial lines:</b> only bytes up to the last <c>\n</c> are consumed; a half-written trailing record
///   is left in place and picked up whole on the next read. Decoding stops at a <c>\n</c> byte, which never
///   occurs inside a UTF-8 multibyte sequence, so a split never corrupts a character.</item>
/// <item><b>Truncation / replacement:</b> a file shorter than the offset, or whose bytes just before the offset
///   no longer match what was read (a rewrite with a different prefix), resets the reader to the top.</item>
/// <item><b>Initial seek:</b> with <c>initialLines &gt; 0</c> the first read (and a reset) returns only the last
///   that-many complete lines, found by scanning backwards from the end.</item>
/// </list>
///
/// Opens with <see cref="FileShare.ReadWrite"/> (the file is written live) and never throws: a missing or
/// locked file, or one that shrinks mid-read, reads as nothing new (or as the lines completed so far). The file
/// is read in bounded chunks, so a huge transcript never needs one buffer the size of the file (review fixes
/// CP26), and a read commits its offset only when it completes. Not thread-safe — one reader per consumer, called
/// from one thread at a time.
/// </summary>
public sealed class TranscriptTailReader
{
    private const int FingerprintLength = 64;
    private const int BackScanChunk = 64 * 1024;
    private const int DefaultReadChunk = 1024 * 1024;

    private readonly int _initialLines;
    private readonly int _readChunk;
    private long _offset;                       // bytes consumed: always just past a '\n' (or 0)
    private byte[] _fingerprint = [];           // the bytes immediately before _offset
    private bool _started;

    /// <param name="path">The transcript to follow.</param>
    /// <param name="initialLines">How many trailing lines the first read returns; 0 = the whole file.</param>
    public TranscriptTailReader(string path, int initialLines = 0) : this(path, initialLines, DefaultReadChunk) { }

    /// <summary>The test seam: a tiny <paramref name="readChunk"/> puts lines (and characters) across chunk
    /// boundaries.</summary>
    internal TranscriptTailReader(string path, int initialLines, int readChunk)
    {
        Path = path;
        _initialLines = Math.Max(0, initialLines);
        _readChunk = Math.Max(1, readChunk);
    }

    public string Path { get; }

    /// <summary>Bytes consumed so far (through the last complete line read).</summary>
    public long Offset => _offset;

    /// <summary>Reads what's been appended since the last call. See the class remarks.</summary>
    public TailRead Read()
    {
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool reset = _started && !StillSameFile(fs);
            long offset = _offset;
            byte[] fingerprint = _fingerprint;
            bool skipped = false;
            if (!_started || reset)
            {
                offset = 0;
                fingerprint = [];
                if (_initialLines > 0)
                {
                    offset = SeekLastLines(fs, _initialLines);
                    skipped = offset > 0;
                }
            }
            var lines = ReadFrom(fs, ref offset, ref fingerprint);
            // Commit only now: a read that throws part-way leaves the reader where it was, to try again.
            _started = true;
            _offset = offset;
            _fingerprint = fingerprint;
            return new TailRead(lines, reset, skipped);
        }
        catch
        {
            return new TailRead([], false, false);
        }
    }

    // True when the file still holds, just before _offset, the bytes we last consumed.
    private bool StillSameFile(FileStream fs)
    {
        if (fs.Length < _offset) return false;
        if (_fingerprint.Length == 0) return true;
        var buf = new byte[_fingerprint.Length];
        fs.Position = _offset - _fingerprint.Length;
        return fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) == buf.Length
            && buf.AsSpan().SequenceEqual(_fingerprint);
    }

    // Reads the complete lines from offset up to the length seen at the start, in bounded chunks, advancing
    // offset past the last '\n' and refreshing the fingerprint. A line spanning chunks is carried until its '\n'
    // arrives (decoding only whole lines, so a split multibyte character is never corrupted); bytes after the last
    // '\n' are a partial line and stay unread. A file that shrinks mid-read just ends the read early.
    private List<string> ReadFrom(FileStream fs, ref long offset, ref byte[] fingerprint)
    {
        var lines = new List<string>();
        long end = fs.Length;
        if (end <= offset) return lines;

        var buf = new byte[(int)Math.Min(_readChunk, end - offset)];
        var carry = new MemoryStream();   // the start of a line that began in an earlier chunk
        bool atFileStart = offset == 0;
        long pos = offset, consumed = offset;
        fs.Position = offset;
        while (pos < end)
        {
            int want = (int)Math.Min(buf.Length, end - pos);
            int n = fs.ReadAtLeast(buf.AsSpan(0, want), want, throwOnEndOfStream: false);
            if (n <= 0) break;
            int start = 0;
            while (start < n)
            {
                int nl = buf.AsSpan(start, n - start).IndexOf((byte)'\n');
                if (nl < 0) { carry.Write(buf, start, n - start); break; }
                ReadOnlySpan<byte> line;
                if (carry.Length > 0)
                {
                    carry.Write(buf, start, nl);
                    line = carry.GetBuffer().AsSpan(0, (int)carry.Length);
                }
                else line = buf.AsSpan(start, nl);
                AddLine(lines, line, ref atFileStart);
                carry.SetLength(0);
                start += nl + 1;
                consumed = pos + start;
            }
            pos += n;
        }

        if (consumed > offset)
        {
            offset = consumed;
            fingerprint = BytesBefore(fs, consumed);
        }
        return lines;
    }

    private static void AddLine(List<string> lines, ReadOnlySpan<byte> bytes, ref bool atFileStart)
    {
        if (atFileStart)
        {
            atFileStart = false;
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        }
        var line = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
    }

    // The fingerprint: up to FingerprintLength bytes ending at `at` (empty if they can't be read back).
    private static byte[] BytesBefore(FileStream fs, long at)
    {
        int len = (int)Math.Min(FingerprintLength, at);
        var b = new byte[len];
        fs.Position = at - len;
        return fs.ReadAtLeast(b, len, throwOnEndOfStream: false) == len ? b : [];
    }

    // The byte offset where the last `count` complete lines begin (0 when the file has no more than that).
    private static long SeekLastLines(FileStream fs, int count)
    {
        long length = fs.Length;
        var buf = new byte[BackScanChunk];
        long pos = length;
        int newlines = 0;
        bool sawTerminator = false;   // the '\n' ending the last complete line; bytes after it are a partial line
        while (pos > 0)
        {
            int size = (int)Math.Min(BackScanChunk, pos);
            pos -= size;
            fs.Position = pos;
            int n = fs.ReadAtLeast(buf.AsSpan(0, size), size, throwOnEndOfStream: false);
            for (int i = n - 1; i >= 0; i--)
            {
                if (buf[i] != (byte)'\n') continue;
                if (!sawTerminator) { sawTerminator = true; continue; }
                if (++newlines == count) return pos + i + 1;
            }
        }
        return 0;
    }
}
