# TODO
- figure out how the visualize tasks better (this is turning out to be a bit flakey)
    - we might need to mark it as experimental and figure out how to resolve the PID
    - Maybe hooks can help?
- add the ability to manage and customise statuslines
  - this may actually be a separate app, but I would like to be able to get all the information well formatted and swap between them at will
- Allow rendering of really large session histories (think 10mb)
  - the dropdown should also give an indicator of size so you know what to expect
- make the permission mode dim when idle so that is less obvious
- add a tooltip over the thermometer
- make the session indicator red if you are over the expected rate, provided it is greater than 5%


## Quick wins
- session count badge on tray icon — render active session count as a number overlay on the tray icon
- global hotkey — system-wide shortcut (configurable) to show/hide the overlay


## Bigger ideas (brainstorm)
- turn the watcher into a controller — push past read-only monitoring
  - remote approve/deny: plugin captures the permission-prompt text ("wants to run `rm -rf …`") and the
    overlay/ntfy notification gets Approve/Deny buttons that write the answer back, so a session can be
    unblocked without alt-tabbing
  - quick-reply: a one-line box on the overlay row (or an ntfy reply) injected as the next prompt
    ("yes, continue"). Lighter, always-on version of the claude.ai bridge round-trip
- context-window pressure gauge — per-session context-fill bar (cumulative tokens vs model window) that
  warns *before* auto-compaction hits ("~85% full, compaction imminent")
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
  - weekly "your week in Claude Code" card rendered as a shareable image (reuse the owner-drawn dashboard)
  - focus window / DND to batch notifications during a meeting


