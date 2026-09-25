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
///   followed by a <see cref="TranscriptTailHost"/> (last ~600 lines, then live appends) into a conversation the
///   feed owns. A reset (the file was truncated or replaced) swaps in a fresh conversation.</item>
/// <item><b>Fixed</b> — a prebuilt conversation (the headless renderer's samples).</item>
/// </list>
/// <see cref="Changed"/> fires on the UI thread, coalesced to one notification per dispatcher pass, whenever the
/// conversation's content or state moves (or <see cref="Conversation"/> is swapped) — the pane refreshes its mini
/// card and rebinds its thread off it.
/// </summary>
internal sealed class RoostFeed : IDisposable
{
    /// <summary>Trailing transcript lines a tailed pane loads (a "load earlier" affordance can come later).</summary>
    public const int TailLines = 600;

    private TranscriptTailHost? _tail;
    private bool _notifyQueued, _disposed;

    private RoostFeed(SessionConversation conversation, bool controlled, string sessionId)
    {
        Conversation = conversation;
        IsControlled = controlled;
        SessionId = sessionId;
        Attach(conversation);
    }

    public SessionConversation Conversation { get; private set; }

    /// <summary>Perch drives this session (its cards will be answerable in P2); otherwise read-only.</summary>
    public bool IsControlled { get; }

    /// <summary>The session id the feed was built for — a tailed pane whose session id changed (<c>/clear</c>)
    /// needs a new feed on the new transcript.</summary>
    public string SessionId { get; }

    /// <summary>True until a tailed feed's first read lands (a controlled/fixed feed is never loading).</summary>
    public bool IsLoading { get; private set; }

    /// <summary>A tailed feed started from the last <see cref="TailLines"/> lines of a longer transcript.</summary>
    public bool SkippedEarlier { get; private set; }

    public event Action? Changed;

    public static RoostFeed ForControlled(PerchSession session) =>
        new(session.Conversation, controlled: true, session.SessionId ?? "");

    public static RoostFeed ForFixed(SessionConversation conversation, string sessionId, bool controlled = false) =>
        new(conversation, controlled, sessionId);

    public static RoostFeed ForTranscript(string sessionId, string cwd)
    {
        var feed = new RoostFeed(NewHistoryConversation(sessionId), controlled: false, sessionId) { IsLoading = true };
        Task.Run(() => TranscriptLocator.Resolve(sessionId, cwd)).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => feed.StartTail(t.IsCompletedSuccessfully ? t.Result : null)));
        return feed;
    }

    private static SessionConversation NewHistoryConversation(string sessionId)
    {
        var conv = new SessionConversation();
        conv.UseHistorySession(sessionId);   // lets image-bearing user lines resolve their cached images
        return conv;
    }

    private void StartTail(string? path)
    {
        if (_disposed) return;
        if (path is null) { IsLoading = false; Notify(); return; }   // no transcript (yet) — the pane says so
        _tail = new TranscriptTailHost(path, TailLines, OnTail);
        _tail.Start(watch: true);
    }

    private void OnTail(TailRead read)
    {
        if (_disposed) return;
        if (read.Reset)
        {
            Detach(Conversation);
            Conversation = NewHistoryConversation(SessionId);
            Attach(Conversation);
        }
        if (IsLoading || read.Reset) SkippedEarlier = read.SkippedEarlier;
        IsLoading = false;
        foreach (var line in read.Lines) Conversation.AppendTranscriptLine(line);
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
