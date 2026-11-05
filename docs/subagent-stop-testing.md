# Stopped sub-agents leave the roster at once — manual test

When a parent session stops its own background sub-agents (TaskStop), Perch should drop them from the session row and the session window's running chips as soon as the terminal shows them stopped. Before this fix they lingered for about 90 seconds.

## Why it lingered

TaskStop rejects the agent's pending tool call and appends a `[Request interrupted by user for tool use]` (or `[Request interrupted by user]`) record to `subagents/agent-{id}.jsonl`. Unlike a normal finish, it does **not** rewrite `agent-{id}.meta.json` with `"finished": true` or `"stoppedByUser": true`. `SubAgentReader` read that trailing user record as "awaiting the model" (working), and with no end flag the only way out was the 90s staleness window.

The fix treats an interrupt marker at the tail of an agent transcript as an immediate stop (classified like a stale agent, so an interrupted teammate still skips the "done" alert).

## Steps

1. Run the tray from this branch: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.
2. In a Claude Code session, paste:

   > Launch three background general-purpose agents in parallel. Each one should run `Start-Sleep -Seconds 300` in PowerShell (timeout 600000) and then report "done". Don't wait for them.

3. Confirm Perch shows three running sub-agents on that session row (and as chips in the session window if it's open).
4. Paste:

   > Stop all three background agents now with TaskStop.

5. **Expected (after):** the sub-agents disappear from Perch within a scan or two of the terminal reporting "Successfully stopped task". **Before:** they stayed for roughly 90 seconds.
6. Resume check: paste

   > Send one of the stopped agents a message asking it to reply "back".

   It should reappear as running while it works, then drop off when it finishes.
