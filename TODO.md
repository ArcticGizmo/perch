# TODO


## Bigger ideas (brainstorm)
- turn the watcher into a controller — push past read-only monitoring
  - remote approve/deny: plugin captures the permission-prompt text ("wants to run `rm -rf …`") and the
    overlay/ntfy notification gets Approve/Deny buttons that write the answer back, so a session can be
    unblocked without alt-tabbing
  - quick-reply: a one-line box on the overlay row (or an ntfy reply) injected as the next prompt
    ("yes, continue"). Lighter, always-on version of the claude.ai bridge round-trip
- stuck / runaway detection — distinct alert when a session is running badly, not just done:
  - running > N minutes with no new transcript records → "possibly hung"
  - same tool fired 50+ times in a short window → "possible runaway loop"
- human-latency analytics — flip the stats onto the user: from transcript gaps, measure "time Claude
  spent waiting on me" vs "time I spent waiting on Claude" per session/day. Pairs with escalating
  notifications (balloon → ntfy → louder) when a session is ignored for X minutes
- budget guardrails + rate-limit ETA — daily/weekly $ budget with a threshold alert; project the usage
  bar forward to a date ("at current burn you hit your weekly cap ~Thu 3pm") — extends ShowExpectedUsageRate
- global transcript search — search box over ~/.claude/projects/** ("find where I discussed X across all
  sessions"), jumping into the history viewer at the hit
- billable-time export — export the existing per-project active time as CSV / weekly timesheet (or push to
  a time tracker) for anyone billing clients by project
- smaller touches:
  - last-action peek on hover ("last did: edited SessionMonitor.cs") via the existing tooltip pattern / Activity field
  - per-project notification routing — different chime/priority, or mute a noisy project
  - focus window / DND to batch notifications during a meeting
- add git information (-+ lines of code)
- add right click to toggle system usage/weekly stats on or off

## More ideas (2026-07)

- new views:
  - activity / notification log window — a scrollable, timestamped feed of past done/waiting/stuck
    events, so you can catch up on what happened while AFK (pairs with the lock-override push)
- interaction / flow:
  - global hotkey to cycle-focus sessions — an "Alt-Tab for Claude Code sessions", plus a hotkey
    that jumps straight to whichever one needs attention (reuses the existing terminal-focus logic)
  - quiet hours / DND schedule — scheduled do-not-disturb, or a one-click "mute for 30 min"; the
    scheduled sibling of the DND / focus-window idea above
  - session notes & pins — jot a short note against a session and pin important ones to the top of
    the overlay; stored in a sidecar, in keeping with the file-based model
- silly (but on-brand for a thing called Perch):
  - voice / TTS announcements — optionally speak the session name ("the auth refactor is done")
    instead of a generic chime, for when you're heads-down elsewhere
  - confetti / sound pack when a task list hits n/n — gamification beyond Wrapped


