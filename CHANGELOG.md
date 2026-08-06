# Changelog

All notable changes to Perch are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.3.8] - 2026-08-06

- Right-click the note button to search across all your projects
- Pin a note to any of them without starting a session first
- Ones already carrying a note float to the top, previewed
- Same spotlight chrome as the session switcher (they're cousins now)

---

## [v0.3.7] - 2026-08-05

- A status dot on the PR glyph — green, red, or blue — for its CI checks
- Hover it for each check, coloured by pass / fail / still-running
- Click it to open any check's logs straight from the menu
- Or list the checks inline as session child rows — new "PR checks" toggle
- Glyph tooltips wait a beat longer before popping (brushing past no longer flickers)

---

## [v0.3.6] - 2026-08-05

- Six colour themes on a new Appearance page — Midnight, Ember, Blush, Dim, High Contrast, and a Winamp tribute that has no business looking this good
- Switching theme recolours the whole app at once: overlay, settings, every window
- A theme designer — re-tint the chrome, pick an accent, live WCAG contrast readout with a one-click "Fix"
- Accent picker is a proper spectrum-and-hue colour map
- Colour-blind preview (protanopia / deuteranopia / tritanopia), so status colours don't blur into one
- Save custom themes, and copy one to the clipboard as a code you can paste back to import
- Keyboard focus is finally visible on the settings toggles (Space or Enter flips them)
- The overlay's muted text was quietly failing contrast; it isn't anymore

---

## [v0.3.5] - 2026-08-05

- New read-only Change Review, from a session's right-click menu (on by default)
- Working-tree changes and recent commits, each with its own diff
- Unified or split diffs, with a wrap toggle and line numbers
- Ctrl+F find highlights every match, the current one loudest
- Collapsible file sections, for folding a big diff away
- Select text across lines, or whole line ranges by number, and copy
- Auto-refreshes as the working tree changes, keeping your scroll position
- Flags BOM- or line-ending-only changes with ≈ (git sees them; you can't)

---

## [v0.3.4] - 2026-08-05

- New achievement: swear in your prompts 10 times for Fowl Mouthed, 100 for Like a Sailor (it counts; it does not judge)

---

## [v0.3.3] - 2026-08-05

- Dense-mode hotkey no longer collapses the preview while you're hovering it (it would just pop back)

---

## [v0.3.2] - 2026-08-05

- Flight path now tells "waiting for input" apart from "done and idle" (the old bar meant either)
- Flight path lanes show active, waiting, and idle time, plus a day total
- API errors marked on the flight path, status code and all (529, an old friend)

---

## [v0.3.1] - 2026-08-05

- Dense-mode hotkey now dismisses the popped-open list preview before leaving dense (a second press still exits)

---

## [v0.3.0] - 2026-08-04

- Rebuilt Settings around a searchable catalogue of every feature
- Live overlay preview docked in Settings, updating as you toggle
- Retired the old per-topic settings pages (your settings carry over)
- Dropped the Experimental section; Agent Teams joins the catalogue
- Media controller now toggles from Settings, not the overlay menu
- Project-only note glyph dimmed (an inherited note needn't shout)
- Note glyph opens on click; the hover tooltip that fought it is gone

---

## [v0.2.39] - 2026-08-04

- Achievements window opens wider — three trophies across by default
- Search box filters the wall live; `?` rounds up just the mystery tiles
- Trophies group by theme instead of scattering across tiers, each section keeping its own tally

---

## [v0.2.38] - 2026-08-04

- Achievement cards and toasts now spell out the criteria that fired them ("1M input tokens")
- The level number moves to a dimmer second line, no longer the whole story

---

## [v0.2.37] - 2026-08-03

- GitHub pull-request glyph on session rows, coloured by state (open, draft, merged, closed)
- Click it to open the PR in a browser (title, state and number in tow)
- New "GitHub pull requests" toggle under Settings → Integrations, off by default
- Uses the `gh` CLI you're already signed into; nothing runs while it's off
- Configurable check interval (default 5 minutes); a missing PR is remembered just as long

---

## [v0.2.36] - 2026-08-03

- Roughly halved memory use by switching the overlay to software rendering
- Retired the ambient screen-edge glow (an experiment nobody switched on)

---

## [v0.2.35] - 2026-07-30

- New "daemon" section: Claude Code's headless background workers, under the session rows
- Clicking one opens an options menu — there is no terminal window to focus (we checked)
- Five rows at most; "show +N more" opens the full list in its own window
- Spare workers are hidden (pre-warmed, perpetually idle, not news)
- Daemon workers stay out of the header counts and never fire notifications
- "Display daemon processes" toggle under Settings → Indicators, on by default
- Fixed the one-line installer on stock Windows PowerShell 5.1

---

## [v0.2.34] - 2026-07-29

- One-line Windows install: `irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex`
- Releases ship `SHA256SUMS.txt`; a download that doesn't match is deleted, not run
- Still no SmartScreen wall, and updates stay in-app where they belong
- Removed Scoop support (a bold same-day experiment): updating meant leaving the app

---

## [v0.2.33] - 2026-07-29

- `scoop install perch`, with no SmartScreen wall to click past
- Perch knows how it was installed, and won't update what Scoop owns
- Scoop copies still hear about new versions, command included (one click to copy)
- `scoop uninstall perch` clears the hooks and login entry, like the real uninstaller

---

## [v0.2.32] - 2026-07-29

- The mic strip is one line now: which app has your microphone, click it to go there
- Removed Teams call controls and every mute button (a bold four-hour experiment)

---

## [v0.2.31] - 2026-07-29

- Auto Start Perch: never, when a session starts, or when you log in
- Starting at login is Windows' job now, so Perch is up before the first session
- Your old auto-start setting carries over (it becomes "when a session starts")

---

## [v0.2.30] - 2026-07-29

- Limits, quick links and Hypertree stay put with no sessions running
- Only the Claude rows go missing, there being none to show
- The header chevron expands and collapses at zero sessions too
- Dense hover popup no longer clips the microphone strip

---

## [v0.2.29] - 2026-07-28

- New microphone strip: which app is currently using your mic
- Names Slack, Zoom, browser tabs and OBS too, not just Teams
- Click the app's name to jump to it, virtual desktops included
- Mute from the strip — the capture device, or Teams itself when connected
- Opt-in Teams call controls: real meeting state, and a mute Teams agrees with
- Settings checks whether Teams' third-party API is switched on (it usually isn't)

---

## [v0.2.28] - 2026-07-28

- Clicking a session with a hidden terminal window un-hides it
- Previously the click did nothing, quietly
- A session with no window left to focus says so
- New "Terminate session…" on the row right-click menu, with a confirmation
- It re-checks the PID before killing anything (the desktop app is also claude.exe)

---

## [v0.2.27] - 2026-07-28

- The panel keeps its expanded state when the last session ends
- Previously it collapsed itself and hid the chevron you'd need to undo that

---

## [v0.2.26] - 2026-07-28

- Hypertree rows with more than one desktop gain a chevron on their label
- Click it to pick a desktop, rather than always taking the resume point
- The rest of the line still jumps where it always did

---

## [v0.2.25] - 2026-07-27

- New red "API error" status when a session's last request fails (e.g. 529 Overloaded)
- A failed run no longer masquerades as a cheerful "done"
- Shows the HTTP code on the row ("api 529"), with its own notification and chime toggle
- Clears itself the moment the session retries and recovers

---

## [v0.2.24] - 2026-07-27

- Optional [Hypertree](https://github.com/ArcticGizmo/hypertree) integration: your desktop branches under the quick links, click to jump
- The branch you're on is marked, and stays marked when you switch desktops outside Perch
- Settings grows an Integrations page (a population of one, for now)

---

## [v0.2.23] - 2026-07-25

- Context pressure sizes the window from the evidence, not a table of model names
- Opus 5 on the 1M beta stops reading as 200k (it was inventing five times the pressure)
- Context tooltip names the model and where its window came from, for when it's still wrong

---

## [v0.2.22] - 2026-07-25

- Per-model weekly limits in the usage strip (Fable's was there all along)

---

## [v0.2.21] - 2026-07-24

- Renamed the macOS DMG to a stable `Perch-osx-arm64.dmg` (the version was just noise).
- Dropped the portable .zip builds from releases (nobody was reaching for them).

---

## [v0.2.20] - 2026-07-23

- Now-playing media strip on the overlay, with previous / play-pause / next.
- Toggle it from Settings → Music or the header right-click menu (off by default).

---

## [v0.2.19] - 2026-07-23

- Token achievements split into Input, Output, and Cached milestones.
- Retired the lumped "total tokens" trophy (it just measured cache reads).

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
