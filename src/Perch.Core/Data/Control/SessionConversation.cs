namespace Perch.Data.Control;

/// <summary>How a <see cref="SessionConversation"/> item changed, for incremental UI updates.</summary>
internal enum ConversationChange { Added, Updated }

/// <summary>One composed unit in the conversation: a user message, an assistant message (with its ordered
/// parts), a permission prompt, or a system note. The rich session UI binds one control per item.</summary>
internal abstract class ConversationItem;

internal sealed class UserMessageItem(string text, IReadOnlyList<MessageAttachment>? attachments = null) : ConversationItem
{
    public string Text { get; } = text;

    /// <summary>Files/images the user attached to this message (empty when none). Display-only — the file
    /// paths and image content blocks are already on their way to the CLI by the time this is recorded.</summary>
    public IReadOnlyList<MessageAttachment> Attachments { get; } = attachments ?? [];
}

internal enum NoteKind { Info, Error }

/// <summary>A quiet system line: "permission mode → plan", "session ended", a launch failure.</summary>
internal sealed class NoteItem(string text, NoteKind kind = NoteKind.Info) : ConversationItem
{
    public string Text { get; } = text;
    public NoteKind Kind { get; } = kind;
}

/// <summary>A <c>/compact</c> in progress (and then complete): a live row with a progress meter. The CLI
/// runs compaction as a turn and streams <c>status</c> records; <see cref="Percent"/> follows them when a
/// figure can be read, and the UI always shows its own elapsed timer from <see cref="StartedUtc"/> so it
/// reads as progress even when no percentage arrives. Finalised by the compaction turn's result.</summary>
internal sealed class CompactionItem(string? instructions) : ConversationItem
{
    public string? Instructions { get; } = instructions;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    /// <summary>Completion percentage from the CLI's status records, or null → indeterminate (timer-driven).</summary>
    public int? Percent { get; internal set; }
    public bool IsDone { get; internal set; }
    /// <summary>The compaction was interrupted or errored (nothing reclaimed) — a canceled state, not a
    /// green success. Set when finalising if the user interrupted, the turn errored, or no context was freed.</summary>
    public bool Failed { get; internal set; }
    /// <summary>Context tokens reclaimed (before − after), known only once the turn's result lands.</summary>
    public long FreedTokens { get; internal set; }
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
    /// <summary>The tool's result as text (capped at ~8k chars), or empty until it lands. The card shows a
    /// one-line summary of it collapsed and the whole thing on expand.</summary>
    public string ResultText { get; internal set; } = "";
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
    /// <summary>True when this is an <c>ExitPlanMode</c> approval — Claude presenting a plan to carry out, not
    /// a tool-run permission. The UI shows a plan-approval card.</summary>
    public bool IsPlan => Request.ToolName == PlanApprovalInput.ToolName;
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

    // The session id a history load is for, so an image-bearing user line can resolve its cached image files
    // (~/.claude/image-cache/{sessionId}/{N}.ext). Set on the scratch conversation in LoadHistory; null live.
    private string? _historySessionId;

    // The /compact currently running (its progress row), and the context size just before it, so the freed
    // amount can be reported when the compaction turn's result lands. Null when no compaction is in flight.
    private CompactionItem? _activeCompaction;
    private long _contextBeforeCompaction;
    private bool _compactionInterrupted;
    // Set by a compact_boundary so the compact turn's own (misleadingly large) result can't overwrite the
    // corrected post-compaction occupancy; consumed by the next result, and cleared by the next user prompt.
    private bool _suppressNextResultContext;

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
    /// <summary>The MCP servers the CLI reported at init (name + status), backing the <c>/mcp</c> view.</summary>
    public IReadOnlyList<McpServerInfo> McpServers { get; private set; } = [];
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
                if (init.McpServers is { Count: > 0 }) McpServers = init.McpServers;
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
                    call.Part.ResultText = result.Text;
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

            case StatusEvent status:
                // Only meaningful while a /compact is running: advance its meter when a percentage is read.
                if (_activeCompaction is { IsDone: false } running && status.Percent is int pct)
                {
                    running.Percent = pct;
                    Changed?.Invoke(running, ConversationChange.Updated);
                }
                break;

            case CompactionCompletedEvent done:
            {
                // The authoritative success signal (only emitted when a compaction actually completed): settle
                // the progress row with the exact freed amount and correct the context occupancy to the post
                // size. Turn bookkeeping is left to the turn's own `result`; but that result reports the
                // summarisation's (large) input, which would clobber the corrected occupancy — so suppress the
                // next result's context update. (New user prompts clear the flag, so it can't linger.)
                if (done.PostTokens > 0) { ContextTokens = done.PostTokens; _suppressNextResultContext = true; }
                long freed = Math.Max(0, done.PreTokens - done.PostTokens);
                if (_activeCompaction is { IsDone: false } compaction)
                {
                    compaction.IsDone = true;
                    compaction.Failed = false;
                    compaction.Percent = 100;
                    compaction.FreedTokens = freed;
                    Changed?.Invoke(compaction, ConversationChange.Updated);
                    _activeCompaction = null;
                    _compactionInterrupted = false;
                    // The boundary IS the compaction turn's completion — a manual /compact that then idles may
                    // emit no `result`, so settle the turn here (unless prompts were queued behind it, which the
                    // result drains). Otherwise TurnActive stays stuck "working…" and the next prompt queues
                    // behind a phantom turn, leaving the composer's context/token pills stale. A later result
                    // for this turn is idempotent (it sets TurnActive false again when the queue is empty).
                    if (QueuedPrompts == 0) TurnActive = false;
                }
                else
                {
                    // No progress row — this is the CLI's own auto-compaction near the limit, which the user
                    // didn't initiate. Leave a quiet note so the context drop isn't a mystery.
                    Append(new NoteItem(freed > 0
                        ? $"context auto-compacted by Claude Code — freed {freed:N0} tokens"
                        : "context auto-compacted by Claude Code"));
                }
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

            case AssistantUsageEvent usage:
                // The newest single message's prompt size is the current context occupancy. Honour the
                // compaction suppression flag: the summarisation message's usage is the (large) pre-compaction
                // context, and a following compact_boundary sets the true post size — don't let a late
                // summary message clobber it.
                if (usage.ContextTokens > 0 && !_suppressNextResultContext)
                {
                    ContextTokens = usage.ContextTokens;
                    StateChanged?.Invoke();
                }
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
                // Occupancy is normally NOT read from the result: its usage aggregates every round-trip in the
                // turn, so an agentic turn's result reads as a multiple of the real prompt size. The per-message
                // AssistantUsageEvent carries the honest occupancy instead. The sole exception is a compaction
                // turn that emitted no compact_boundary — there the result is the only post-compaction size we
                // get, so let it set occupancy (which also drives FreedTokens below). A boundary, when present,
                // already set the true post size and raised the suppress flag, so this is skipped then.
                if (_activeCompaction is { IsDone: false } && turn.ContextTokens > 0 && !_suppressNextResultContext)
                    ContextTokens = turn.ContextTokens;
                _suppressNextResultContext = false;
                // The result still supplies the cumulative session totals (per-turn amounts, correct to sum).
                TotalOutputTokens += turn.OutputTokens;
                TotalFreshInputTokens += turn.FreshInputTokens;
                if (QueuedPrompts > 0) QueuedPrompts--; else TurnActive = false;
                // A compaction still active at the result normally means its `compact_boundary` (the success
                // signal, handled above) hasn't been seen — so judge it only by hard signals: the user
                // interrupted, or the turn errored. A clean result settles it as done either way. NEVER infer
                // failure from freed tokens — a successful compaction's result doesn't necessarily report a
                // reduced context, which is exactly what made a real success look like a failure.
                if (_activeCompaction is { IsDone: false } compaction)
                {
                    compaction.IsDone = true;
                    compaction.FreedTokens = Math.Max(0, _contextBeforeCompaction - ContextTokens);
                    compaction.Failed = _compactionInterrupted || turn.IsError;
                    if (!compaction.Failed) compaction.Percent = 100;
                    Changed?.Invoke(compaction, ConversationChange.Updated);
                    _activeCompaction = null;
                    _compactionInterrupted = false;
                }
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
    public int LoadHistory(IEnumerable<string> transcriptLines, string? sessionId = null)
    {
        var scratch = new SessionConversation { _historySessionId = sessionId };
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
            // A resumed message that carried a pasted image: recover the image as an attachment. The "[Image #N]"
            // placeholder is kept in the text (it gives context, and the view makes the token itself hover/click
            // to the image) — the k-th token pairs with the k-th image attachment.
            var images = TranscriptImages.Extract(TranscriptJson.ContentArray(root), prompt, _historySessionId);
            Append(new UserMessageItem(prompt, images.Count > 0 ? images : null));
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

    /// <summary>Every user prompt in the thread, oldest first, for the composer's ↑/↓ input-history recall.
    /// Includes resumed history (it reads the same <see cref="UserMessageItem"/>s) and skips blank entries
    /// (an image-only message whose text stripped to nothing).</summary>
    public IReadOnlyList<string> UserPromptHistory() =>
        _items.OfType<UserMessageItem>()
              .Select(u => u.Text)
              .Where(t => !string.IsNullOrWhiteSpace(t))
              .ToList();

    /// <summary>Records a prompt the user sent (the controller already wrote it to the CLI), with any
    /// attachments for display.</summary>
    public void AddUserPrompt(string text, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        Append(new UserMessageItem(text, attachments));
        _suppressNextResultContext = false;   // a genuine new turn: its result's context is real again
        if (TurnActive) QueuedPrompts++; else TurnActive = true;
        // `/compact` runs as an ordinary turn, but its effect is to shrink the context rather than to answer,
        // so drop a marker that explains the upcoming context drop — the CLI itself emits only progress
        // `status` system records, which the parser ignores (see docs/slash-command-actions.md, Group B). The
        // optional [instructions] ride through in the sent text; echo them so the marker says what was kept.
        // Context/usage figures self-correct on the compaction turn's result (ContextTokens = latest prompt).
        // `/compact` runs as an ordinary turn, but its effect is to shrink the context rather than to answer,
        // so it becomes a live progress row (the CLI streams status while it works; the row's meter follows
        // and its result finalises it below). Snapshot the context size to report how much was freed.
        // (`/autocompact` is a native command handled by SessionWindow — it opens the auto-compaction modal.)
        if (SlashCommandCatalog.CommandName(text) == "compact")
        {
            var item = new CompactionItem(CompactInstructions(text));
            _activeCompaction = item;
            _contextBeforeCompaction = ContextTokens;
            _compactionInterrupted = false;
            Append(item);
        }
        StateChanged?.Invoke();
    }

    // The text after `/compact` (the summarisation instructions), or null when none were given.
    private static string? CompactInstructions(string text)
    {
        var t = text.TrimStart();
        int sp = t.IndexOf(' ');
        if (sp < 0) return null;
        var rest = t[(sp + 1)..].Trim();
        return rest.Length > 0 ? rest : null;
    }

    public void AddNote(string text, NoteKind kind = NoteKind.Info) => Append(new NoteItem(text, kind));

    /// <summary>The user asked to interrupt the turn. If a <c>/compact</c> is running, remember it so its
    /// progress row settles to a canceled state (not a green success) when the aborted result lands.</summary>
    public void NoteInterrupt()
    {
        if (_activeCompaction is { IsDone: false }) _compactionInterrupted = true;
    }

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
        _activeCompaction = null;
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
        // A compaction still in flight didn't get its result — settle its meter (as canceled) so it doesn't
        // spin forever.
        if (_activeCompaction is { IsDone: false } compaction)
        {
            compaction.IsDone = true;
            compaction.Failed = true;
            Changed?.Invoke(compaction, ConversationChange.Updated);
            _activeCompaction = null;
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
