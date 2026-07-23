# Changelog

All notable changes to Perch are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.2.18] - 2026-07-23

- The floating overlay finds its way home when you undock — no more hiding on a monitor that left.
- Expanding or collapsing re-asserts the overlay as topmost, in case a display change buried it.

---

## [v0.2.17] - 2026-07-23

- Clicking the "update available" toast now starts the update, not just the button.
- Reworded the toast to admit both routes exist.

---

## [v0.2.16] - 2026-07-23

- Notes are sticky notes now — draggable, resizable, and shamelessly yellow.
- They open beside the overlay, never on top of or behind it, and don't block the rest of Perch.
- Row notes split into a project note (shared across the project) and a session note.
- A project note lights the note glyph on every session in that folder.

---

## [v0.2.15] - 2026-07-22

- New global scratch pad — a note button on the quick-links row opens a multi-line pad.
- Session notes are multi-line now; the 140-character limit has been retired.
- Click a session's note glyph to edit it (no more digging through the right-click menu).
- Session notes are an Indicators toggle now, off by default.

---

## [v0.2.14] - 2026-07-21

- New **Replay mode**: record a Claude Code session and scrub through it in the real Perch (demos, bug repros).
- Capture recordings from Settings → Export, redacted by default (text out, shape in).
- Play back with `perch replay <file>` — a transport window with play/pause, speed, and a scrub timeline.
- The timeline plots prompts, tool calls, sub-agents and interrupts as hover-able markers you can jump between.
- Replays wear a light-blue "Perch - Replay" badge and leave your live sessions well alone.

---

## [v0.2.13] - 2026-07-21

- Fixed the dense strip eating clicks where "Hide inactive members" had removed rows (invisible, but still grabby).

---

## [v0.2.12] - 2026-07-21

- Post-update "what's new" window — only the releases since your last version.
- Switch it off in Settings → Changelog, or dismiss it for good from the window.

---

## [v0.2.11] - 2026-07-21

- Stopped the overlay hopping to the front every five seconds (it kept landing on its own tooltips).

---

## [v0.2.10] - 2026-07-20

- Gold unlocks now flip a card out of the screen under a black vignette (confetti has retired).
- A batch flips in up to three cards side by side, plus a "+N more" card.
- Unlock toasts are now a separate toggle, off by default (the reveal does the celebrating).

---

## [v0.2.9] - 2026-07-19

- More trophies for the cabinet: tool-grind badges (Web Crawler, Search Party, List Maker, Plan B).
- Secret achievements — mystery tiles showing only a cryptic hint until earned (that's rather the point).

---

## [v0.2.8] - 2026-07-19

- Achievement badges — trophies that level up with your lifetime stats, earned retroactively.

---

## [v0.2.7] - 2026-07-19

- Reopen recently-closed sessions from the switcher — a fresh terminal running claude --resume.
- Closed sessions join the switcher list, renamed titles and all.
- Ctrl+Enter copies the resume command instead of launching a terminal.
- Choose which terminal reopening uses — Windows Terminal, PowerShell, or Command Prompt.

---

## [v0.2.6] - 2026-07-18

- Pinned session notes — annotate any session from its right-click menu.
- Notes ride along in the overlay and survive restarts (the sticky that doesn't fall off).
- New "Session notes" toggle in Indicators — a dedicated line, or a compact hover-able glyph.

---

## [v0.2.5] - 2026-07-18

- Configurable keyboard shortcuts — a new Shortcuts settings page, every hotkey rebindable.
- Jump to next session (Alt+Shift+S) — cycle focus through your terminals; the overlay marks where you land.
- Session switcher (Alt+Shift+Space) — a keyboard palette to leap to any session. Perch's own Cmd+Space.
- A finishing sub-agent no longer fires a premature "done" mid-thought.

---

## [v0.2.4] - 2026-07-16

- Outage footer now reflects the worst live incident (a major outage no longer poses as "minor").
- Outage menu opens on either mouse button.

---

## [v0.2.3] - 2026-07-16

- Fixed auto-start hanging your first prompt.

---

## [v0.2.2] - 2026-07-16

- Stopped duplicating our own hooks into settings.json on every launch (they were multiplying).
- Existing duplicate hook entries get swept up on the next startup.

---

## [v0.2.1] - 2026-07-15

- Claude service-status footer — flags an Anthropic outage, so you know it's not just you.
- Configurable status poll interval.
- Perch is now MIT licensed (the paperwork was overdue).

---

## [v0.2.0] - 2026-07-08

- Rebuilt on Avalonia so you can use this on MacOS!

---

## [v0.1.27] - 2026-07-04

### Added

- Autonomous section — background (SDK-driven) sessions collapse under one counted header, below the real ones.
- A little robot marks each background session (nobody's at the keyboard).

---

## [v0.1.26] - 2026-07-02

### Added

- Right-click the overlay header to show or hide the system metrics and usage strips.
- Right-click either strip to hide it on the spot.

### Fixed

- Menu glyphs now render properly (the party popper had been showing up as a tofu box).

---

## [v0.1.25] - 2026-07-02

### Added

- Confetti finish — a session erupts with confetti the moment it next completes.
- Right-click a session to arm it (experimental, off by default); a party popper marks the armed row.
- Fires exactly once, then disarms itself.
- Never saved, so it can't ambush you after a restart (you're welcome).

---

## [v0.1.24] - 2026-07-02

### Added

- Git line changes — a "+142 -37" chip beside each session's name (green added, red deleted) for unstaged work.
- Toggle under Settings → Experimental, off by default (off means it never even thinks of running git).

---

## [v0.1.23] - 2026-07-02

### Fixed

- Cancelling a turn (Esc / Ctrl+C) no longer fires a phantom "done" alert.

---

## [v0.1.22] - 2026-07-02

### Added

- Perch reacts — the tray and overlay bird wears your sessions' mood.
- Dozes when idle, alert while working, flags a "!" when you're needed, and panics (sweat and all) on a stuck session.
- Toggle under Settings → Experimental (on by default).

---

## [v0.1.21] - 2026-07-02

### Added

- Flight path — a daily timeline of your sessions, one lane each, coloured by what they were up to.
- Lanes mark engaged, waiting-on-you, and stuck stretches; blank means you'd wandered off.
- Step through earlier days with ‹ / › or the arrow keys.

---

## [v0.1.20] - 2026-07-02

### Overlay

- "Waiting on you" timer on blocked rows — warms yellow to red the longer you ignore it (configurable, default 10 minutes).

---

## [v0.1.19] - 2026-07-02

### Experimental

- Live token burn rate (tokens/min) beside a running session — off by default.
- It measures fresh tokens only; counting the context re-read pushed it into the millions and helped no one.

---

## [v0.1.18] - 2026-07-02

### Overlay

- The attention border is now a travelling neon glow, not a hard orange flash.

### Experimental

- Ambient screen-edge glow — a soft pulse around your screen when a session needs you (off by default).
- The glow follows the overlay to whichever monitor you drag it onto.

---

## [v0.1.17] - 2026-07-01

### Added

- Automatic update checks on startup and hourly (checks only — nothing downloads uninvited).
- An orange update badge, top-right of the panel, when a new version is waiting.
- "Check for Updates…" in the tray menu becomes "Update available" once there is one.
- An "update available" flag on **About** in Settings, for good measure.
- Clicking any of them installs the update; you're notified once, not every hour.

---

## [v0.1.16] - 2026-07-01

### Fixed

- Stopped reading an open `/workflows` menu as "awaiting input" (it was just a menu).

---

## [v0.1.15] - 2026-07-01

### Monitoring

- New "Monitoring" page — live CPU and RAM in the overlay (off by default; nothing is sampled until you opt in).
- Whole-machine CPU + RAM strip across the top of the panel.
- Per-session CPU/RAM mini-bars, coloured by load; hover for the exact numbers.
- Optional whole-process-tree roll-up per session — the MCP servers, shells and tools it spawns, not just the `claude` process.
- Sub-agents fold into their session's bar (they share one process; there's no prising them apart).

---

## [v0.1.14] - 2026-07-01

### Experimental

- "Hide inactive members" toggle for Agent Teams — idle teammates drop off the roster (they reappear the moment they do something).

---

## [v0.1.13] - 2026-07-01

### Context pressure

- Sonnet 5 reads as a 1M window, not 200k (it was quietly shrinking everyone's headroom).

---

## [v0.1.12] - 2026-07-01

### Context pressure

- New option: a green thermometer below the first threshold instead of blank. Off by default.
- Thermometer hover shows the numbers now — "34.6k/200k (17%)" — not just the percent.

---

## [v0.1.11] - 2026-06-30

### Notifications

- The "done" badge now sticks until you look at it, instead of vanishing after five minutes.

---

## [v0.1.10] - 2026-06-30

### Notifications

- No more duplicate "done" when a sub-agent finishes and the session keeps working (one was plenty).
- The completion alert now waits a beat to see whether the parent picks the work back up.

---

## [v0.1.9] - 2026-06-30

### Plugin

- Fixed the Claude Code plugin "Update" button, which had only ever produced errors (it asked for the plugin by the wrong name).

---

## [v0.1.8] - 2026-06-29

### Detection

- Sub-agents and teammates retire the instant they finish, not a staleness window later.
- Driven by Claude Code's `SubagentStop`/`TeammateIdle` hooks; the old timer still backs it up.
- A re-tasked teammate springs back to life on its own (it never really left).

---

## [v0.1.7] - 2026-06-29

### Settings

- New Indicators tab corrals the overlay glyph toggles in one place.
- Toggle to show or hide artifacts on the overlay.
- Toggle for the permission-mode badge.
- New Experimental tab for early features (Agent Teams, currently lurking).
- Settings pages no longer clip their last row when scrolled.

### Detection

- Idle sessions now time out after a stretch of inactivity.

---

## [v0.1.6] - 2026-06-29

### Overlay

- Task-list progress on a running session: an _n/m_ count that climbs as Claude works the list.
- Hover the count for the full checklist — ✓ done, ▸ doing, ○ waiting.
- The list bows out when you move on, and a fresh plan starts at 0 (no lingering "5/5").

---

## [v0.1.5] - 2026-06-28

### Detection

- Stuck-session warnings: an amber ⚠ on a session that's spinning — tool calls failing in a row, or the same failing action on loop.
- Hover the warning for why Perch is worried.
- New Detection settings page to switch it off — or just the half that's crying wolf.

### Overlay

- Live activity now describes PowerShell commands, not just Bash (PowerShell was getting the silent treatment).

---

## [v0.1.4] - 2026-06-27

### Stats

- **Perch Wrapped**: turn any scope's stats into a shareable poster (the gradient button is hard to miss).
- A data-derived persona on each poster — Night Owl, Agent Wrangler, Token Titan, and friends.
- Playful equivalences ("≈ 333 novels of text", "≈ 48 movies of focus") and a highlight reel.
- Copy your Wrapped to the clipboard or save it as a PNG, then go flex.

---

## [v0.1.3] - 2026-06-27

### Overlay

- Permission-mode badges dim on idle sessions (they were shouting into an empty room).

---

## [v0.1.2] - 2026-06-27

### Overlay

- Session and Weekly markers turn red when usage outpaces the clock (held back until 5% in, to spare your nerves early).
- Hover the context thermometer for a "Context at NN%" readout.

---

## [v0.1.1] - 2026-06-27

### History

- Large transcripts (10 MB+) no longer crash the viewer; they load in the background and ask first.
- Session sizes shown in the dropdown (lag, telegraphed).
- History opens to a "pick a session" prompt, with a "(none)" option for the indecisive.

---

## [v0.1.0] - 2026-06-27

The first release worth giving a round number. Everything before this was a
rehearsal; the audience just happened to be claude-watching. What follows is the
accumulated work of several dozen point releases, compacted into one tidy entry
and stripped of the embarrassing intermediate states.

### Overlay

- A floating desktop overlay with one square per active Claude Code session — humble, correct, and the whole reason this exists.
- Live activity indicator, elapsed time, and a status dot per session.
- Sub-agents (including background sub-agents) surface as child rows beneath their parent.
- Sessions renamed with `/rename` show that name everywhere, instead of a bare project folder.
- Sessions with published web artifacts show a clickable glyph — click to open, or pick from a list when there's more than one.
- A context-pressure gauge that warns you before your context window boils over.
- Clicking the overlay focuses the right terminal — including VS Code's integrated terminal, and the correct window when one VS Code hosts several (previously pot luck).
- Drag to reposition, dock to either side (the right-vs-left debate remains unresolved), dense mode for minimalists, and a remote-control icon you can actually see now.
- Git worktrees are hidden from the session list. They are not sessions; they are a trap.

### Notifications

- "Needs attention" detection that properly knows when Claude is waiting on _you_.
- Clicking a notification opens the relevant Claude instance.
- Notifications fire even when the machine is locked.
- Optional Windows chime when a session needs you (off by default; external alerts stay politely silent).
- Fast built-in commands like `/clear`, `/model`, and `/doctor` no longer trigger a "done" alert — no work, no ping.

### Remote control & external alerts

- Generate a QR code to control sessions from another device.
- External push notifications via ntfy.sh, with QR-code setup and a direct link straight to the remote session.

### History & stats

- A history viewer to browse past transcripts, with markdown rendering and clickable images.
- A session stats window: today's sessions, active time, prompts, tool calls, token totals, an equivalent API cost (what the tokens _would_ have cost pay-as-you-go — not a bill), an hourly heatmap, and breakdowns by project, tool, model, and git branch.
- Switch between Today, 7/30 days, and all time, with a daily activity trend, a day-streak counter, and records for the busiest day and longest single session.
- A "Today: N sessions · 3h 12m active" line in the tray menu, read straight from your transcripts — so there's history from the moment you install it.

### Quick Links

- Shortcut to any app you like — much eaiser than finding the window yourself.
- Icons pulled from the apps themselves (Store apps like Slack show their real logo), with presets for GitKraken, Slack, Microsoft Teams, and Outlook (new and classic installs both).
- Live preview of the app it found as you type, and optional upside-down icons for the connoisseur of inverted iconography (off by default, mercifully).

### Sessions & plugin

- A companion Claude Code plugin for automatic session start/stop and permission-monitor hooks (write-mode and cleanup-mode).
- Session limits to cap concurrency, configurable auto-close with a countdown so you can watch your fate approach.
- Plugin installs at user scope, so it follows you across every project.
- Install and update messages point you at `/reload-plugins` rather than a full restart.

### Configuration

- A settings window that gathers everything in one place — reworked into something larger and more coherent, after a brief and bold three-minute experiment with in-app settings that was immediately reverted.
- Honours the `CLAUDE_CONFIG_DIR` environment variable, matching Claude Code itself.

### Plumbing

- Event-driven session state — no more polling for what changed.
- Single-instance enforcement: one Perch, no negotiations.
- Owner-drawn dashboards, cached overlay rendering for snappiness, and a sweeping under-the-hood refactor you will not notice in the slightest — which is precisely the point.
