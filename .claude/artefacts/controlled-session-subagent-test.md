# Controlled-session subagent visibility — test harness

Purpose: verify whether subagent (Task/Agent) activity surfaces for a **Perch-controlled**
(`claude -p` stream-json) session, in both the rich `SessionWindow` and the floating overlay.
See memory `controlled-session-subagents-gap` for the root-cause analysis.

## Test prompt (paste into a Perch-controlled session, cwd = the perch repo)

```
Using the Task tool, launch ONE general-purpose subagent and delegate the
following to it — do NOT do any of this yourself, hand it all to the subagent
and wait for it to finish before you reply:

1. Grep for "parent_tool_use_id" across src/ and list the matching files.
2. Glob "src/**/Session*.cs" and tell me how many files match.
3. Run these shell commands and include their raw output verbatim:
     git log --oneline -5
     git status --short
     sleep 5
     dotnet --version

When the subagent returns, give me a 3-line summary of what it found.
```

Shape rationale:
- Forces a real subagent (explicit "launch ONE general-purpose subagent via the Task tool").
- Produces observable child activity: read-only Grep/Glob **plus** a shell child; `sleep 5`
  guarantees a live child process/shell for ~5s — long enough for the overlay poll and the
  window to show it running if the plumbing works.
- Entirely read-only and safe; repeatable.

### Parallel / async variant (tests fan-out + background-agent path)

Swap line 1 for:

```
Using the Task tool, launch TWO subagents in parallel (one Explore, one
general-purpose) and delegate to them — do NOT do the work yourself:
```

and split the tasks between them.

## Capture setup

In the shell that launches the tray, before starting it:

```powershell
$env:PERCH_SESSION_LOG = "$env:TEMP\perch-session-stream.log"
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
```

`ClaudeSessionController` (src/Perch.Core/Data/Control/ClaudeSessionController.cs) reads
`PERCH_SESSION_LOG` and appends every controlled-session stdout line verbatim.

## What to check (the two open questions)

1. **Window / stream:** does `%TEMP%\perch-session-stream.log` contain `parent_tool_use_id`?
   - yes → the CLI streams subagent activity and Perch drops it → window-side fix
     (parse `parent_tool_use_id`, nest child events under the Task card).
   - no  → `-p` doesn't stream it → window must tail the `subagents/` dir instead.
2. **Overlay / disk:** during the run, does
   `~/.claude/projects/C--Users-JonHowell-Documents-git-personal-perch/{sessionId}/subagents/`
   populate with `agent-*.jsonl`?
   - yes → the overlay path *should* work (bug is elsewhere).
   - no  → `-p` doesn't write it → overlay needs the controlled session's in-memory
     subagent state published into `ControlledSessions`.

Also just watch the UI during the run: whether the Task card in the `SessionWindow` shows any
child activity, and whether the overlay row for the session shows subagent sub-rows.
