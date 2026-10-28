# Idle compaction toggle — manual testing

Claude Code compacts a long conversation while the session is idle (before the prompt cache expires) unless `"idleCompaction": false` is set at the root of a config dir's `settings.json`. It defaults to on, and writing `true` does not force it on, so Perch writes `false` to disable and removes the key to enable. The setting is "Idle compaction in Claude Code" (Settings → Features → Advanced, or Search "idle").

Agent Teams and idle compaction both live in each config dir's own `settings.json`, so with more than one config dir they show an **Edit…** button (Features page **and** Search page) that opens a per-directory dialog instead of a single toggle.

## Single config dir

1. Open Settings → Search, type `idle`. Expect a toggle that is **on** (assuming no `idleCompaction` key in `~/.claude/settings.json`).
2. Turn it off. Expect `"idleCompaction": false` in `~/.claude/settings.json`, with every other key untouched.
3. In a Claude Code session, run `/config` and check "Idle compaction" reads off.
4. Turn it back on in Perch. Expect the key removed (not set to `true`).

## Several config dirs

1. With two or more dirs on Settings → Config directories, open Search and type `agent` — expect **Edit…**, not a toggle (before this change Search showed a toggle that only wrote the primary dir).
2. Click Edit… — expect "Enable Agent Teams in Claude Code — per directory" listing each dir with its own toggle.
3. Repeat for `idle`. Flip one dir off; expect only that dir's `settings.json` to gain `"idleCompaction": false`.
4. Check the Features page (Advanced) shows Edit… for both cards too.
