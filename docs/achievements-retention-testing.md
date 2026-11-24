# Achievements: sub-agent count and deleted-transcript retention: manual testing

Two fixes to the stats behind the Stats window and Achievements.

- **Sub-agents were never counted.** The stats fold only counted a `tool_use` named `Task`, but current Claude Code names its sub-agent launcher `Agent`. The Sub-agents family (Delegator, Middle Manager, Puppet Master, Overlord) was stuck. Both names now count. `SessionFoldVersion` is bumped to 3, so the first report after upgrading rebuilds `stats-cache.bin` from the transcripts.
- **"All time" shrank as Claude Code deleted transcripts.** Claude Code deletes transcripts older than `cleanupPeriodDays` (30 by default) from `~/.claude/settings.json`. The all-time report then dropped those sessions, so every cumulative figure (prompts, swears, tokens, sub-agents…) was really "the last N days", and the big badges could never be reached. A deleted transcript's figures now move into `stats-archive.bin`, beside `settings.json`, and keep counting. The archive is separate from the cache, so a cache rebuild never wipes it. History deleted *before* this change can't be recovered.

Run each build with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`, from `main` for "before" and from `fix/achievements-subagents-retention` for "after". Quit the installed Perch first. Achievements are checked when Perch starts and then at most every few minutes after a session finishes, so restart Perch to see a change straight away. Bronze and Silver unlocks don't get the full-screen card, so look in the Achievements window.

## 1. Sub-agent badges

| | What to do | What you see |
|---|---|---|
| Before | Open Achievements → Collaboration → Sub-agents. | Stuck at Delegator (or locked), with progress near 0, even though you use sub-agents daily. |
| After | Restart Perch on the branch and open the same tile. | The count reflects your `Agent` calls. With 100+ in your retained history, Middle Manager unlocks. With 1,000+, Puppet Master (Gold) unlocks and shows the full-screen card. |

To add one on demand, run this in a Claude Code session. It costs one small sub-agent run:

```
Use the Explore agent to find where SwearFilter is defined, then tell me the file name only.
```

Restart Perch: the Sub-agents count goes up by one.

## 2. A deleted transcript keeps counting

This deletes a throwaway transcript you create for the test. Don't delete a real one.

1. Start a new Claude Code session in a scratch folder and send:
   ```
   damn damn damn, just reply OK
   ```
2. Exit the session. Restart Perch and open Achievements → Activity → Swears. Note the count; it should have gone up by 3.
3. Delete that session's transcript: `~/.claude/projects/<the scratch folder, encoded>/<session id>.jsonl`.
4. Restart Perch and open the Swears tile again.

| | Step 4 shows |
|---|---|
| Before | The count drops by 3. The session is gone from Stats → All time too. |
| After | The count is unchanged. `stats-archive.bin` exists beside `settings.json`. |

5. After only: quit Perch, delete `stats-cache.bin` (not the archive), start Perch and open the tile again. The count is still unchanged, because the archive survives a cache rebuild.

## 3. No double counting

1. On the branch, repeat 2.1–2.4, but *move* the transcript out instead of deleting it.
2. Move it back and restart Perch.
3. The Swears count is the same as in step 2.2, not 3 higher: a transcript that comes back leaves the archive.
