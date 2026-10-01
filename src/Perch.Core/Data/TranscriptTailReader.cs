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
/// locked file reads as nothing new. Not thread-safe — one reader per consumer, called from one thread at a time.
/// </summary>
public sealed class TranscriptTailReader
{
    private const int FingerprintLength = 64;
    private const int BackScanChunk = 64 * 1024;

    private readonly int _initialLines;
    private long _offset;                       // bytes consumed: always just past a '\n' (or 0)
    private byte[] _fingerprint = [];           // the bytes immediately before _offset
    private bool _started;

    /// <param name="path">The transcript to follow.</param>
    /// <param name="initialLines">How many trailing lines the first read returns; 0 = the whole file.</param>
    public TranscriptTailReader(string path, int initialLines = 0)
    {
        Path = path;
        _initialLines = Math.Max(0, initialLines);
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
            if (!_started || reset)
            {
                _started = true;
                _offset = 0;
                _fingerprint = [];
                bool skipped = false;
                if (_initialLines > 0)
                {
                    _offset = SeekLastLines(fs, _initialLines);
                    skipped = _offset > 0;
                }
                return new TailRead(ReadFrom(fs), reset, skipped);
            }
            return new TailRead(ReadFrom(fs), false, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

    // Reads complete lines from _offset to the last '\n', advancing _offset past it.
    private List<string> ReadFrom(FileStream fs)
    {
        var lines = new List<string>();
        long length = fs.Length;
        if (length <= _offset) return lines;

        var bytes = new byte[length - _offset];
        fs.Position = _offset;
        int n = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        int lastNl = Array.LastIndexOf(bytes, (byte)'\n', n - 1);
        if (lastNl < 0) return lines;   // only a partial line so far

        int start = _offset == 0 && HasBom(bytes) ? 3 : 0;
        var text = Encoding.UTF8.GetString(bytes, start, lastNl + 1 - start);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        }

        _offset += lastNl + 1;
        int fp = (int)Math.Min(FingerprintLength, lastNl + 1);
        _fingerprint = bytes.AsSpan(lastNl + 1 - fp, fp).ToArray();
        return lines;
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

    private static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
}
