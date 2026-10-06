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
///
/// <para>A resumed session starts <b>dormant</b> (<see cref="Dormant"/>, docs/session-recovery-plan.md D4): its
/// conversation is loaded from the transcript but no <c>claude</c> process runs until the first send, which
/// <see cref="Wake"/>s it. Opening a session just to read it spends nothing.</para>
/// </summary>
internal sealed class PerchSession : IDisposable
{
    // Null while dormant (until Wake) and for the headless renderer's process-less stand-in.
    private ClaudeSessionController? _controller;

    public SessionConversation Conversation { get; } = new();
    public string Cwd { get; }
    /// <summary>The model as launched or last switched to (the CLI reports the launch model in init only).</summary>
    public string? Model { get; private set; }
    public string? PermissionMode { get; private set; }
    /// <summary>The effort level as launched or last set; null = the CLI's default ("auto").</summary>
    public string? Effort { get; private set; }
    /// <summary>The config dir this session was launched under (injected as <c>CLAUDE_CONFIG_DIR</c>), or null
    /// when it inherited Perch's environment (→ the primary account). Drives the footer's account chip.</summary>
    public string? ConfigDir { get; private set; }

    /// <summary>Known from launch (pinned or resumed id).</summary>
    public string? SessionId => _controller?.SessionId ?? Conversation.SessionId;
    public bool IsRunning => _controller is { IsRunning: true } && !HasEnded;

    /// <summary>Shown with its conversation, but no <c>claude</c> process yet: the first send <see cref="Wake"/>s it.
    /// Not running and not ended.</summary>
    public bool IsDormant { get; private set; }

    /// <summary>A dormant session started its process (UI thread).</summary>
    public event Action<PerchSession>? Woke;
    public bool HasEnded { get; private set; }
    public int ExitCode { get; private set; }

    /// <summary>The session's <c>/rename</c> custom title, if any — read from the transcript on resume and
    /// updated optimistically when a <c>/rename</c> is sent. Null when never renamed.</summary>
    public string? Title { get; private set; }

    /// <summary>The process exited (UI thread). The conversation already shows the closing note.</summary>
    public event Action<PerchSession>? Ended;

    /// <summary>The session's <see cref="Title"/> changed (UI thread).</summary>
    public event Action? TitleChanged;

    /// <summary>Remote Control was enabled or disabled for this session (UI thread). Carries the CLI's ack —
    /// <see cref="RemoteControlEvent.SessionUrl"/> is set on enable (the claude.ai / QR target).</summary>
    public event Action<RemoteControlEvent>? RemoteControlChanged;

    /// <summary>Whether Remote Control is currently on for this session, and its claude.ai session URL.</summary>
    public bool RemoteControlEnabled { get; private set; }
    public string? RemoteControlUrl { get; private set; }

    private void SetTitle(string? title)
    {
        if (title == Title) return;
        Title = title;
        TitleChanged?.Invoke();
    }

    // The argument of a "/rename <title>" prompt, or null when it isn't one (or has no title argument).
    private static string? RenameArg(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith("/rename", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = t["/rename".Length..].Trim();
        return rest.Length > 0 ? rest : null;
    }

    private PerchSession(ClaudeSessionController? controller, SessionLaunchOptions o)
    {
        _controller = controller;
        Cwd = o.Cwd;
        Model = o.Model;
        PermissionMode = o.PermissionMode;
        Effort = o.Effort;
        ConfigDir = o.ConfigDir;
    }

    /// <summary>Launches a session (fresh in the folder, or resuming <see cref="SessionLaunchOptions.ResumeId"/>).
    /// Throws when the process can't start; the collision guards run before this, in the caller.</summary>
    public static PerchSession Start(SessionLaunchOptions o)
    {
        var session = new PerchSession(null, o);
        session.Launch(o);
        if (o.ResumeId is { } resumeId)
        {
            session.Conversation.AddNote($"resumed session {Shorten(resumeId)}");
            session.LoadHistoryAsync(resumeId);
        }
        return session;
    }

    /// <summary>A resumed session shown <b>without</b> starting <c>claude</c>: the conversation loads from the
    /// transcript, and <see cref="Wake"/> starts the process on the first send. Never throws for a missing
    /// transcript (the conversation says so); the collision guards run at wake, in the caller.</summary>
    public static PerchSession Dormant(SessionLaunchOptions o)
    {
        if (o.ResumeId is not { Length: > 0 } resumeId)
            throw new ArgumentException("A dormant session needs a session id to resume.", nameof(o));
        var session = new PerchSession(null, o) { IsDormant = true };
        session.Conversation.SetSessionId(resumeId);
        session.LoadHistoryAsync(resumeId);
        return session;
    }

    /// <summary>Starts <c>claude --resume</c> for a dormant session, with the launch settings chosen since it opened
    /// (model, mode, effort, account). The conversation already holds the history, so nothing is reloaded. Throws
    /// when the process can't start, leaving the session dormant.</summary>
    public void Wake(SessionLaunchOptions o)
    {
        if (!IsDormant) return;
        Launch(o with { ResumeId = SessionId });
        IsDormant = false;
        Model = o.Model;
        PermissionMode = o.PermissionMode;
        Effort = o.Effort;
        ConfigDir = o.ConfigDir;
        Conversation.AddNote($"resumed session {Shorten(SessionId ?? "")}");
        Woke?.Invoke(this);
    }

    // Spawns the controller and wires it into this session. Throws (and disposes the controller) when it can't start.
    private void Launch(SessionLaunchOptions o)
    {
        var controller = new ClaudeSessionController();
        controller.EventReceived += ev => Dispatcher.UIThread.Post(() =>
        {
            // Remote-control acks aren't conversation items — route them to the session's own signal (which
            // still drops a note); everything else folds into the conversation model.
            if (ev is RemoteControlEvent rc) OnRemoteControl(rc);
            else Conversation.Apply(ev);
        });
        controller.Exited += (code, err) => Dispatcher.UIThread.Post(() => OnExited(code, err));
        try
        {
            controller.Start(o.Cwd, o.Model, o.PermissionMode, o.ResumeId, effort: o.Effort, configDir: o.ConfigDir);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
        _controller = controller;
        Conversation.SetSessionId(controller.SessionId);
        // The CLI doesn't heartbeat the session-file status over stream-json, so publish our own live status
        // (busy/waiting/idle) into the in-process ControlledSessions registry the overlay reads. StateChanged
        // fires at turn/permission boundaries — exactly the transitions that move the status.
        Conversation.StateChanged += PublishActivity;
        PublishActivity();   // seed the real status from the starting state
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
            List<string> lines;
            bool clipped;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                (lines, clipped) = TranscriptScan.LastLines(ReadAll(reader), MaxHistoryLines);

            static IEnumerable<string> ReadAll(StreamReader r)
            {
                while (r.ReadLine() is { } line) yield return line;
            }
            // The CLI replays no usage on --resume, so seed the context gauge from the transcript's last
            // prompt size — the same figure the resume estimate showed on the launcher.
            var (contextTokens, _) = TranscriptReader.ReadContextUsage(path, Cwd);
            var title = TranscriptReader.ReadTitle(path);   // the /rename custom title, if the session has one
            return (lines, clipped, contextTokens, title);
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Conversation.AddNote("couldn't read the transcript — history unavailable", NoteKind.Error);
                return;
            }
            var (lines, clipped, contextTokens, title) = t.Result;
            int n = Conversation.LoadHistory(lines, sessionId);
            Conversation.SeedContextTokens(contextTokens);
            SetTitle(title);
            if (clipped && n > 0) Conversation.AddNote("showing the most recent part of a long transcript");
        }));
    }

    /// <summary>A process-less session for the headless renderer: the conversation is fed synthetic events.</summary>
    internal static PerchSession ForRender(string cwd) => new(null, new SessionLaunchOptions(cwd));

    /// <summary>A dormant stand-in for the headless renderer: no transcript is read, the caller feeds the history.</summary>
    internal static PerchSession DormantForRender(string cwd, string sessionId)
    {
        var session = new PerchSession(null, new SessionLaunchOptions(cwd, ResumeId: sessionId)) { IsDormant = true };
        session.Conversation.SetSessionId(sessionId);
        return session;
    }

    private void OnExited(int code, string stderrTail)
    {
        if (HasEnded) return;
        HasEnded = true;
        ExitCode = code;
        // Stop publishing status; the controller has already unregistered the session (and dropped its lock),
        // so the overlay no longer sees it as controlled and won't read a stale "busy".
        Conversation.StateChanged -= PublishActivity;
        Conversation.SessionEnded(code, stderrTail);
        _controller?.Dispose();
        Ended?.Invoke(this);
    }

    /// <summary>Publishes the session's live status into the in-process <see cref="ControlledSessions"/>
    /// registry so the overlay reflects it. No-op for the process-less render session. A rebased id (/clear)
    /// needs no cleanup — the controller has already re-registered under the new id, and this writes to it.</summary>
    private void PublishActivity()
    {
        if (_controller is null || HasEnded) return;
        var id = SessionId;
        if (string.IsNullOrEmpty(id)) return;

        // A pending permission/question means the session is blocked on the user; otherwise a live turn is
        // busy and a settled one idle.
        var activity = Conversation.PendingPermission is not null ? ControlledActivity.Waiting
            : Conversation.TurnActive ? ControlledActivity.Busy
            : ControlledActivity.Idle;
        ControlledSessions.SetActivity(id, activity);
    }

    public void SendPrompt(string text) => SendPrompt(text, null);

    /// <summary>Sends a prompt with attachments. Image attachments ride along as base64 content blocks;
    /// dropped-file attachments are display-only here (their paths are already in <paramref name="text"/>).</summary>
    public void SendPrompt(string text, IReadOnlyList<MessageAttachment>? attachments)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.SendPrompt(text, BuildImageContents(attachments));
        Conversation.AddUserPrompt(text, attachments);
        // Optimistically reflect a /rename in the title (the CLI writes the custom-title record to the transcript).
        if (RenameArg(text) is { } title) SetTitle(title);
    }

    /// <summary>The largest image Perch will attach: the API's own per-image limit. Checked when the image is
    /// staged (so the user hears about it then), and again here before the file is read and base64-encoded on
    /// the UI thread, in case it grew since.</summary>
    internal const long MaxImageBytes = 5 * 1024 * 1024;

    // Reads each image attachment off disk and base64-encodes it for the outgoing content block. Best-effort:
    // an unreadable or over-size image is dropped rather than aborting the send. Null when there are no images.
    private static IReadOnlyList<ImageContent>? BuildImageContents(IReadOnlyList<MessageAttachment>? attachments)
    {
        if (attachments is null) return null;
        List<ImageContent>? images = null;
        foreach (var a in attachments)
        {
            if (a.Kind != AttachmentKind.Image) continue;
            try
            {
                if (new FileInfo(a.Path).Length > MaxImageBytes) continue;
                var bytes = File.ReadAllBytes(a.Path);
                var media = a.MediaType ?? MediaTypeForPath(a.Path);
                (images ??= []).Add(new ImageContent(media, Convert.ToBase64String(bytes)));
            }
            catch { /* skip an image we can't read */ }
        }
        return images;
    }

    /// <summary>The image media type for a file path, by extension (defaults to png).</summary>
    internal static string MediaTypeForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".webp"           => "image/webp",
        _                 => "image/png",
    };

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

    /// <summary>Switches the permission mode for the rest of the session. The displayed mode updates
    /// optimistically (the CLI defers the <c>set_permission_mode</c> ack until the next turn, so waiting on it
    /// leaves the pill stale); the ack's own "permission mode → X" note still lands when it arrives.</summary>
    public void SetPermissionMode(string mode)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.SetPermissionMode(mode);
        PermissionMode = mode;
        Conversation.SetSessionId(SessionId);   // nudge StateChanged so the mode pill refreshes now
    }

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

    public void Interrupt()
    {
        Conversation.NoteInterrupt();   // so a running /compact settles to "canceled", not a green success
        _controller?.Interrupt();
    }

    /// <summary>Enables/disables Remote Control for the live session (the CLI's <c>remote_control</c> control
    /// request). The ack arrives asynchronously as a <see cref="RemoteControlEvent"/> → <see cref="OnRemoteControl"/>.</summary>
    public void RequestRemoteControl(bool enabled)
    {
        if (_controller is not { IsRunning: true } c) return;
        c.RequestRemoteControl(enabled);
    }

    // The CLI's remote_control ack (UI thread): update state, drop a note, and fan out to the window (which
    // pops the QR on enable). An error ack surfaces as an error note and leaves state unchanged.
    private void OnRemoteControl(RemoteControlEvent ev)
    {
        if (ev.Error is { Length: > 0 } err)
        {
            Conversation.AddNote($"remote control unavailable: {err}", NoteKind.Error);
        }
        else if (ev.SessionUrl is { Length: > 0 } url)
        {
            RemoteControlEnabled = true;
            RemoteControlUrl = url;
            Conversation.AddNote($"remote control on — {url}");
        }
        else
        {
            RemoteControlEnabled = false;
            RemoteControlUrl = null;
            Conversation.AddNote("remote control off");
        }
        RemoteControlChanged?.Invoke(ev);
    }

    /// <summary>Stops the process. The session stays resumable on disk; <see cref="Ended"/> follows. Perch's own exits
    /// call this too, and those sessions come back dormant on the next start; <see cref="EndByUser"/> is the user
    /// being done with it.</summary>
    public void End()
    {
        if (_controller is null) return;
        StoppedByPerch = true;
        _controller.Stop();
    }

    /// <summary><see cref="End"/> stopped the process, rather than it exiting by itself (a clean exit is <c>/exit</c>, a
    /// non-zero one a crash).</summary>
    public bool StoppedByPerch { get; private set; }

    /// <summary>The user let this session go from Perch (ended it, or handed it back to a terminal), so it doesn't
    /// come back after a restart (docs/session-recovery-plan.md, D3). Raised before the process stops.</summary>
    public event Action<PerchSession>? Released;

    /// <summary>The user's "End session": <see cref="Released"/>, then <see cref="End"/>.</summary>
    public void EndByUser()
    {
        Released?.Invoke(this);
        End();
    }

    /// <summary>Stops the process and reopens the same session in a real terminal (<c>claude --resume</c>).</summary>
    public void HandBackToTerminal()
    {
        var id = SessionId;
        if (string.IsNullOrEmpty(id)) return;
        Released?.Invoke(this);   // it's a terminal session now, not Perch's to restore
        Conversation.AddNote($"handing session {Shorten(id)} to a terminal…");
        _controller?.Stop();
        // Same account the session ran under here, or the terminal's `claude --resume` can't find it.
        LaunchLog.Write($"hand back: session={id} cwd={LaunchLog.Show(Cwd)} CLAUDE_CONFIG_DIR={LaunchLog.Show(ConfigDir)}");
        try { PlatformServices.SessionLauncher.Reopen(Cwd, id, TerminalApp.Auto, ConfigDir); }
        catch (Exception ex) { Conversation.AddNote($"couldn't open a terminal: {ex.Message}", NoteKind.Error); }
    }

    public void Dispose() => _controller?.Dispose();

    /// <summary>A session id cut to its first eight characters, for notes and messages.</summary>
    internal static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}

/// <summary>Everything a session launch needs, in one place — what the launcher collects and the CLI/elevate
/// paths supply.</summary>
internal sealed record SessionLaunchOptions(
    string Cwd,
    string? Model = null,
    string? PermissionMode = null,
    string? Effort = null,
    string? ResumeId = null,
    string? ConfigDir = null);
