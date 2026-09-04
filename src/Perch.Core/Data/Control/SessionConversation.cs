namespace Perch.Data.Control;

/// <summary>How a <see cref="SessionConversation"/> item changed, for incremental UI updates.</summary>
internal enum ConversationChange { Added, Updated }

/// <summary>One composed unit in the conversation: a user message, an assistant message (with its ordered
/// parts), a permission prompt, or a system note. The rich session UI binds one control per item.</summary>
internal abstract class ConversationItem;

internal sealed class UserMessageItem(string text) : ConversationItem
{
    public string Text { get; } = text;
}

internal enum NoteKind { Info, Error }

/// <summary>A quiet system line: "permission mode → plan", "session ended", a launch failure.</summary>
internal sealed class NoteItem(string text, NoteKind kind = NoteKind.Info) : ConversationItem
{
    public string Text { get; } = text;
    public NoteKind Kind { get; } = kind;
}

/// <summary>An assistant turn: streamed prose, thinking, and tool calls in the order they arrived.
/// Closed by the turn's <c>result</c> record, after which the next assistant event opens a new item.</summary>
internal sealed class AssistantMessageItem : ConversationItem
{
    private readonly List<AssistantPart> _parts = new();
    public IReadOnlyList<AssistantPart> Parts => _parts;
    public bool IsComplete { get; internal set; }
    public TurnResultEvent? Result { get; internal set; }
    internal void Add(AssistantPart part) => _parts.Add(part);
    internal AssistantPart? Last => _parts.Count > 0 ? _parts[^1] : null;
}

internal abstract class AssistantPart;

/// <summary>Prose. <see cref="IsStreaming"/> while deltas accumulate; the completed block replaces the
/// accumulated text (so the UI can swap a plain streaming block for a Markdown render).</summary>
internal sealed class TextPart : AssistantPart
{
    public string Text { get; internal set; } = "";
    public bool IsStreaming { get; internal set; } = true;
}

internal sealed class ThinkingPart(string text) : AssistantPart
{
    public string Text { get; } = text;
}

internal enum ToolCallStatus { Running, Done, Failed }

internal sealed class ToolCallPart(string toolUseId, string toolName, string summary, string inputJson) : AssistantPart
{
    public string ToolUseId { get; } = toolUseId;
    public string ToolName { get; } = toolName;
    public string Summary { get; } = summary;
    public string InputJson { get; } = inputJson;
    public ToolCallStatus Status { get; internal set; } = ToolCallStatus.Running;
    public string ResultPreview { get; internal set; } = "";
}

internal enum PermissionResolution { Pending, Allowed, Denied, Expired }

/// <summary>A <c>can_use_tool</c> prompt as a first-class item. The turn is paused until it resolves. When the
/// tool is <c>AskUserQuestion</c> (<see cref="IsQuestion"/>) it is Claude asking the user something, not a
/// permission — the UI shows a question card and "allows" with the answers folded into the input.</summary>
internal sealed class PermissionItem(PermissionRequestEvent request) : ConversationItem
{
    public PermissionRequestEvent Request { get; } = request;
    public PermissionResolution Resolution { get; internal set; } = PermissionResolution.Pending;
    /// <summary>The mode the user switched to when allowing (the CLI's suggestion), if any.</summary>
    public string? SwitchedMode { get; internal set; }
    public bool IsQuestion => Request.ToolName == AskUserQuestionInput.ToolName;
    /// <summary>For a question: what was answered, as a one-line receipt.</summary>
    public string? AnswerSummary { get; internal set; }
}

/// <summary>
/// The message/turn model behind the rich session UI: folds the flat <see cref="SessionEvent"/> stream
/// (plus the user's own prompts and answers) into composed <see cref="ConversationItem"/>s, and tracks the
/// session-level state a header shows (id, model, mode, cost, running/queued turns, the pending permission).
/// UI-free and deterministic so the grouping rules are unit-testable; the window binds one control per item
/// and re-syncs an item's parts on <see cref="ConversationChange.Updated"/>. Single-threaded: call from the
/// UI thread (the controller's events are marshalled there by the window).
/// </summary>
internal sealed class SessionConversation
{
    private readonly List<ConversationItem> _items = new();
    private readonly Dictionary<string, (AssistantMessageItem Owner, ToolCallPart Part)> _toolCalls = new();

    public IReadOnlyList<ConversationItem> Items => _items;

    /// <summary>An item was appended or changed in place.</summary>
    public event Action<ConversationItem, ConversationChange>? Changed;

    /// <summary>The whole item list changed shape (history was loaded in front of the live items) — rebuild.</summary>
    public event Action? Reset;

    /// <summary>Session-level state (id/model/mode/cost/turn bookkeeping) changed.</summary>
    public event Action? StateChanged;

    public string? SessionId { get; private set; }
    public string Model { get; private set; } = "";
    public string PermissionMode { get; private set; } = "default";
    public int ToolCount { get; private set; }
    public IReadOnlyList<string> SlashCommands { get; private set; } = [];
    /// <summary>Cumulative session cost (the CLI's <c>total_cost_usd</c> is a running total).</summary>
    public double TotalCostUsd { get; private set; }
    public TurnResultEvent? LastTurn { get; private set; }

    /// <summary>The most recent turn's prompt size (all input buckets summed) — the session's current
    /// context occupancy, to weigh against the model's context window. Zero until the first turn lands.</summary>
    public long ContextTokens { get; private set; }
    /// <summary>Cumulative output tokens the model has generated across the session.</summary>
    public long TotalOutputTokens { get; private set; }
    /// <summary>Cumulative input tokens actually re-billed across the session (fresh input + cache writes;
    /// cache reads excluded, since those bill at ~0.1×). The honest "tokens in" figure.</summary>
    public long TotalFreshInputTokens { get; private set; }
    /// <summary>A turn is running (prompt sent, no result yet).</summary>
    public bool TurnActive { get; private set; }
    /// <summary>Prompts sent while a turn was running; the CLI drains them in order.</summary>
    public int QueuedPrompts { get; private set; }
    public PermissionItem? PendingPermission { get; private set; }

    /// <summary>Seeds the context occupancy from a resumed session's transcript, so the context gauge reads
    /// true before the first new turn lands (the CLI replays no usage on <c>--resume</c>). A live turn's own
    /// figure supersedes it. No-op for a non-positive count.</summary>
    public void SeedContextTokens(long tokens)
    {
        if (tokens <= 0 || ContextTokens > 0) return;
        ContextTokens = tokens;
        StateChanged?.Invoke();
    }

    /// <summary>Seeds the identity before <c>init</c> arrives (the id is known at launch when pinned).</summary>
    public void SetSessionId(string? sessionId)
    {
        SessionId = sessionId;
        StateChanged?.Invoke();
    }

    public void Apply(SessionEvent ev)
    {
        switch (ev)
        {
            case SessionInitEvent init:
            {
                // A second init with a *different* id means the session was re-based mid-process — the CLI's
                // response to `/clear` (a fresh session id + wiped context). The id is seeded before the first
                // init and matches it, so a genuine first init never trips this. Reset the thread to match the
                // now-empty conversation. (The controller separately migrates the lock + registry to the new id.)
                bool cleared = SessionId is { Length: > 0 } prev && init.SessionId.Length > 0 && init.SessionId != prev;
                SessionId = init.SessionId;
                Model = init.Model;
                if (init.PermissionMode.Length > 0) PermissionMode = init.PermissionMode;
                ToolCount = init.ToolCount;
                SlashCommands = init.SlashCommands ?? [];
                if (cleared) ClearForNewConversation();
                StateChanged?.Invoke();
                break;
            }

            case TextDeltaEvent delta:
            {
                var owner = OpenAssistant();
                if (owner.Last is TextPart { IsStreaming: true } streaming) streaming.Text += delta.Text;
                else owner.Add(new TextPart { Text = delta.Text });
                Changed?.Invoke(owner, ConversationChange.Updated);
                break;
            }

            case AssistantTextEvent text:
            {
                var owner = OpenAssistant();
                if (owner.Last is TextPart { IsStreaming: true } streaming)
                {
                    streaming.Text = text.Text;
                    streaming.IsStreaming = false;
                }
                else owner.Add(new TextPart { Text = text.Text, IsStreaming = false });
                Changed?.Invoke(owner, ConversationChange.Updated);
                break;
            }

            case AssistantThinkingEvent thinking:
            {
                var owner = OpenAssistant();
                FreezeStreaming(owner);   // a new block starting means the streamed one has ended
                owner.Add(new ThinkingPart(thinking.Text));
                Changed?.Invoke(owner, ConversationChange.Updated);
                break;
            }

            case ToolUseEvent tool:
            {
                var owner = OpenAssistant();
                FreezeStreaming(owner);
                var part = new ToolCallPart(tool.ToolUseId, tool.ToolName, tool.Summary, tool.InputJson);
                owner.Add(part);
                if (tool.ToolUseId.Length > 0) _toolCalls[tool.ToolUseId] = (owner, part);
                Changed?.Invoke(owner, ConversationChange.Updated);
                break;
            }

            case ToolResultEvent result:
                if (_toolCalls.Remove(result.ToolUseId, out var call))
                {
                    call.Part.Status = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Done;
                    call.Part.ResultPreview = result.Preview;
                    Changed?.Invoke(call.Owner, ConversationChange.Updated);
                }
                break;

            case PermissionRequestEvent request:
            {
                var item = new PermissionItem(request);
                PendingPermission = item;
                Append(item);
                StateChanged?.Invoke();
                break;
            }

            case ModeChangedEvent mode:
                if (mode.Mode != PermissionMode)
                {
                    PermissionMode = mode.Mode;
                    Append(new NoteItem($"permission mode → {mode.Mode}"));
                }
                StateChanged?.Invoke();
                break;

            case TurnResultEvent turn:
            {
                // Close every open assistant item of this turn — a permission prompt splits a turn into two
                // items, and both belong to the result that just arrived. A block still streaming when the
                // turn ends is what we have; freeze it.
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (_items[i] is UserMessageItem) break;   // the previous turn's boundary
                    if (_items[i] is not AssistantMessageItem { IsComplete: false } open) continue;
                    FreezeStreaming(open);
                    open.IsComplete = true;
                    open.Result = turn;
                    Changed?.Invoke(open, ConversationChange.Updated);
                }
                LastTurn = turn;
                if (turn.CostUsd > 0) TotalCostUsd = turn.CostUsd;
                if (turn.ContextTokens > 0) ContextTokens = turn.ContextTokens;   // latest prompt = current occupancy
                TotalOutputTokens += turn.OutputTokens;
                TotalFreshInputTokens += turn.FreshInputTokens;
                if (QueuedPrompts > 0) QueuedPrompts--; else TurnActive = false;
                if (turn.IsError) Append(new NoteItem($"turn failed ({turn.Subtype})", NoteKind.Error));
                StateChanged?.Invoke();
                break;
            }
        }
    }

    /// <summary>
    /// Loads a resumed session's past conversation from its transcript (<c>{sessionId}.jsonl</c> lines) in
    /// front of whatever live items exist — the CLI replays nothing on <c>--resume</c>, only <c>init</c>.
    /// Transcript records share the stream-json record shapes, so assistant/tool lines go through
    /// <see cref="StreamJsonParser"/>; genuine user prompts (plain text, not tool results, not slash-command
    /// echoes) become user items. Everything loaded is closed (no turn bookkeeping) and <see cref="Reset"/>
    /// is raised once. Returns how many items were added. Never throws on a bad line.
    /// </summary>
    public int LoadHistory(IEnumerable<string> transcriptLines)
    {
        var scratch = new SessionConversation();
        foreach (var line in transcriptLines)
        {
            try { scratch.ApplyTranscriptLine(line); }
            catch { /* a malformed line is skipped, never fatal */ }
        }
        foreach (var item in scratch._items)
            if (item is AssistantMessageItem { IsComplete: false } a)
            {
                FreezeStreaming(a);
                a.IsComplete = true;
            }
        foreach (var (_, part) in scratch._toolCalls.Values)
            if (part.Status == ToolCallStatus.Running) part.Status = ToolCallStatus.Done;   // result not recorded

        if (scratch._items.Count == 0) return 0;
        _items.InsertRange(0, scratch._items);
        Reset?.Invoke();
        return scratch._items.Count;
    }

    private void ApplyTranscriptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (System.Text.Json.Nodes.JsonNode.Parse(line) is not { } root) return;
        if (root["isSidechain"]?.GetValue<bool>() == true) return;   // a sub-agent's line, not this thread
        if (TranscriptJson.AsString(root["type"]) == "user" && GenuineUserPrompt(root) is { } prompt)
        {
            Append(new UserMessageItem(prompt));
            return;
        }
        foreach (var ev in StreamJsonParser.Parse(line)) Apply(ev);
    }

    // A typed prompt: string content, or an array with text blocks and no tool_result — minus slash-command
    // echoes (<command-name>…) and their captured stdout, which the TUI shows but a reader shouldn't.
    private static string? GenuineUserPrompt(System.Text.Json.Nodes.JsonNode root)
    {
        if (root["isMeta"]?.GetValue<bool>() == true) return null;
        var content = root["message"]?["content"];
        string? text = TranscriptJson.AsString(content);
        if (text is null && content is System.Text.Json.Nodes.JsonArray arr)
        {
            if (arr.Any(b => TranscriptJson.BlockType(b) == "tool_result")) return null;
            text = string.Join("\n", arr
                .Where(b => TranscriptJson.BlockType(b) == "text")
                .Select(b => TranscriptJson.AsString(b?["text"]))
                .Where(t => !string.IsNullOrEmpty(t)));
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<command-name>", StringComparison.Ordinal)
            || trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
            || trimmed.StartsWith("<local-command-caveat>", StringComparison.Ordinal)
            || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
            return null;
        return text;
    }

    /// <summary>Records a prompt the user sent (the controller already wrote it to the CLI).</summary>
    public void AddUserPrompt(string text)
    {
        Append(new UserMessageItem(text));
        if (TurnActive) QueuedPrompts++; else TurnActive = true;
        StateChanged?.Invoke();
    }

    public void AddNote(string text, NoteKind kind = NoteKind.Info) => Append(new NoteItem(text, kind));

    // `/clear` re-based the session onto a new id with wiped context: drop every conversation item and the
    // per-conversation turn/context bookkeeping, leaving a marker, and rebuild the view via Reset. Cumulative
    // process-level spend (cost, total tokens) is kept — it's the same process and billing window.
    private void ClearForNewConversation()
    {
        _items.Clear();
        _toolCalls.Clear();
        PendingPermission = null;
        TurnActive = false;
        QueuedPrompts = 0;
        LastTurn = null;
        ContextTokens = 0;
        _items.Add(new NoteItem("conversation cleared"));
        Reset?.Invoke();
    }

    /// <summary>Marks the pending permission answered (the controller already replied to the CLI).</summary>
    public void ResolvePermission(PermissionItem item, bool allowed, string? switchedMode = null, string? answerSummary = null)
    {
        if (item.Resolution != PermissionResolution.Pending) return;
        item.Resolution = allowed ? PermissionResolution.Allowed : PermissionResolution.Denied;
        item.SwitchedMode = allowed ? switchedMode : null;
        item.AnswerSummary = answerSummary;
        if (ReferenceEquals(PendingPermission, item)) PendingPermission = null;
        Changed?.Invoke(item, ConversationChange.Updated);
        StateChanged?.Invoke();
    }

    /// <summary>The process ended: settle every in-flight thing so the UI shows a quiet, closed state.</summary>
    public void SessionEnded(int exitCode, string stderrTail)
    {
        if (PendingPermission is { } pending)
        {
            pending.Resolution = PermissionResolution.Expired;
            PendingPermission = null;
            Changed?.Invoke(pending, ConversationChange.Updated);
        }
        foreach (var (owner, part) in _toolCalls.Values)
        {
            if (part.Status == ToolCallStatus.Running) part.Status = ToolCallStatus.Failed;
            Changed?.Invoke(owner, ConversationChange.Updated);
        }
        _toolCalls.Clear();
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i] is AssistantMessageItem { IsComplete: false } a)
            {
                FreezeStreaming(a);
                a.IsComplete = true;
                Changed?.Invoke(a, ConversationChange.Updated);
            }
        TurnActive = false;
        QueuedPrompts = 0;
        Append(new NoteItem(
            exitCode == 0 ? "session ended" : $"claude exited ({exitCode}) {stderrTail}".TrimEnd(),
            exitCode == 0 ? NoteKind.Info : NoteKind.Error));
        StateChanged?.Invoke();
    }

    // The assistant item events accumulate into: the trailing open one, else a fresh one. A user message or a
    // permission prompt in between starts a new assistant item, which is what makes turns read as units.
    private AssistantMessageItem OpenAssistant()
    {
        if (_items.Count > 0 && _items[^1] is AssistantMessageItem { IsComplete: false } open) return open;
        var item = new AssistantMessageItem();
        Append(item);
        return item;
    }

    private static void FreezeStreaming(AssistantMessageItem item)
    {
        foreach (var part in item.Parts)
            if (part is TextPart { IsStreaming: true } t) t.IsStreaming = false;
    }

    private void Append(ConversationItem item)
    {
        _items.Add(item);
        Changed?.Invoke(item, ConversationChange.Added);
    }
}
