using System.Text;
using System.Text.Json;

namespace Perch.Remote;

/// <summary>What a controller that attaches is sent to catch up: the transcript lines the session loaded on resume, then
/// the stream lines since. <see cref="Truncated"/> says earlier messages aren't in it.</summary>
internal sealed record JournalSnapshot(IReadOnlyList<string> Transcript, IReadOnlyList<string> Lines, bool Truncated,
    int? ExitCode);

/// <summary>Someone following a <see cref="SessionJournal"/>. Called on the thread that fed the journal (the session's
/// stdout pump), in order; must not block.</summary>
internal interface IJournalListener
{
    void OnLine(string line);

    /// <summary>The transcript loaded after this listener attached (a resume's history lands a moment after start).</summary>
    void OnTranscript(IReadOnlyList<string> lines, bool truncated);

    void OnEnded(int exitCode);
}

/// <summary>
/// The record of what fed one worker session's conversation, so a controller attaching mid-session can rebuild it
/// (docs/remote-workers-plan.md, W3): the transcript lines loaded on resume, then the raw stdout lines minus the
/// <c>stream_event</c> partials (each message's final record follows its partials). Capped at
/// <see cref="MaxChars"/>, oldest dropped.
/// <para>It only records while <see cref="Recording"/> says so (the worker setting is on), so sessions on a machine that
/// never controls anything hold no extra memory. A session that ran while it was off is marked truncated.</para>
/// </summary>
internal sealed class SessionJournal
{
    /// <summary>On while this machine lets paired machines control it. Read at each line.</summary>
    public static volatile bool RecordingEnabled;

    /// <summary>The most it keeps, in characters (about 16 MB of mostly-ASCII JSON).</summary>
    public const int MaxChars = 16 * 1024 * 1024;

    private readonly Func<bool> _recording;
    private readonly Lock _lock = new();
    private List<string> _transcript = [];
    private readonly List<string> _lines = [];
    private long _chars;
    private bool _truncated;
    private int? _exitCode;
    private readonly List<IJournalListener> _listeners = [];

    public SessionJournal(Func<bool>? recording = null) => _recording = recording ?? (() => RecordingEnabled);

    /// <summary>One raw stdout line from the session's process.</summary>
    public void Append(string line)
    {
        IJournalListener[] listeners;
        lock (_lock)
        {
            if (!Record()) return;
            if (!IsPartial(line))
            {
                _lines.Add(line);
                _chars += line.Length;
                Trim();
            }
            listeners = [.. _listeners];
        }
        foreach (var l in listeners) l.OnLine(line);
    }

    /// <summary>The transcript lines the session loaded on resume (once; they sit before every stream line).</summary>
    public void SetTranscript(IReadOnlyList<string> lines, bool clipped)
    {
        IJournalListener[] listeners;
        lock (_lock)
        {
            if (!Record()) return;
            foreach (var l in _transcript) _chars -= l.Length;
            _transcript = [.. lines];
            foreach (var l in _transcript) _chars += l.Length;
            _truncated |= clipped;
            Trim();
            listeners = [.. _listeners];
        }
        foreach (var l in listeners) l.OnTranscript(lines, clipped);
    }

    /// <summary>The session's process exited.</summary>
    public void End(int exitCode)
    {
        IJournalListener[] listeners;
        lock (_lock)
        {
            _exitCode = exitCode;
            listeners = [.. _listeners];
        }
        foreach (var l in listeners) l.OnEnded(exitCode);
    }

    /// <summary>Hands <paramref name="onSnapshot"/> everything so far and adds <paramref name="listener"/>, under one lock,
    /// so nothing is missed or sent twice. <paramref name="onSnapshot"/> runs inside the lock: it should only queue.
    /// Dispose the result to stop listening.</summary>
    public IDisposable Subscribe(Action<JournalSnapshot> onSnapshot, IJournalListener listener)
    {
        lock (_lock)
        {
            Record();   // marks the journal truncated if it isn't recording
            onSnapshot(new JournalSnapshot([.. _transcript], [.. _lines], _truncated, _exitCode));
            if (_exitCode is null) _listeners.Add(listener);
        }
        return new Unsubscriber(this, listener);
    }

    internal long CharCount { get { lock (_lock) return _chars; } }

    // Called under the lock. Off: drop what's kept and remember that the start is missing.
    private bool Record()
    {
        if (_recording()) return true;
        if (_lines.Count > 0 || _transcript.Count > 0 || !_truncated)
        {
            _lines.Clear();
            _transcript = [];
            _chars = 0;
            _truncated = true;
        }
        return false;
    }

    // Called under the lock: drops the oldest down to three quarters of the cap, transcript first, so the work is
    // done in batches rather than on every line once full.
    private void Trim()
    {
        if (_chars <= MaxChars) return;
        long target = MaxChars / 4 * 3;
        int t = 0;
        while (_chars > target && t < _transcript.Count) _chars -= _transcript[t++].Length;
        if (t > 0) _transcript.RemoveRange(0, t);
        int s = 0;
        while (_chars > target && s < _lines.Count) _chars -= _lines[s++].Length;
        if (s > 0) _lines.RemoveRange(0, s);
        _truncated = true;
    }

    /// <summary>A <c>stream_event</c> partial (a text delta and the like): shown live, but not kept, since the message's
    /// final record carries the same content. Reads only as far as the top-level <c>type</c>.</summary>
    internal static bool IsPartial(string line)
    {
        if (!line.Contains("stream_event", StringComparison.Ordinal)) return false;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isType = reader.ValueTextEquals("type"u8);
                if (!reader.Read()) return false;
                if (isType) return reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("stream_event"u8);
                reader.Skip();
            }
        }
        catch (JsonException) { /* not JSON: keep it, the parser skips it */ }
        return false;
    }

    private sealed class Unsubscriber(SessionJournal journal, IJournalListener listener) : IDisposable
    {
        public void Dispose()
        {
            lock (journal._lock) journal._listeners.Remove(listener);
        }
    }
}
