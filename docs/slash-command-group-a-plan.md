# Group A (wizard/picker) slash commands — implementation plan

Per-command decisions (user, 2026-09-04) for the interactive built-in commands, sequenced by difficulty.
Group B (actions) and Group C (read-only) are decided separately. Depends on the palette/catalogue
foundation already shipped (`docs/session-slash-commands-plan.md`).

## Decisions

| Command | Decision |
|---|---|
| `/config` | Open **Claude Desktop** app if possible (shell-launch) |
| `/model` | Open the **model menu** in the UI (the composer's model pill) |
| `/resume` | Open a **modal** = the launcher's "recent" list, **filtered to this project** (for now) |
| `/mcp` | A **modal to configure MCPs** — low priority, experimental (loopback auth may not work) |
| `/permissions` | A **modal** — low priority, needs more examples first |
| `/login` / `/logout` | Probably **shell out** to a normal interactive `claude` process |
| `/terminal-setup` | **Remove** |
| `/statusline` | **Defer** — bring back later as an enhancement |
| `/hooks` | **Defer** |
| `/bug` | **Defer** |
| `/theme` | Open **Perch Settings → Appearance** (theme section) |
| `/install-github-app` | **Remove** |

## Progress

- **Step 0 (dispatch) — DONE (2026-09-04).** `SessionWindow.RunNativeCommand(name)` routes model/effort/theme/
  config; called from both `AcceptPalette` and `SendPrompt` (a bare no-arg native command runs its action
  instead of being sent). `OpenSettingsRequested(pageKey)` event wired in the App.
- **Group 1 — DONE (2026-09-04).** `/model`→model pill, `/effort`→effort pill, `/theme`→Settings→Appearance
  (`App.OpenSettings(page)` + `SettingsWindow.NavigateTo("appearance")`), `/config`→`SessionLauncher.
  OpenClaudeDesktop()` (note if not installed). Catalogue: added `/theme`, `/config` description now "Open
  Claude Desktop". Builds green, 30 slash/highlighter tests pass. **Live dogfood owed.**
- **Group 2 (`/resume`) — DONE (2026-09-04).** Reused the launcher rather than a new window: `/resume` raises
  `ResumeInProjectRequested(cwd)` → `App.OpenResumePicker` opens a fresh launcher window and calls
  `SessionWindow.ShowResumePicker(cwd)`, which sets a `_projectFilter` so `RenderRecents` scopes the recents
  (and the search) to that project, keeping every existing guard/estimate/resume-flow. Picking a row opens a
  new session via the normal resume path. **Revised again (2026-09-04, user):** `/resume` now opens an
  **in-window quick-open overlay** (a scrim + centred card over the thread) instead of a separate window —
  a search box + a keyboard-navigable list (↑↓ select, Enter resume, Esc/scrim close) of the project's
  sessions. Shared the row visuals + guards: `RecentRow(e, onChoose, selected)` + `ChooseResume` (live-check +
  heavy-resume confirm) + `EnsureEstimates(shown, reRender)` now serve both the launcher and the overlay. A
  pick raises `ResumeSessionRequested(id, cwd)` → `App.OpenSessionResume` (opens/reuses a window). The
  separate-window path (`ShowResumePicker`/`OpenResumePicker`/`ResumeInProjectRequested`) was removed. Builds
  green, 1004 tests pass. **Live dogfood owed.**
  - **Feedback round 2 (2026-09-04):** (a) **Renamed titles now show** — `SessionHistory`'s per-file project/
    title cache was never invalidated, so a `/rename` after first caching stayed stale; it now re-reads when
    the transcript's last-write time advances. (b) **Resume replaces the current window by default** — Enter or
    click swaps this window's view to the resumed session (the previous one keeps running under the app,
    reachable from its row) via `ResumeReplace`→`StartSession(replace:true)` (Attach detaches the old view) or,
    if already a live Perch session, `App.ResumeIntoWindow` just attaches it; **Shift+Enter** opens it in a
    separate window. The overlay footer documents the gestures.
  - **Feedback round 3 (2026-09-04):** (a) the `/rename` **custom title is shown inline** with the project name
    in every session row (`RecentRow` name = `project · title`). (b) **Perch-controlled sessions are no longer
    "live in a terminal"** — since Perch supports many UIs on one session, such rows get a **brand-coloured
    dot**, an "open in Perch — opens another window" sub, and their custom title; clicking them opens/views the
    existing session (`ChooseResume` → `ResumeSessionRequested(newWindow:true)` → `App.OpenSessionResume` →
    `ShowSessionView` the existing `PerchSession`) with no heavy-resume confirm. Row state is derived from
    `LiveLookup(id).IsPerchControlled` (Perch-open) vs `IsActive` (terminal-live) vs resumable.

## Step 0 — Native-command dispatch (foundation for all of Group A)

Right now `AcceptPalette` special-cases only `/model` and `/effort` (opens their pills). Generalise this into
one dispatch so each command below is just a handler:

- A `RunNativeCommand(string name) : bool` in `SessionWindow` — returns true when it handled the command
  natively (so it is **not** sent to the CLI as text). Called from `AcceptPalette` (palette run) **and** from
  `SendPrompt` when the whole composer text is exactly a no-arg native command (so typing it and hitting Enter
  works even if the palette is closed).
- In-window handlers call methods directly (`ShowModelMenu`, the resume modal).
- App-level handlers raise **events** the App wires (mirroring the existing `NewSessionRequested`):
  `OpenClaudeDesktopRequested`, `OpenSettingsSectionRequested(SettingsSection)` (Appearance for `/theme`),
  and later `ShellCommandRequested(login|logout)`.
- Catalogue upkeep: `/terminal-setup` and `/install-github-app` are **never added** (already absent). Add
  `/theme` now; add `/permissions`, `/login`, `/logout` when their steps land; keep `/statusline`, `/hooks`,
  `/bug` out until their deferred work is picked up.

Small and testable; unblocks everything else. **Do this first.**

## Group 1 — Trivial: route to existing Perch UI (≈ half a day total)

1. **`/model` → model menu.** Already routed in `AcceptPalette`; fold it into `RunNativeCommand`, confirm the
   catalogue entry, done. *(Effectively done.)*
2. **`/theme` → Settings → Appearance.** Add `/theme` (Native) to the catalogue. Extend `App.OpenSettings` to
   take an optional target section and have `SettingsWindow` select it (the Appearance/designer page already
   exists — Settings is registry-driven, so this is "navigate to surface/section", likely a small addition to
   `SettingsWindow`). Wire `SessionWindow.OpenSettingsSectionRequested(Appearance)` → `OpenSettings(section)`.
3. **`/config` → launch Claude Desktop.** New tiny platform capability (e.g. `IExternalApps.LaunchClaudeDesktop`
   with a Windows impl that resolves the installed Claude Desktop exe / shell-launches it), resolved via
   `PlatformServices`. `SessionWindow.OpenClaudeDesktopRequested` → App calls it; if not installed, a toast/note
   ("Claude Desktop isn't installed"). Confirm what "config" should really open with the user if Desktop is
   absent.

## Group 2 — One modal, reusing the launcher (≈ a day)

4. **`/resume` → project-filtered recents modal.** The launcher already has every piece: `SessionHistory.ListAll`,
   `RecentRow`, the resume estimate, and `ConfirmHeavyResumeAsync`. Extract the recents list + resume flow into a
   reusable **`ResumePickerWindow`** (or a lightweight in-window overlay) that:
   - lists recent sessions **filtered to the current project** (`_cwd`), newest first (reuse `MatchesSearch`/the
     project grouping),
   - on pick, opens a **new** Perch session via the existing new-session/resume path (you can't resume *into* the
     running session — it starts another `SessionWindow`, exactly like the launcher's resume),
   - carries the refuse-if-live + heavy-resume guards already written.
   Wire `SessionWindow` `/resume` → open this modal seeded with `_cwd`. Later: a project filter toggle to widen it.

## Group 3 — New config modals: low priority, spike first (multi-day each)

5. **`/permissions` → modal.** Blocked on **more examples** from the user. *Park until examples arrive.*
6. **`/mcp` → status view — DONE (read-only, 2026-09-04).** init now carries `mcp_servers` (parsed into
   `SessionInitEvent.McpServers` → `SessionConversation.McpServers`, fixture-tested). `/mcp` opens
   `McpStatusWindow`: a modal listing each server with a colour-coded status badge (green connected / amber
   needs-auth / red failed), and a footer pointing at `claude mcp` for config. **Editing + loopback/browser
   auth are still deferred** (experimental) — the status view is the cheap first cut. Builds green.

## Group 4 — Shell-out / auth — DONE (2026-09-04)

7. **`/login` / `/logout` → shell out.** Spiked: the CLI exposes `claude auth login` / `auth logout` / `auth
   status` (login runs a browser OAuth flow → needs a real terminal). Generalised the launcher seam:
   `ISessionLauncher.RunClaudeCommand(cwd, args, terminal)` (Windows impl reuses `Reopen`'s terminal
   selection + fallback; Mac stub). `/login`→`auth login`, `/logout`→`auth logout` open a terminal (a note
   confirms / reports if none). Perch picks up the new auth on its next usage poll. Added `/login`, `/logout`
   to the catalogue. **Live dogfood owed.**

## Remove now

- `/terminal-setup`, `/install-github-app` — never surface them (both already absent from the catalogue; just
  don't add them). No code change needed beyond this note.

## Deferred (tracked, not scheduled)

- `/statusline` — revisit later as an **enhancement** (a Perch-native status-line/config surface).
- `/hooks` — Perch manages its own hook; a general hooks view is out of scope for now.
- `/bug` — feedback flow; decide later (native form vs. shell-out).

## Suggested order to work through

Step 0 → **1 (model)** → **2 (theme)** → **3 (config/Claude Desktop)** → **4 (resume modal)** → then, when
prioritised: **6 (mcp status view)**, **7 (login/logout spike)**, **5 (permissions, once examples exist)**.
Each is independently shippable behind the Step-0 dispatch.
