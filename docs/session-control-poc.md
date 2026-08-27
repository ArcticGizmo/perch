# Perch-controlled sessions — PoC investigation (ConPTY vs Claude Agent SDK)

**Branch:** `session-control-poc` · **Status:** investigation + working PoC code (stream-json path) ·
**Verified against:** claude CLI 2.1.247 on Windows 11, 2026-08-27.

Perch today is an *observer*: it reads `~/.claude` sidecars and transcripts that other people's
terminals produce. This PoC investigates Perch *owning* sessions — launching Claude Code itself,
sending prompts, answering permission prompts, and rendering a rich, Claude-Desktop-like conversation
in a Perch window. Two candidate mechanisms were investigated:

1. **ConPTY** — embed a real terminal in a Perch window and run the interactive `claude` TUI in it.
2. **Claude Agent SDK / stream-json** — drive the CLI headlessly over its bidirectional JSON protocol
   (the same wire protocol the official Python/TypeScript Agent SDKs wrap) and render the
   conversation ourselves.

**TL;DR: they solve different halves of the request, and the stream-json protocol is the right
foundation.** "A rich text experience from within a Perch-controlled window" is exactly what
stream-json provides, cheaply and cross-platform — and the PoC on this branch has it working
end-to-end (permissions included). "Full control over their terminals" — a real embedded terminal
with the actual Claude Code TUI — is only reachable via ConPTY plus a terminal-emulator control,
which is viable (good 2025-era Avalonia options exist) but is a medium-sized dependency-and-fidelity
project, not a protocol problem. VS Code and JetBrains ship both halves side by side; Perch can too,
starting with the rich pane.

---

## 1. The stream-json path (Agent SDK protocol) — verified working

### What it is

There is **no official .NET Agent SDK** (Python and TypeScript only; the `Anthropic` NuGet package is
the raw messages API, not the agent harness). But the SDKs are wrappers around the CLI's documented
headless interface, which any language can speak:

```
claude -p --input-format stream-json --output-format stream-json --verbose \
         --include-partial-messages --permission-prompt-tool stdio
```

One process = one session = many turns. JSON-lines out on stdout, JSON-lines in on stdin. Everything
below was **verified live on this machine** (captures in the sections below; the raw session logs from
the verification runs were kept in the session scratchpad).

### Verified capabilities (claude 2.1.247, Windows, redirected stdio)

| Capability | Status | Notes |
|---|---|---|
| Spawn + one-shot prompt, parse output | ✅ verified | needs `--verbose` with `-p` + stream-json |
| Send user messages on stdin (multi-turn, one process) | ✅ verified | `{"type":"user","message":{...}}` |
| Rich output: text / thinking / tool_use / tool_result blocks, per-block messages | ✅ verified | plus token usage + model on every assistant record |
| Streaming text deltas | ✅ verified | `--include-partial-messages` → `stream_event` records |
| Control-protocol handshake | ✅ verified | `initialize` control request → capability catalogue back |
| **Interactive permission prompts** | ✅ verified | `--permission-prompt-tool stdio` → `can_use_tool` control requests; allow/deny responses honoured (file write observed) |
| Permission-mode switch mid-session | ✅ verified | `set_permission_mode` control request, acked, subsequent Write ran unprompted |
| Interrupt | ⚠️ implemented, not yet exercised | same control-request shape per the Agent SDK protocol |
| Cost/usage per turn | ✅ verified | `result` record: `total_cost_usd`, tokens, duration, `permission_denials` |
| Writes the normal `~/.claude` files | ✅ verified | transcript JSONL appears under `projects/{enc-cwd}/`; SessionStart hooks fire (perch-hook included), so existing observers keep working |
| Resume/fork | present (`--resume`, `--session-id`, fork) | not exercised in the PoC; sessions Perch spawns are resumable in a terminal later |
| ANSI noise in the JSON | none observed | UTF-8 in/out; parse was clean |

### The wire shapes (as actually observed — docs and SDK reports differ in places)

Output records (one JSON object per stdout line, `type` discriminated):

- `{"type":"system","subtype":"init","session_id":…,"model":…,"permissionMode":…,"tools":[…],"slash_commands":[…],…}`
- `{"type":"assistant","message":{"content":[{"type":"text"|"thinking"|"tool_use",…}],"usage":{…},"model":…},…}`
  — one record **per content block**, each carrying usage.
- `{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":…,"content":…,"is_error":…}]}}`
- `{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":…}}}`
- `{"type":"control_request","request_id":…,"request":{"subtype":"can_use_tool","tool_name":…,"input":{…},"description":…,"permission_suggestions":[{"type":"setMode","mode":"acceptEdits",…}],"tool_use_id":…}}`
- `{"type":"control_response","response":{"subtype":"success","request_id":…,"response":{…}}}` (acks for our control requests)
- `{"type":"result","subtype":"success","is_error":false,"total_cost_usd":…,"usage":{…},"permission_denials":[…],…}` per turn
- plus ignorable chatter under `--verbose`: hook lifecycle records, `rate_limit_event`, `thinking_tokens` estimates.

Input records (stdin, one per line):

```json
{"type":"user","message":{"role":"user","content":[{"type":"text","text":"…"}]}}
{"type":"control_request","request_id":"perch_req_1","request":{"subtype":"initialize"}}
{"type":"control_request","request_id":"perch_req_2","request":{"subtype":"set_permission_mode","mode":"acceptEdits"}}
{"type":"control_request","request_id":"perch_req_3","request":{"subtype":"interrupt"}}
{"type":"control_response","response":{"subtype":"success","request_id":"<their id>","response":{"behavior":"allow","updatedInput":{…}}}}
{"type":"control_response","response":{"subtype":"success","request_id":"<their id>","response":{"behavior":"deny","message":"…"}}}
```

Gotchas learned the hard way:

- `-p --output-format stream-json` **requires `--verbose`** or the CLI exits with an error.
- Without `--permission-prompt-tool stdio`, tools needing permission are **silently denied** (they land
  in `result.permission_denials`) — no control request is ever sent.
- The `can_use_tool` allow-response must echo the tool input back as `updatedInput` (which also means a
  controlling UI may *edit* it — a future affordance the TUI doesn't have).
- Third-party write-ups (and even the SDK docs) describe several of these shapes differently
  (`{"type":"message","subtype":"assistant"}`, `{"decision":"approve"}` etc.). The shapes above are
  what 2.1.247 actually speaks; keep the parser fixture-tested against captures.

### Limitations of this path

- **Cannot attach to an existing terminal session.** Control requires owning the process. The bridge
  is `--resume <sessionId>`: Perch can offer "continue this session in a Perch window" by resuming an
  observed session id into a controlled process (and the reverse hand-off back to a terminal works the
  same way). Not exercised in this PoC.
- The control protocol is what the official SDKs ride on, so it's real and load-bearing — but it is not
  a formally versioned public contract; the `init` record advertises `capabilities` worth checking at
  runtime. Pin expectations with parser tests.
- Slash commands/TUI affordances don't exist here; rich equivalents must be built (mode switching is
  already done via control requests).

## 2. The ConPTY path — viable, bigger, complementary

ConPTY (`CreatePseudoConsole`, Win10 1809+) is the plumbing every real Windows terminal host uses.
Perch would spawn `claude` attached to a pseudo-console and get two byte streams — then must **render
the VT output and synthesise VT input itself**. That means a terminal-emulator control, which is the
actual cost:

- **Best current option:** `Iciclecreek.Avalonia.Terminal` (MIT, active 2025) — an Avalonia
  `TerminalControl` over **XTerm.NET** (full VT engine: alt-screen, truecolor, mouse, scrollback) and
  **Porta.Pty** (maintained cross-platform ConPTY/PTY wrapper — also helps the macOS head, where the
  same control works over openpty). `IvanJosipovic/AvaloniaTerminal` is a comparable alternative on
  the same engine. Single-author ecosystem is the main risk.
- Microsoft's `Pty.Net` is dead (one 2018 pre-release, unlisted); the Windows Terminal control was
  never productized (`Microsoft.Terminal.Wpf` is VS-internal); re-parenting a conhost HWND via
  `SetParent` is a demo trick, not shippable.
- ConPTY re-synthesises the viewport (it is not a byte-for-byte passthrough); scrollback is the host's
  job; for best fidelity ship the redistributable `conpty.dll`/`OpenConsole.exe` from Windows Terminal
  releases (MIT) like wezterm/alacritty do.
- Fits Perch's architecture as `IPtySessionHost` in `Perch.Core/Platform` + a
  `Perch.Platform.Windows` implementation (the P/Invoke shape matches `WindowActivator.cs`), or just
  take the Porta.Pty dependency.
- Effort: days-to-two-weeks for a credible embedded terminal using the off-the-shelf control;
  1–3 months if the control proves immature and we hand-roll rendering.

What ConPTY does **not** give: structure. The byte stream has no notion of turns, tools, permissions,
or cost — a "rich" experience on top of a PTY means scraping the TUI, which is the wrong tool. The
`perch-hook` stdout-handle-inheritance comment (`src/Perch.Hook/Program.cs:325`) is required reading
before any ConPTY work — same hazard class, inverted.

## 3. Comparison

| | ConPTY + terminal control | stream-json control protocol |
|---|---|---|
| User gets the *real* Claude Code TUI (keybindings, slash commands, pickers) | ✅ | ❌ (equivalents rebuilt in Perch UI) |
| Rich structured rendering (blocks, tool chips, cost, thinking) | ❌ scraping | ✅ native |
| Permission prompts in Perch UI | ❌ (typed into TUI) | ✅ verified, incl. suggestions + input editing |
| Programmatic control (queue prompts, interrupt, mode switch, automation) | ❌ keystroke injection | ✅ verified |
| Cross-platform | ⚠️ per-OS PTY (Porta.Pty covers it) | ✅ same protocol everywhere |
| New dependencies | terminal control + PTY lib (+ optional conpty redist) | none |
| Existing Perch observers keep working | ✅ | ✅ verified (hooks fire, transcript written) |
| Attach to session Perch didn't start | ❌ | ❌ (but `--resume` hands off both directions) |
| Effort to first useful window | medium | small — **done in this PoC** |

**Recommendation.** Build controlled sessions on the stream-json protocol — it is the only path that
delivers the rich-pane experience and programmatic control, it needed zero new dependencies, and it's
the same seam the official SDKs use. Treat an embedded ConPTY terminal as a later, additive feature
(likely `Iciclecreek.Avalonia.Terminal` + Porta.Pty behind a Core interface) for users who want the
genuine TUI docked in Perch — or as a fallback surface *inside* the session console for the odd thing
the protocol can't do. If both ship, "full terminal control" and "rich text experience" become two
tabs over the same Perch-owned session (resume bridges between them).

## 4. What this PoC ships

- `src/Perch.Core/Data/Control/SessionEvents.cs` — the decoded event model.
- `src/Perch.Core/Data/Control/StreamJsonParser.cs` — stateless, defensive line decoder
  (fixture-tested in `tests/Perch.Tests/StreamJsonParserTests.cs` against captured 2.1.247 lines).
- `src/Perch.Core/Data/Control/ClaudeSessionController.cs` — process host: spawn (via `cmd /c`, since
  `claude` is a .cmd shim; `/bin/sh -lc` elsewhere), initialize handshake, prompt send, permission
  respond, mode switch, interrupt, stop-with-tree-kill. Events on a pump thread.
- `src/Perch.App/Windows/SessionConsoleWindow.cs` — the rich window: folder/model/mode pickers,
  streamed assistant text, dimmed thinking, tool chips (`▸ Editing Foo.cs … ✓`/error), inline
  permission bar (Allow / Allow+acceptEdits when suggested / Deny), live mode switcher, interrupt,
  per-turn cost line. Opened from the tray: **"Session console (PoC)…"**.

Deliberate PoC cuts: no markdown rendering of assistant text; no session resume/fork UI; interrupt
unexercised; tool-input editing before allow not surfaced; no `AskUserQuestion`/plan-approval special
casing; the spawned session appears in the overlay as an ordinary CLI session, so clicking it there
tries to focus a terminal that doesn't exist ("No window to focus") — production would route focus to
the console window and tag Perch-owned sessions.

## 5. Suggested next steps

1. Exercise interrupt + `--resume` hand-off (both directions) and pin them with captures.
2. Markdown-render assistant text (the markdown-viewer feature already has the pieces).
3. Route overlay clicks for Perch-owned sessions to the console window; add a "Continue in Perch"
   action on observed idle sessions (resume).
4. Surface `AskUserQuestion`/plan approval as first-class UI (they arrive as tool_use blocks).
5. Decide on the embedded-terminal tab: spike `Iciclecreek.Avalonia.Terminal` + Porta.Pty against the
   real `claude` TUI (mouse, alt-screen, resize) before committing.
