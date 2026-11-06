# Sub-agents show up as soon as they start — manual test

Perch should show a new sub-agent on its parent's session row within a second or two of Claude Code launching it, and drop it within a couple of seconds of it finishing. Before this fix, a terminal session's sub-agents could take up to 30 seconds to appear, so a short agent (a 35-second PR "skeptic", say) was barely visible or missed.

## Why it was late

Perch rescans on a file event in `sessions/`, a 30s reconcile timer, or a 2s poll that ran only while a Perch-controlled session was working. Claude Code 2.1.318 doesn't rewrite a terminal session's `sessions/{pid}.json` while it works: in the session that prompted this fix, the file sat untouched for five minutes while the session launched 11 agents. A sub-agent appearing under `projects/{enc-cwd}/{sessionId}/subagents/` fired nothing Perch watched, so it waited for the reconcile timer or for an unrelated session to write its file.

The fix:

- A recursive watcher on each config dir's `projects/` folder, for file-name changes only (created/renamed, never appended to). It requests a scan only for `subagents/agent-*.jsonl` and `agent-*.meta.json`, so a spawn lands within the 150ms debounce.
- The 2s poll also runs while any session has a working sub-agent, so activity text stays current and a finished agent drops off within about 2s. It stops when none is working.

## Steps

1. Run the tray from this branch: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.
2. In a **terminal** Claude Code session (not a Perch-controlled one), paste:

   > Launch two background general-purpose agents in parallel. The first should run `Start-Sleep -Seconds 20` in PowerShell and report "short done"; the second should run `Start-Sleep -Seconds 60` and report "long done". Don't wait for them.

3. **Expected (after):** both agents appear on the session row within about 1s of the terminal showing them launched. **Before:** they appeared anywhere from 0 to 30s later, and the 20-second one was sometimes never shown.
4. **Expected (after):** the 20-second agent drops off within about 2s of its notification arriving in the terminal, while the 60-second one stays. **Before:** it could linger for up to 30s.
5. Check that nothing is busy at rest. Once both agents have finished and the session is idle, leave the tray for a minute. `logs/scan.log` should gain no new "Slow scan" lines, and CPU should settle back to idle levels.
