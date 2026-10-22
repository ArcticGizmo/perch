# Work items — design & checkpoint plan

Status: **W0–W3 built** on branch `work-items` (2026-10-16), each checkpoint green on tests and checked by the
headless render. **Nothing has been run in a live tray yet**: the owed checks are `docs/work-items-testing.md`. W4 is
not started. The interaction exploration this builds on is the artifact "Perch Work Items" (local copy
`.claude/artefacts/work-items-interaction-patterns.html`).

### Where the build differs from the exploration

- **Member rows aren't indented.** They keep the full row width and wear a 3px stripe in the item's colour, so every
  glyph lines up with the ungrouped rows.
- **Ended sessions are faded lines under an expanded item** (changed 2026-10-16, at the user's ask; it was a count on
  the header). The newest five show, one line each, with a "N more ended…" line that opens the window for the rest. A
  click opens Resume in Perch / Resume in terminal / View history / Take out. A collapsed header still says "N ended".
- **Drag a session row (or an ended line) onto a group** (added 2026-10-16). Dropped on an item (its header or any line under it) it
  *moves* there, out of the item it was dragged from; dropped on "Not in a work item" it leaves its item. To have a
  session in two items, tick the second in its row menu.
- **A Work items button** (a briefcase) on the "+ New session" row, left of Recent and the Roost, opens the window.
- **A grouping button** at the far right of that row (right of the Roost, once an item exists) flips the list between
  grouped by work item and flat (the pre-work-items list). Persisted as `AppSettings.GroupByWorkItem`, UI state like
  the collapse set, so no Settings control.
- **Marking done asks nothing.** Done is reversible, and the item's still-running sessions just fall under "Not in a
  work item", so a leave/end/take-out prompt would only add a step. "End them" was also the one destructive choice.
- **No inline name field in the overlay.** "New work item from this session…" creates the item at once and opens the
  window with its name selected. The tray's **Work items…** opens the window's new-item field.

## The ask

Perch tracks *sessions*. What you actually care about is the *piece of work* those sessions are for: several agents
in different worktrees of one project, all chipping at one change. Unless you name every session, nothing shows how
they group. A **work item** is a named pile you define, which sessions join however and wherever they run. It never
decides where or how you run them (the difference from a single-screen tool like herdr).

## Decisions (user-confirmed 2026-10-16)

| # | Question | Decision |
|---|----------|----------|
| D1 | How big is a work item? | **As big as you want.** A name plus details. Perch doesn't tell a task from an epic. |
| D2 | Can a session be in several items? | **Yes, allowed, not designed around.** Membership is a tag, so it's free; the UI doesn't optimise for it. |
| D3 | Who marks an item done? | **Only the user.** Perch never closes one (a merged PR or idle sessions don't mean done). |
| D4 | Integrations | **A generic links list** (label + URL). No Jira/GitHub source for now. |
| D5 | Overlay shape | **Store as tags, show as groups**, focus mode later as an opt-in way to attach sessions. |
| D6 | Storage | **One markdown file per item** in the profile's `work/` folder. Front matter holds the structure, the body is notes + log. Editable in any editor, greppable, git-able. |

## The model

```
WorkItem   id · name · colour · status (Active/Paused/Done) · created · links[] · members[] · notes · log[]
Member     sessionId · via (manual/launched/terminal) · added · title · cwd     (title/cwd are snapshots for ended sessions)
LogEntry   at · text · byPerch
```

`via` records how a session joined, so automatic joins are visible and easy to undo. `title`/`cwd` let an ended
session still be shown, and resumed, from the item.

### On disk: `%APPDATA%\Perch\work\{slug}-{id}.md`

```markdown
---
id: 3f9c1a2b
name: "Payments refactor"
colour: teal
status: active
created: 2026-10-16T13:00:00Z
links:
  - label: "Design doc"
    url: "https://example.com/docs/payments-v2"
sessions:
  - id: 4f1c0000-…
    via: manual
    added: 2026-10-16T13:05:00Z
    title: "api · worktree-a"
    cwd: "C:\\src\\payments-wt-a"
---
Free-form notes, markdown.

## Log

- 2026-10-16 13:42 +11:00 Ledger API agreed with the platform team.
- 2026-10-16 13:05 +11:00 (perch) api · worktree-a joined (manual)
```

- The front matter is a small, fixed YAML subset Perch writes itself (scalars; lists of maps). Values are written as
  JSON-style quoted strings when they need it and read back either way. No YAML dependency.
- The **last** `## Log` heading splits notes from the log. Log lines are `- <yyyy-MM-dd HH:mm zzz> [(perch)] text`,
  oldest first (append-only); a line that doesn't parse is kept as text, never dropped.
- The file name is fixed at creation; renaming the item doesn't move the file. The `id` is the key.
- **Every mutation is read-modify-write of one file** (`WorkItemStore.Mutate`): reload it from disk, apply, write
  atomically. The tray, `perch work …` and a hand edit can all touch the same file without one silently undoing
  another's change. A file that won't parse is never rewritten.

### Referring to an item (`PERCH_WORK`, `--work`, `perch work …`)

An item ref matches, in order: its id, its file stem, the slug of its name, its name (case-insensitive), a unique
name prefix. Done items match too (so you can reopen by name). An unknown ref is ignored by the hook path and an
error on the CLI. Nothing ever creates an item implicitly.

## Interaction patterns (what's built)

Marked **first** (W0–W3) or **later** (W4).

| Journey | Pattern | When |
|---------|---------|------|
| Create | "New work item" in the Work items window / tray menu; "New work item from this session…" on a session row | W1/W3 |
| Create | `perch work new "<name>"` | W2 |
| Attach | Right-click a session → **Work items ▸** with checkmarks (tick adds, untick removes; many-to-many) | W1 |
| Attach | **+** on an item's overlay header starts a session inside it | W2 |
| Attach | `PERCH_WORK=<ref>` before `claude` (any terminal): `perch-hook` writes `{sid}.work`, the tray attaches it | W2 |
| Attach | `perch --work <ref> [dir]` opens a Perch session in the item; `perch work add <ref> <session-id>` | W2 |
| Attach | Resume keeps membership (same session id) | free |
| See | Overlay sessions section grouped by item: header = chevron, colour swatch, name, rolled-up status light + needs-you count, "+"; "Not in a work item" group last | W1 |
| See | Paused item = one dimmed collapsed line; Done item leaves the overlay; collapse remembered per item | W1 |
| See | Ended members counted on the header ("· 2 ended"); listed (and resumable) in the window | W1/W3 |
| Home | Work items window: Active / Paused / Done list; detail with name, colour, status, links, notes, sessions (with via), log | W3 |
| Log | Perch writes the facts (joined / left / status change / session ended); you write one-liners (window, header menu, `perch work log`) | W1–W3 |
| Log | "Copy as Markdown" of an item's log | W3 |
| Finish | Status buttons / header menu → Done (no prompt; reopen any time) | W3 |
| Later | Drag a row onto a header; folder rules; suggestions; "This week" view; transcript summaries; Roost tab binding; Focus mode | W4 |

If you never create an item, the overlay looks exactly as it did before.

## Checkpoints

| # | Scope | Gate |
|---|-------|------|
| ✅ **W0** | Core: `WorkItem` model, `WorkItemFile` codec (front matter + notes + log), `WorkItemStore` (folder, load, `Mutate`, create, resolve refs, membership helpers), hue set in `FixedColors`. xUnit tests: round-trip, hand-edited/odd files, ref resolution, concurrent read-modify-write. | tests green |
| ✅ **W1** | Overlay grouping (`OverlayCanvas.Work.cs`): rows per item, header paint/hit/hover, collapse state (`AppSettings.CollapsedWorkItems`, NotSettings), header right-click menu, session-row **Work items ▸** submenu, "New work item from this session…". App: `WorkItemsHost` (load, watch the folder, push to overlay), auto log of joins/leaves/session end. SampleData + render. | render + tests |
| ✅ **W2** | Starting inside an item: header "+" → launcher bound to the item; `perch --work`; `perch-hook` `PERCH_WORK` → `{sid}.work` sidecar (cleaned on SessionEnd) → `ClaudeSession.WorkRef` → tray attaches (`via: terminal`). `perch work new/list/add/log/done` CLI. Help text. | tests |
| ✅ **W3** | `WorkItemsWindow`: list + detail (rename, colour, status, links, notes edit, sessions with open/resume/remove, log + entry box + copy as Markdown). Tray menu entry. | render + manual |
| **W4** | Later set (see table). Not started. | — |

## Manual testing

`docs/work-items-testing.md` (before/after prompts for each checkpoint).
