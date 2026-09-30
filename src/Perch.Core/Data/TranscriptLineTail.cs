namespace Perch.Data;

/// <summary>
/// Tails one transcript by byte offset for the history viewer (review fixes CP24): each <see cref="Read"/> returns
/// only the whole lines appended since the last one, rather than re-reading and re-splitting the whole file on
/// every change. When the file was truncated or replaced, <c>Reset</c> is true and the lines are the whole file
/// again. Built on <see cref="TranscriptFold"/>, so it follows the same rules (partial trailing lines wait, the
/// head check spots a replacement, a leading BOM is stripped). Not thread-safe: one reader at a time.
/// </summary>
internal sealed class TranscriptLineTail
{
    // The fold's state is just the lines it has fed since the last Read. A reset swaps in a fresh state object,
    // which is how a Read tells a reset apart from an append.
    private sealed class Pending { public readonly List<string> Lines = new(); }

    private readonly LineFolder<Pending> _folder = new(() => new Pending(), (s, line) => s.Lines.Add(line));
    private readonly TranscriptFold _fold;
    private Pending? _current;

    public TranscriptLineTail() => _fold = new TranscriptFold(_folder);

    /// <summary>Total bytes read from disk, a test probe.</summary>
    internal long BytesRead => _fold.BytesRead;

    /// <summary>The lines appended to <paramref name="path"/> since the last read (all of them on the first), or
    /// null when the file can't be read right now. <c>Reset</c> means the file was truncated or replaced, so the
    /// caller should rebuild from these lines rather than append them.</summary>
    public (bool Reset, List<string> Lines)? Read(string path)
    {
        var state = _fold.Get(path, _folder, s => s, (Pending?)null);
        if (state is null) return null;
        bool reset = _current is not null && !ReferenceEquals(state, _current);
        _current = state;
        var lines = new List<string>(state.Lines);
        state.Lines.Clear();
        return (reset, lines);
    }
}
