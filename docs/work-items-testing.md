# Work items: manual test prompts

Manual checks for **work items** (docs/work-items-plan.md): named piles of work that sessions belong to, however they
run. What's covered:

- **Storage.** One markdown file per item in `%APPDATA%\Perch (Dev)\work\` for a dev run (`Perch\work\` installed).
  Front matter for the structure, the body for notes, a `## Log` at the end.
- **Overlay.** Once any item is active or paused, the session list groups by item. Each item has a header row, its
  member rows get a stripe in its colour, and the rest go under "Not in a work item".
- **Joining.** A session joins through the row menu's **Work items ▸**, the header's **+**, `PERCH_WORK` in any
  terminal, `perch --work`, or `perch work add`.
- **The Work items window.** It shows each item's name, colour, status, links, notes, sessions and log.

The headless render covers the overlay (`overlay_work_items_*.png`, dark and `daylight`) and the window
(`work_items_window_1x.png`). Joining, the folder watcher and the hook need a real tray, so they're below.

## Setup

1. Build and run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.
2. Keep Explorer open on `%APPDATA%\Perch (Dev)\work\` to watch the files.
3. Have two or three Claude sessions you don't mind poking at. Terminal sessions and Perch sessions both work.

The `perch` on PATH is still the installed one, which talks to the installed tray and its own folder. Run the dev
build's CLI instead: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- work …` (and `-- --work …`),
shortened to `perch work …` / `perch --work …` below. A Debug build is the dev profile automatically, so it writes to
`Perch (Dev)\work\` and talks to the dev tray. A Debug build also publishes a dev perch-hook next to itself, and the
dev tray installs it on start, so the `PERCH_WORK` checks use the new hook.

## 1. No items: the overlay is unchanged

Start with an empty (or absent) `work\` folder.

| | Expected |
|---|---|
| Overlay session list | Exactly as before: no headers, no "Not in a work item", Autonomous as usual |
| Right-click a session row | A new **Work items ▸** entry at the top of the actions group, holding only "New work item from this session…" and "All work items…" |

## 2. Create an item from a session

Right-click a running session → **Work items ▸ → New work item from this session…**

| | Expected |
|---|---|
| A file appears | `work\<session-name>-<8 hex>.md`, with the session under `sessions:` and `via: manual` |
| The Work items window | Opens on the new item with its name selected, so typing renames it. Enter commits the new name |
| The overlay | A header row with a teal swatch, the name, a status dot, and **+** at the right. The session sits under it with a teal stripe on its left edge. The other sessions are under **Not in a work item** |
| The log (window, bottom) | "… joined (manual)" in muted text |

## 3. Several items, several sessions

In the window, type a second name in **Name a new work item…** and press Enter. Then on another session row tick
**Work items ▸ → <second item>**. On the first session, tick the second item too.

| | Expected |
|---|---|
| Second item's colour | Different from the first (the next free hue) |
| A session ticked into both items | Listed under both headers. Unticking one removes it only from that item |
| Header light | The most urgent state among the item's sessions: red for an API error, then yellow for waiting on you, orange for done/unread, green for working. The number next to it counts the sessions that want you |
| Click a header | Collapses or expands it. Restart the tray and the collapse state is kept |
| Click "Not in a work item" | Collapses or expands too |

## 4. Start a session inside an item

Click **+** on an item's header.

| | Expected |
|---|---|
| Launcher | A Perch session window titled "New session · <item>" |
| Start a session in any folder | Its row appears under the item. The file gets it with `via: launched` |
| Then use /resume in that same window | The resumed session does **not** join the item (the binding is one-shot) |

## 5. `PERCH_WORK` from a plain terminal

The hook build has to be current: after building, restart the dev tray once so it reinstalls perch-hook.

```powershell
$env:PERCH_WORK = "<the item's id or name>"
claude
```

| | Expected |
|---|---|
| Within a scan or two | The new session shows under that item. The file has it with `via: terminal` |
| `~\.claude\sessions\<session id>.work` | Gone once the tray has handled it |
| Take the session out (untick it) | It stays out. The marker is gone, so nothing re-adds it |
| `PERCH_WORK = "no such item"` | The session goes under "Not in a work item". Create an item with that name and the session joins it |
| Then start a **Perch** session from that tray | It does **not** join anything (the tray and its sessions never inherit `PERCH_WORK`) |

## 6. `perch --work`

With the dev tray running, from a terminal:

| Command | Expected |
|---|---|
| `perch --work payments C:\some\repo` | A Perch session opens there and joins the item that "payments" resolves to |
| `perch --work payments --resume <id>` | Resumes that session and adds it to the item |
| `perch --work nothing-like-it` | The session still opens. The terminal says no work item matched |

## 7. The CLI

| Command | Expected |
|---|---|
| `perch work` | Active and paused items: id, status, name, session count |
| `perch work new "Billing emails"` | Prints the id and the PowerShell / bash / `perch --work` lines. The overlay shows the header within a second (folder watcher) |
| `perch work log billing "Spec signed off"` | Shows in the window's log at once, in normal (not muted) text |
| `perch work add billing <session-id>` / `remove` | Same effect as the row menu tick |
| `perch work done billing` | The header leaves the overlay. The item shows in the window under Done, once **Show done** is ticked |
| `perch work list --all` | Includes the done item |
| `perch work help` | The usage text |

## 8. The window

Open it from the tray (**Work items…**), a header's right-click (**Open work item**), or a row's **Work items ▸ → All work items…**.

| | Expected |
|---|---|
| A bare item | Only the title, **Sessions** (with a short how-to when it has none), two chips **+ Notes** / **+ Link**, and **Activity**. Nothing empty takes a whole section |
| List | **+ New work item** at the top turns into a name field (Enter creates, Esc or leaving it empty folds it back). Done items sit folded under **Done · N** at the bottom. A green **● N** shows only on items with running sessions |
| Name | Click it to rename in place. Enter or clicking away saves, Esc cancels |
| Status pill | **● Active ▾** opens Active / Paused / Done. Paused turns the overlay header into one dim "· paused" line, and clicking that line opens the item. Done removes the header |
| Colour swatch | A flyout of 8 colours. Picking one recolours the header and stripes |
| **⋯** | Open file, Show in folder, Copy activity as Markdown (`## <name>` then one `- yyyy-MM-dd HH:mm text` line per entry), Delete… (asks first; the sessions themselves are untouched) |
| Sessions | Running ones first, with their state in colour. Hovering a row shows ✕ (take it out) and a tooltip with how and when it joined. Click a running one to focus it. Click an ended one for Resume in Perch / Resume in terminal / View history. **+ New session** and **Add running ▾** sit on the section header |
| Notes | **+ Notes** opens a text box. **Save** renders it as markdown, and the section stays from then on. Start editing, then make a change from outside (e.g. `perch work log …`): your unsaved text stays |
| Links | **+ Link** shows a URL field (label optional). A bare URL is labelled "host · last segment". The URL keeps its tail when it's too long. Click a row to open it, hover for ✕ |
| Activity | Type and press Enter: your line appears at the top in full colour. Perch's own lines are smaller and muted. Only the last 5 show, with **Show all N** for the rest |
| A broken hand edit | Delete the closing `---` in a file. The item drops out of the list with a red note naming the file, and the file isn't rewritten |

## 9. Ended sessions

End a session that's in an item (exit claude).

| | Expected |
|---|---|
| Overlay header | "1 ended" appears at its right |
| Log | "<session> ended" |
| Window → Sessions | The row reads "ended" and is still resumable |

## Not covered

- **Drag a row onto a header, folder rules, suggestions, a "This week" view, Roost tab binding, focus mode.** These
  are the plan's W4, not built.
- **macOS.** Nothing here is Windows-specific, but it hasn't been run there.
