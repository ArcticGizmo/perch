using System.Text.Json.Nodes;
using Avalonia.Threading;
using Perch.Data;
using Perch.Data.Control;
using Perch.Platform;

namespace Perch.Avalonia.Services;

/// <summary>
/// A Perch-driven Claude Code session as the <em>app</em> owns it: the stream-json process
/// (<see cref="ClaudeSessionController"/>) plus its <see cref="SessionConversation"/>. Deliberately not tied
/// to a window — a <see cref="Windows.SessionWindow"/> is only a <em>view</em> onto one of these, so closing
/// the window hides the view while the session keeps running (its overlay row reopens it), and only an
/// explicit <see cref="End"/> stops the process. Controller events are marshalled to the UI thread here, so
/// every consumer (window, app) sees the conversation change on the UI thread.
/// </summary>
internal sealed class PerchSession : IDisposable
{
    private readonly ClaudeSessionController? _controller;

    public SessionConversation Conversation { get; } = new();
    public string Cwd { get; }
    /// <summary>The model as launched or last switched to (the CLI reports the launch model in init only).</summary>
    public string? Model { get; private set; }
    public string? PermissionMode { get; }
    /// <summary>The effort level as launched or last set; null = the CLI's default ("auto").</summary>
    public string? Effort { get; private set; }

    /// <summary>Known from launch (pinned or resumed id).</summary>
    public string? SessionId => _controller?.SessionId ?? Conversation.SessionId;
    public bool IsRunning => _controller is { IsRunning: true } && !HasEnded;
    public bool HasEnded { get; private set; }
    public int ExitCode { get; private set; }

    /// <summary>The process exited (UI thread). The conversation already shows the closing note.</summary>
    public event Action<PerchSession>? Ended;

    private PerchSession(ClaudeSessionController? controller, SessionLaunchOptions o)
    {
        _controller = controller;
        Cwd = o.Cwd;
        Model = o.Model;
        PermissionMode = o.PermissionMode;
        Effort = o.Effort;
    }

    /// <summary>Launches a session (fresh in the folder, or resuming <see cref="SessionLaunchOptions.ResumeId"/>).
    /// Throws when the process can't start; the collision guards run before this, in the caller.</summary>
    public static PerchSession Start(SessionLaunchOptions o)
    {
        var controller = new ClaudeSessionController();
        var session = new PerchSession(controller, o);
        controller.EventReceived += ev => Dispatcher.UIThread.Post(() => session.Conversation.Apply(ev));
        controller.Exited += (code, err) => Dispatcher.UIThread.Post(() => session.OnExited(code, err));
        try
        {
            controller.Start(o.Cwd, o.Model, o.PermissionMode, o.ResumeId, effort: o.Effort);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
        session.Conversation.SetSessionId(controller.SessionId);
        if (o.ResumeId is { } resumeId)
        {
            session.Conversation.AddNote($"resumed session {Shorten(resumeId)}");
            session.LoadHistoryAsync(resumeId);
        }
        return session;
    }

    // Transcripts past this many lines show only their tail — a multi-MB history is neither readable nor
    // cheap to lay out, and the CLI itself has the full context regardless.
    private const int MaxHistoryLines = 4000;

    // `--resume` replays nothing on the wire (only init), so the past conversation comes from the transcript
    // on disk, read off the UI thread and inserted in front of the live items (Conversation.Reset).
    private void LoadHistoryAsync(string sessionId)
    {
        var path = TranscriptLocator.Resolve(sessionId, Cwd);
        if (path is null)
        {
            Conversation.AddNote("no transcript found for this session — history unavailable", NoteKind.Error);
            return;
        }
        Task.Run(() =>
        {
            var lines = new List<string>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line) lines.Add(line);
            bool clipped = lines.Count > MaxHistoryLines;
            if (clipped) lines.RemoveRange(0, lines.Count - MaxHistoryLines);
            // The CLI replays no usage on --resume, so seed the context gauge from the transcript's last
            // prompt size — the same figure the resume estimate showed on the launcher.
            var (contextTokens, _) = TranscriptReader.ReadContextUsage(path, Cwd);
            return (lines, clipped, contextTokens);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Conversation.AddNote("couldn't read the transcript — history unavailable", NoteKind.Error);
                return;
            }
            var (lines, clipped, contextTokens) = t.Result;
            int n = Conversation.LoadHistory(lines);
            Conversation.SeedContextTokens(contextTokens);
            if (clipped && n > 0) Conversation.AddNote("showing the most recent part of a long transcript");
        }));
    }

    /// <summary>A process-less session for the headless renderer: the conversation is fed synthetic events.</summary>
    internal static PerchSession ForRender(string cwd) => new(null, new SessionLaunchOptions(cwd));

    private void OnExited(int code, string stderrTail)
    {
        if (HasEnded) return;
        HasEnded = true;
        ExitCode = code;
        Conversation.SessionEnded(code, stderrTail);
        _controller?.Dispose();
        Ended?.Invoke(this);
    }

    public void SendPrompt(string text)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.SendPrompt(text);
        Conversation.AddUserPrompt(text);
    }

    /// <summary>Allows/denies a permission card; on allow optionally switches to the CLI's suggested mode.</summary>
    public void AnswerPermission(PermissionItem item, bool allow, bool switchMode)
    {
        if (_controller is not { } c) return;
        c.RespondToPermission(item.Request, allow);
        string? switched = null;
        if (allow && switchMode && item.Request.SuggestedMode is { Length: > 0 } mode)
        {
            c.SetPermissionMode(mode);
            switched = mode;
        }
        Conversation.ResolvePermission(item, allow, switched);
    }

    /// <summary>Answers an AskUserQuestion card: allow, with the answers folded into the tool input.</summary>
    public void AnswerQuestion(PermissionItem item, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        if (_controller is not { } c) return;
        var questions = AskUserQuestionInput.Parse(item.Request.InputJson);
        JsonNode updated = AskUserQuestionInput.BuildAnswer(item.Request.InputJson, answers);
        c.RespondToPermission(item.Request, allow: true, updatedInput: updated);
        Conversation.ResolvePermission(item, allowed: true, answerSummary: AskUserQuestionInput.Summarise(questions, answers));
    }

    public void SetPermissionMode(string mode) => _controller?.SetPermissionMode(mode);

    /// <summary>Switches the model for the rest of the session (control request; no ack is surfaced).</summary>
    public void SetModel(string model)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.SetModel(model);
        Model = model;
        Conversation.AddNote($"model → {model}");
        Conversation.SetSessionId(SessionId);   // nudge StateChanged so pills refresh
    }

    /// <summary>Sets the effort level for the rest of the session (sends <c>/effort &lt;level&gt;</c>).</summary>
    public void SetEffort(string level)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.SetEffort(level);
        Effort = level == "auto" ? null : level;
        Conversation.AddNote($"effort → {level}");
        Conversation.SetSessionId(SessionId);
    }

    public void Interrupt() => _controller?.Interrupt();

    /// <summary>Stops the process. The session stays resumable on disk; <see cref="Ended"/> follows.</summary>
    public void End() => _controller?.Stop();

    /// <summary>Stops the process and reopens the same session in a real terminal (<c>claude --resume</c>).</summary>
    public void HandBackToTerminal()
    {
        var id = SessionId;
        if (string.IsNullOrEmpty(id)) return;
        Conversation.AddNote($"handing session {Shorten(id)} to a terminal…");
        _controller?.Stop();
        try { PlatformServices.SessionLauncher.Reopen(Cwd, id, TerminalApp.Auto); }
        catch (Exception ex) { Conversation.AddNote($"couldn't open a terminal: {ex.Message}", NoteKind.Error); }
    }

    public void Dispose() => _controller?.Dispose();

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}

/// <summary>Everything a session launch needs, in one place — what the launcher collects and the CLI/elevate
/// paths supply.</summary>
internal sealed record SessionLaunchOptions(
    string Cwd,
    string? Model = null,
    string? PermissionMode = null,
    string? Effort = null,
    string? ResumeId = null);
