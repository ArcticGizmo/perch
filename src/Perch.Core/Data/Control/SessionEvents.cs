namespace Perch.Data.Control;

/// <summary>
/// The events a Perch-controlled Claude Code session surfaces to the UI, decoded from the CLI's
/// <c>--output-format stream-json</c> stream by <see cref="StreamJsonParser"/>. One line on stdout can
/// yield several events (an assistant message carries one event per content block). PoC scope: see
/// <c>docs/session-control-poc.md</c>.
/// </summary>
internal abstract record SessionEvent;

/// <summary>The <c>system/init</c> handshake: the session exists and identifies itself.
/// <paramref name="SlashCommands"/> is the CLI's advertised <c>slash_commands</c> list (empty when absent) —
/// the seed for the rich UI's command palette.</summary>
internal sealed record SessionInitEvent(
    string SessionId, string Model, string PermissionMode, int ToolCount,
    IReadOnlyList<string>? SlashCommands = null) : SessionEvent;

/// <summary>A completed assistant text block.</summary>
internal sealed record AssistantTextEvent(string Text) : SessionEvent;

/// <summary>A completed assistant thinking block.</summary>
internal sealed record AssistantThinkingEvent(string Text) : SessionEvent;

/// <summary>The assistant invoked a tool. <paramref name="Summary"/> is the shared
/// <see cref="ToolSummary"/> phrase ("Editing Foo.cs"); <paramref name="InputJson"/> is the raw tool input
/// (for the expandable arguments on a tool card).</summary>
internal sealed record ToolUseEvent(string ToolUseId, string ToolName, string Summary, string InputJson = "{}") : SessionEvent;

/// <summary>A tool finished; <paramref name="Preview"/> is a clipped, single-block text preview.</summary>
internal sealed record ToolResultEvent(string ToolUseId, string Preview, bool IsError) : SessionEvent;

/// <summary>A streaming text delta (only emitted with <c>--include-partial-messages</c>). Deltas
/// accumulate into the block a later <see cref="AssistantTextEvent"/> finalises.</summary>
internal sealed record TextDeltaEvent(string Text) : SessionEvent;

/// <summary>The CLI is asking permission to run a tool (<c>control_request</c> /
/// <c>can_use_tool</c>); it blocks until answered via
/// <see cref="ClaudeSessionController.RespondToPermission"/>. <paramref name="InputJson"/> is the raw
/// tool input (echoed back as <c>updatedInput</c> on allow); <paramref name="SuggestedMode"/> is the
/// CLI's first <c>setMode</c> suggestion (e.g. "acceptEdits"), when present.</summary>
internal sealed record PermissionRequestEvent(
    string RequestId, string ToolName, string Description, string InputJson, string? SuggestedMode) : SessionEvent;

/// <summary>Acknowledgement of a <c>set_permission_mode</c> control request.</summary>
internal sealed record ModeChangedEvent(string Mode) : SessionEvent;

/// <summary>A turn finished (the <c>result</c> record): outcome plus cost/usage for the whole session so far.
/// The three input buckets are kept apart because they price very differently — fresh
/// <paramref name="InputTokens"/> at 1×, <paramref name="CacheCreationTokens"/> at ~1.25×,
/// <paramref name="CacheReadTokens"/> at ~0.1× — and their sum is the prompt size sent to the model, i.e.
/// the session's current context occupancy.</summary>
internal sealed record TurnResultEvent(
    bool IsError, string Subtype, double CostUsd, long InputTokens, long OutputTokens, long DurationMs,
    long CacheReadTokens = 0, long CacheCreationTokens = 0) : SessionEvent
{
    /// <summary>The prompt size for this turn = every input bucket summed = current context occupancy.</summary>
    public long ContextTokens => InputTokens + CacheReadTokens + CacheCreationTokens;

    /// <summary>Input tokens actually re-billed (not served from cache): fresh input + cache writes.</summary>
    public long FreshInputTokens => InputTokens + CacheCreationTokens;
}
