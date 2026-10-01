using System.IO;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.Control;

namespace Perch.Avalonia.Services;

/// <summary>
/// Where one Roost pane's conversation comes from, behind one shape:
/// <list type="bullet">
/// <item><b>Controlled</b> — a Perch-driven session's live, in-memory <see cref="PerchSession.Conversation"/>
///   (streaming deltas, no disk hop). Borrowed, never owned: disposing the feed only unsubscribes.</item>
/// <item><b>Tailed</b> — an external terminal/IDE session: its transcript is located off the UI thread, then
///   followed by a <see cref="TranscriptTailHost{T}"/> (last ~600 lines, then live appends) into a conversation the
///   feed owns. Lines are decoded off the UI thread too: a fresh start (the first read, a reset, "load earlier")
///   builds a whole conversation there and swaps it in; an append arrives parsed and is only folded in here. A
///   brand-new session whose transcript doesn't exist yet is looked for again on each <see cref="Poke"/>.</item>
/// <item><b>Fixed</b> — a prebuilt conversation (the headless renderer's samples).</item>
/// </list>
/// <see cref="Changed"/> fires on the UI thread, coalesced to one notification per dispatcher pass, whenever the
/// conversation's content or state moves (or <see cref="Conversation"/> is swapped) — the pane refreshes its mini
/// card and rebinds its thread off it.
/// </summary>
internal sealed class RoostFeed : IDisposable
{
    /// <summary>Trailing transcript lines a tailed pane loads before "load earlier".</summary>
    public const int TailLines = 600;

    // The roster scan pokes every feed; a tailed one re-reads (or re-resolves) at most this often.
    private const long PokeEveryMs = 2000;

    private readonly string? _cwd;   // tailed feeds only: where to look for the transcript
    private TranscriptTailHost<Decoded>? _tail;
    private string? _path;
    private bool _notifyQueued, _disposed, _resolving;
    private long _lastPokeMs;

    // What one tail read decoded to, off the UI thread: a whole new conversation (a fresh start), or the appended
    // lines, parsed. Bytes is the transcript's size at the read (the "load earlier" gate).
    private sealed record Decoded(SessionConversation? Fresh, List<SessionConversation.ParsedTranscriptLine> Appended,
        bool SkippedEarlier, long Bytes);

    private RoostFeed(SessionConversation conversation, bool controlled, string sessionId, string? cwd = null)
    {
        Conversation = conversation;
        IsControlled = controlled;
        SessionId = sessionId;
        _cwd = cwd;
        Attach(conversation);
    }

    public SessionConversation Conversation { get; private set; }

    /// <summary>Perch drives this session (its cards will be answerable in P2); otherwise read-only.</summary>
    public bool IsControlled { get; }

    /// <summary>The session id the feed was built for — a tailed pane whose session id changed (<c>/clear</c>)
    /// needs a new feed on the new transcript.</summary>
    public string SessionId { get; }

    /// <summary>True until a tailed feed's first read lands, or its first look for the transcript finds nothing
    /// (a controlled/fixed feed is never loading).</summary>
    public bool IsLoading { get; private set; }

    /// <summary>A tailed feed started from the last <see cref="TailLines"/> lines of a longer transcript.</summary>
    public bool SkippedEarlier { get; private set; }

    /// <summary>"Load earlier" is re-reading the whole transcript.</summary>
    public bool IsLoadingEarlier { get; private set; }

    /// <summary>The transcript's size at the last read (0 until one lands).</summary>
    public long TranscriptBytes { get; private set; }

    /// <summary>Loading the rest would be a large read and render, so the pane asks first (the history viewer's
    /// <see cref="SessionHistory.LargeTranscriptBytes"/> gate).</summary>
    public bool EarlierIsLarge => TranscriptBytes >= SessionHistory.LargeTranscriptBytes;

    public event Action? Changed;

    public static RoostFeed ForControlled(PerchSession session) =>
        new(session.Conversation, controlled: true, session.SessionId ?? "");

    public static RoostFeed ForFixed(SessionConversation conversation, string sessionId, bool controlled = false) =>
        new(conversation, controlled, sessionId);

    public static RoostFeed ForTranscript(string sessionId, string cwd)
    {
        var feed = new RoostFeed(NewHistoryConversation(sessionId), controlled: false, sessionId, cwd) { IsLoading = true };
        feed.Resolve();
        return feed;
    }

    private static SessionConversation NewHistoryConversation(string sessionId)
    {
        var conv = new SessionConversation();
        conv.UseHistorySession(sessionId);   // lets image-bearing user lines resolve their cached images
        return conv;
    }

    /// <summary>The roster scanned (UI thread). A tailed feed re-reads its transcript in case the watcher missed
    /// a write, or — when no transcript was found yet (a session that hasn't written one) — looks again. A no-op
    /// for controlled and fixed feeds, and throttled to one go every couple of seconds.</summary>
    public void Poke()
    {
        if (_disposed || _cwd is null) return;
        long now = Environment.TickCount64;
        if (now - _lastPokeMs < PokeEveryMs) return;
        _lastPokeMs = now;
        if (_tail is { } t) t.Poke();
        else if (!_resolving) Resolve();
    }

    // Locates the transcript off the UI thread (a miss is cheap: TranscriptLocator remembers it), then tails it.
    private void Resolve()
    {
        _resolving = true;
        string sid = SessionId, cwd = _cwd!;
        Task.Run(() => TranscriptLocator.Resolve(sid, cwd)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _resolving = false;
            StartTail(t.IsCompletedSuccessfully ? t.Result : null);
        }));
    }

    private void StartTail(string? path)
    {
        if (_disposed || _tail is not null) return;
        if (path is null)
        {
            // No transcript yet: the pane says "No activity yet", and the next Poke looks again.
            if (IsLoading) { IsLoading = false; Notify(); }
            return;
        }
        _path = path;
        _tail = NewTail(path, TailLines);
        _tail.Start(watch: true);
    }

    private TranscriptTailHost<Decoded> NewTail(string path, int initialLines) =>
        new(path, initialLines, Decode, OnTail);

    /// <summary>A tailed feed that started from the last <see cref="TailLines"/> lines can load the rest.</summary>
    public bool CanLoadEarlier => !IsControlled && SkippedEarlier && _path is not null && !_disposed;

    /// <summary>Re-tails the whole transcript into a fresh conversation, built off the UI thread (the pane rebinds
    /// via <see cref="Changed"/> once it lands). The pane gates a large one behind a confirm first.</summary>
    public void LoadEarlier()
    {
        if (!CanLoadEarlier || IsLoadingEarlier) return;
        _tail?.Dispose();
        IsLoadingEarlier = true;
        _tail = NewTail(_path!, initialLines: 0);
        _tail.Start(watch: true);
        Notify();
    }

    // Thread pool: the costly part of a read. A fresh start (the host's first read — a new host is how "load
    // earlier" starts over — or a reset) builds a whole conversation, which nothing is bound to yet; an append is
    // parsed line by line, to be folded into the bound conversation on the UI thread.
    private Decoded Decode(TailRead read, bool first)
    {
        long bytes = 0;
        try { if (_path is { } p) bytes = new FileInfo(p).Length; } catch { /* size unknown: no gate */ }
        if (first || read.Reset)
        {
            var conv = NewHistoryConversation(SessionId);
            foreach (var line in read.Lines) conv.AppendTranscriptLine(line);
            return new Decoded(conv, [], read.SkippedEarlier, bytes);
        }
        var parsed = new List<SessionConversation.ParsedTranscriptLine>(read.Lines.Count);
        foreach (var line in read.Lines)
            if (SessionConversation.ParseTranscriptLine(line, SessionId) is { } pl) parsed.Add(pl);
        return new Decoded(null, parsed, false, bytes);
    }

    private void OnTail(Decoded d)
    {
        if (_disposed) return;
        if (d.Fresh is { } fresh)
        {
            Detach(Conversation);
            Conversation = fresh;
            Attach(fresh);
            SkippedEarlier = d.SkippedEarlier;
            IsLoadingEarlier = false;
        }
        else
        {
            foreach (var line in d.Appended) Conversation.AppendParsedTranscriptLine(line);
        }
        if (d.Bytes > 0) TranscriptBytes = d.Bytes;
        IsLoading = false;
        Notify();
    }

    private void Attach(SessionConversation c)
    {
        c.Changed += OnConversationChanged;
        c.StateChanged += Notify;
        c.Reset += Notify;
    }

    private void Detach(SessionConversation c)
    {
        c.Changed -= OnConversationChanged;
        c.StateChanged -= Notify;
        c.Reset -= Notify;
    }

    private void OnConversationChanged(ConversationItem _, ConversationChange __) => Notify();

    // Streaming deltas fire per token; coalesce them into one refresh per dispatcher pass.
    private void Notify()
    {
        if (_disposed || _notifyQueued) return;
        _notifyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _notifyQueued = false;
            if (!_disposed) Changed?.Invoke();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach(Conversation);
        _tail?.Dispose();
        Changed = null;
    }
}
