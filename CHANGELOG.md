# Changelog

All notable changes to Perch are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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

- Task-list progress on a running session: an *n/m* count that climbs as Claude works the list.
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
