# Roost tabs — design & checkpoint plan

Status: **PLAN ONLY** (2026-10-01). Builds on the merged Roost (`docs/roost-plan.md`, branch `roost-merge`).
Suggested branch: `roost-tabs`, cut from `roost-merge`.

## The ask

1. The left rail can be ordered **alphabetically** or **by status** (the user picks).
2. Remove the **Tiled / Main + stack / Zoom** toggle and the per-count snap flyout. Replace them with a
   **layout per tab** model:
   - Tabs across the top. Each tab has its own layout.
   - A **tab painter**: start from a list of defaults (today's snap templates), then drag/split regions on a
     bounded grid. Layouts can be **saved** and reused.
   - A fixed **first "tab"**: one full-screen region. A session that isn't in any tab opens here (one-offs).
     It isn't really a tab, but it keeps placement consistent.
3. In a tab you can **drag sessions in from the rail**, **click an empty region to pick a session**, and
   **drag sessions already in the tab onto each other to swap**.
4. Tabs are **renamable** and show the same **status light + number** treatment, scoped to their sessions.
5. Clicking a rail session that's in no tab **focuses it in the first tab**.

## Decisions

User review 2026-10-01: D1, D2, D4 and D6 are **confirmed** (D1 with a caveat, D6 with an addition). D5 is
**changed** to "drop the chips". D3, D7, D8 and D9 still stand as recommendations.

| # | Question | Decision | Why |
|---|----------|----------------|-----|
| D1 | Can one session be in two tabs at once? | **Confirmed, with the door left open.** **For now, a session lives in at most one region of one tab** (the Focus tab included), and dropping it somewhere else *moves* it. Uniqueness is a *policy* in `RoostTabSet`, not a property of the data model. See "Leaving the door open for multi-placement" below. | A rail click then has exactly one destination. An Avalonia control can have only one parent, so one `SessionPane` per session keeps one feed and one thread per session. Two panes of one session would also mean two composers for one conversation. |
| D2 | Does the stage still pull urgent sessions in by itself (today's `RoostStage` admit/bump)? | **Confirmed. No: placement is entirely the user's.** Tab lights, the rail, a "needs you elsewhere" pill and the taskbar flash carry urgency instead. | Auto-bumping fights a layout you painted on purpose. That's the whole point of tabs. |
| D3 | Painter grid | **12 × 12 unit grid**, regions snap to unit lines, **min span 2 units** (1/6 of the stage), **max 8 regions per tab**. | 12 divides into halves, thirds, quarters and sixths. 8 keeps the expanded-thread cost bounded (each region is a full `SessionThreadView`). 60/40 becomes 7/5 (58/42). |
| D4 | What happens to Zoom? | **Confirmed.** Kept as an **in-tab temporary maximise**: double-click a header (or Ctrl+Shift+Z) to fill the tab with that pane, and do it again (or press Esc) to restore. Nothing moves between tabs. | It costs almost nothing (the tile panel draws one slot full-size), and tmux users expect `prefix z`. |
| D5 | Title-bar chips (`N need you`, `N working`…) | **Changed: drop the status chips entirely.** The title bar becomes the Roost mark, then `+ New session`. The one chip with an action of its own, **"N hidden"** (reopen closed panes), moves to the **rail footer**. | With tab lights, a status-grouped rail and the bottom pills, the chips only repeat counts shown three other ways, and filtering a hand-painted tab makes no sense. |
| D6 | Keys | **Confirmed, and shown in the UI.** **Ctrl+0** Focus · **Ctrl+1–9** tab N · **Ctrl+Tab / Ctrl+Shift+Tab** cycle tabs · **Alt+1–8** region N in the active tab · **Ctrl+.** next needing you (across tabs) · **Ctrl+T** new tab · **F2** rename the active tab · **Ctrl+Shift+Z** zoom. Where the keys show up is under "Key discoverability" below. | Today Ctrl+1–9 picks a pane. Tabs are the stronger use of the browser-style chord. |
| D7 | Tab light + number | **Dot** = the most urgent state among the tab's sessions: ApiError red > AwaitingInput yellow > Done·review attn > Working ok > Quiet/empty idle (or none). It pulses like the rail's needs-you ring. **Number** = the tab's sessions wanting you (Needs you + Done·review), in the dot's colour, hidden at 0. | The same rule as the overlay's Roost badge (`OverlayCanvas.RoostBadge`) plus the Done·review state the rail already shows. |
| D8 | First run (no tabs saved) | Create one tab, **"Main"**, with the default layout for the live session count (`RoostTemplates.ForCount`), filled in roster order. Plus Focus. | The window isn't empty the first time it opens. After that, nothing auto-fills (D2). |
| D9 | Layout edit vs. assigned sessions | Region identity is stable through edits. A **split** keeps the session in the original half and leaves the new half empty. A **merge** keeps the survivor's session and sends the other back to the rail. **Applying a preset** maps sessions over in reading order, and extras go back to the rail. | Nothing gets silently lost. The worst case is a session going back to the rail, which is one drag to undo. |

### Leaving the door open for multi-placement (D1)

Uniqueness is enforced in one place, so lifting it later is a policy change, not a rewrite:

- **Data model already allows duplicates.** A tab's `Cells` is `regionId → paneKey`, and nothing in the
  shape stops two cells (in one tab or several) from naming the same key. The persisted form is the same
  either way, so lifting the rule later needs no settings migration.
- **One rule, one switch.** `RoostTabSet.Assign` is the only method that evicts a key from its old
  placement, and it does that behind a single `UniquePlacement` policy (`true` for now). Every other method
  is written for a key that may have several placements.
- **The API returns many.** `Locate(key)` returns `IReadOnlyList<RoostPlacement(TabId, RegionId)>` (0 or
  1 entries for now). The rail-click rule is written as "the most recently shown tab among its placements",
  which reads the same today and still holds with duplicates.
- **UI is the part that would change**, and it's contained. Views are keyed by **placement**
  (`tabId/regionId`) rather than by session key, while feeds stay keyed by **session**. `RoostFeed` already
  exposes one shared `Conversation` plus a multicast `Changed`, so two panes can watch one feed. For now
  each session has one placement, so this is a naming choice that costs nothing. Lifting D1 later would
  still need:
  - only the focused copy's composer is live; the others show "Reply in ‹tab›", so a single conversation
    never has two drafts;
  - drag-from-rail gets a modifier (Ctrl+drag = "also show here");
  - `IsOnScreen` = *any* placement visible.

### Key discoverability (D6)

- **Rail footer `⌨ Keys` row** (under "N hidden"). It opens a flyout cheat-sheet: the D6 table, grouped
  as Tabs / Regions / Sessions. This is the "describe it somewhere in the sidebar" home.
- **Tab tooltips carry their chord** ("Main · Ctrl+2"; Focus says "Ctrl+0"), and the `+` tooltip says
  "New tab · Ctrl+T". Region placeholders say "Alt+3" in their corner when hovered.
- **The bottom hint line** is rewritten for the new chords and kept short ("Ctrl+1–9 tab · Alt+1–8 region
  · drag to place · Ctrl+. next needing you · ⌨ more"). Clicking it opens the same cheat-sheet.
- **One source of truth**: the chords live in a small Core table (`RoostKeys`: chord, description, group).
  The handler, the cheat-sheet, the tooltips and the hint line all read from it, so they can't drift apart.
  A test asserts every chord the handler matches has a row.

## Shape of the window

```
┌ ◫ Roost                                                                 [+ New session] ┐
├──────────┬─────────────────────────────────────────────────────────────────────────────┤
│ Sort: ◉Status ○A–Z │ [◻ Focus] [● Main 2] [● Infra] [○ Docs] [+]                       │
│ NEEDS YOU          ├─────────────────────────────────────────────────────────────────────┤
│ ● api     Main     │ ┌───────────────┬────────────────┐                                 │
│ DONE · REVIEW      │ │ api           │ web            │                                 │
│ ● web     Main     │ │               ├────────────────┤                                 │
│ WORKING            │ │               │  ┄ + Add ┄     │  ← empty region: click to pick  │
│ ● infra   Infra    │ └───────────────┴────────────────┘                                 │
│ ● scratch          │                                                                     │
│ ─────────────      │                                                                     │
│ 2 hidden           │                                                                     │
│ ⌨ Keys             │                                                                     │
├──────────┴─────────┴─────────────────────────────────────────────────────────────────────┤
│ Ctrl+1–9 tab · Alt+1–8 region · drag to place · ⌨ more       [2 not in a tab · 1 needs you] │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Rail**: a sort toggle in the rail header (`Status` | `A–Z`, persisted). *Status* is today's grouped rail.
  *A–Z* is one flat list by `DisplayName` (ordinal ignore-case, ties in first-seen order), with each row's
  status dot. Every row shows a faint **tab tag** naming the tab it lives in (none = not placed). The rows in
  the active tab get a brand left-bar. The "new" tag means a session that has never been placed or viewed.
- **Tab strip** (below the title bar, over the stage only): Focus first (◻ glyph, never renamed or closed),
  then the user's tabs, then `+`. A tab is `dot · name · count`. Double-click or F2 renames it inline.
  Right-click gives Rename / Edit layout… / Duplicate / Close tab. Dragging a session onto a tab header
  drops it into that tab's first empty region, or onto the tab to swap with its focused region when it has
  none. Optional: drag tabs to reorder (T5 stretch).
- **Stage**: the active tab's regions. An empty region is a dashed placeholder ("Drop a session here or
  click to pick"). Clicking it opens a picker flyout: sessions grouped by status, each with its current tab
  tag, plus "+ New session here" at the bottom.
- **Bottom pill**: `N not in a tab · M need you`. A click sends the most urgent unplaced session to Focus.
  A second pill, `K need you in other tabs`, appears when the active tab is calm but another isn't. A click
  goes there.

## Interaction rules (all in Core, unit-tested)

| Gesture | Effect |
|---|---|
| Click a rail row, session **in tab T** | Activate T, focus its region (un-zoom T if another pane was zoomed). |
| Click a rail row, session **in no tab** | Put it in Focus (replacing any one-off there, which goes back to unplaced) and activate Focus. |
| Drag a rail row → **empty region** | Assign it there. If it was elsewhere, that region empties (D1). |
| Drag a rail row → **occupied region** | It takes the region. The occupant goes to the dragged session's old region if it had one (a swap), otherwise back to unplaced. |
| Drag a pane header → another region in the **same tab** | Swap (empty target = move). |
| Drag a pane header → a **tab header** | Move into that tab (see the tab strip above). |
| Click an **empty region** | Picker flyout → assign the chosen session (moving it, D1). |
| Pane menu "Remove from tab" (replaces "Keep on stage") | Region empties, session goes back to unplaced. |
| Pane menu "Close" (today's hidden panes) | Unchanged: hidden until the session leaves the scan. Its region empties. |
| Session **ends** | Its pane lingers greyed in place (`EndedLinger`), then the region empties. |
| **Take over / `--resume`** of a lingering session | The new pid inherits the old pid's region (see Gotchas). |
| `+ New session` (title bar) | The new Perch session goes into Focus. From an empty region's picker it goes into that region. |
| Close a tab | Its sessions go back to unplaced. The tab before it is activated. |

## Architecture

### New, UI-free (`Perch.Core/Data/Roost/`, unit-tested)

- **`RoostGridLayout`**: an immutable set of `RoostRegion(Id, Row, Col, RowSpan, ColSpan)` on the 12×12
  unit grid.
  - `Validate()`: an exact tiling (no gaps or overlaps), every span ≥ 2, ≤ 8 regions. A layout that fails
    reads back as `Full`, for hand-edited or newer files.
  - `SplitVertical(id)` / `SplitHorizontal(id)` split at the nearest unit line to the middle. They return
    null if a half would fall below the min span or the region cap would be passed.
  - `MoveDivider(line, orientation, at, to)` uses **try-then-validate**. Find the maximal run of regions
    whose edge sits on that line at the grabbed point, shift every edge in it, then accept the result only if
    `Validate()` passes. That keeps T-junction cases correct without special-casing them.
  - `Merge(id)`: fold the region into the neighbour that together with it forms a rectangle, preferring the
    larger one, then left/top. Returns null when no rectangular merge exists, so the × button is disabled.
  - `FromTemplate(RoostSnapTemplate)`: today's templates become the **built-in presets** (TwoPlusThree's
    6-column grid scales ×2, Wide60 → 7/5).
  - `ToShape()` → the existing `RoostTemplateShape`, so `RoostTilePanel` draws a custom layout unchanged.
  - `ReadingOrder` for preset re-mapping (D9).
- **`RoostTabSet`**: Focus plus an ordered list of `RoostTab(Id, Name, Layout, Cells: regionId → paneKey)`,
  with `ActiveId` and a per-tab `ZoomedRegion`.
  - It enforces D1 and implements the interaction table: `Locate(key)`, `Assign(tab, region, key)`
    (returns the displaced key), `Swap`, `Unassign`, `Show(key)` (the rail-click rule), `AddTab`,
    `RenameTab`, `CloseTab`, `ApplyLayout(tab, layout)` (D9).
  - `Prune(liveKeys)` and `Rekey(oldKey → newKey)` for adoptions.
  - Persisted form: tabs with cells as `pid/sessionId` tokens, the same rule as `RoostRoster.PersistedOrder`
    (a recycled pid never inherits a region). Tokens resolve on the first roster update after load.
    Unmatched ones become empty regions.
  - Raises `Changed` when the persisted form moves. App saves on it (debounced; drags fire often).
- **`RoostTabStatus.For(tab, panes)`** → `(dot state, count)` per D7. Pure.
- **`RoostRailSort`** enum (`Status`, `Alphabetical`) + **`RoostRoster.RailFor(sort)`**: *Status* = today's
  `Rail`, *Alphabetical* = one pseudo-group. The roster stays the one owner of grouping.
- **Saved layouts**: `RoostSavedLayout(Name, Regions)`. The painter lists built-ins first, then saved ones.

### UI (`Perch.App`)

- **`RoostWindow`**:
  - Loses `RoostLayoutMode`, the segmented toggle, the snap button and flyout, `_mainGrid`/`_stack`,
    `_zoomHost`, the title-bar chips (D5) and `_onStage`. The "N hidden" chip becomes a rail-footer row.
  - Gains a tab strip and **one `RoostTilePanel` per tab** (Focus included) inside `_stage`, with only the
    active one visible. D1 means every pane host sits in exactly one tab's panel, so switching tabs is a
    visibility flip, never a re-parent. Focus and scroll survive, and that is the same reason swaps don't
    re-parent today.
  - The **warm** list generalises: panes in hidden tabs stay materialised, least recently shown first.
    Past `WarmLimit` they're `Park()`ed and rebuild when their tab shows again.
  - Drag/drop extends today's `AttachDragSource`/`MoveGhost`. Hit-testing covers the active tab's region
    hosts **and** the tab headers.
- **`RoostTabStrip`** (new view): tab buttons with an inline rename `TextBox` (placeholder via
  `PlaceholderText`, never `Watermark`), right-click menu, the `+` button and drop-target highlight. The
  dot pulse rides the window's existing `_pulseTimer`.
- **`RoostLayoutPainter`** (new view, shown in place of the active tab's stage while editing, at the real
  stage aspect):
  - Left strip: preset and saved thumbnails (today's `TemplateThumb`), plus "Save layout…" and, on a saved
    one, Rename / Delete.
  - Canvas: each region is an outline showing its session's name, or "empty". Hovering a region shows
    **⇆ split / ⇅ split / × merge** buttons. Dividers can be dragged with a resize cursor and snap to the
    unit grid, with faint unit gridlines while dragging. Invalid moves stop at the last valid position.
  - Footer: region count `5 / 8`, then **Done** and **Cancel** (Cancel restores the layout from before
    the edit).
  - Esc is Cancel. The painter only edits a working copy of `RoostGridLayout`, and Done calls
    `RoostTabSet.ApplyLayout`.
- **`SessionPane`**: the "Keep on stage" menu item becomes "Remove from tab". Double-click on the header
  becomes in-tab zoom.

### Wiring & settings

- `App` owns the `RoostTabSet` next to `_roostRoster` (so tabs survive closing the window, like pins do
  today). It is fed adoptions and prunes on each `UpdateRoost`.
- **`AppSettings`** (all are UI state, so they're added to `SettingsRegistryTests.NotSettings` like the
  current `RoostLayout*`):
  - `RoostTabs` (the persisted `RoostTabSet`)
  - `RoostActiveTab`
  - `RoostSavedLayouts`
  - `RoostRailSort`
  - Retired: `RoostLayout`, `RoostLayoutByCount`, `RoostOrder`. Delete the properties; unknown JSON keys
    already deserialise harmlessly, and `SalvageMerge` is unaffected. Check that `SettingsRegistryTests`
    and the settings-file resilience tests still pass.
- **AttentionSeen**: `IsOnScreen(sessionId)` means the session is in the active tab and isn't hidden by
  another pane's zoom. `PlacementChanged` fires on a tab switch, zoom, assign or remove, so
  `ReplayRoostSuppressed` owes toasts for whatever just left view.
- **Toast parity**: a session needing you in an *inactive* tab is not seen, so its toast fires. That's
  correct, and it's the replacement for D2's lost auto-admit.

## Gotchas to design for

- **Adoption re-keys panes.** `RoostRoster.AdoptContinuations` moves a resumed or taken-over session to a
  *new pid key* in the ended pane's slot. Tabs keyed by pid would drop it. The roster must report
  `Adopted (old → new)` from each `Update`, and `RoostTabSet.Rekey` must apply it before `Prune`. This needs
  its own test.
- **`/clear` keeps the pid but changes the session id.** The key is stable, but the **persisted token** must
  be re-derived from the current session id on save (as `PersistedOrder` already does).
- **Closed (hidden) panes** leave `Panes`. `Prune` must treat them as gone from regions, and Reopen does
  *not* restore a region. It goes back to unplaced. Simple and predictable.
- **Composer drafts across tab switches**: a hidden tab's pane is only hidden, so drafts survive. A
  *parked* pane past `WarmLimit` rebuilds its thread. Check whether `SessionPane` keeps the draft text across
  `Park()`. If it doesn't, never park a pane with a non-empty draft.
- **Typing hold**: with D2 nothing moves by itself, so `TypingHold`'s stage-hold role goes away. Keep it
  only if `SessionPane`'s held styling still needs it, and delete it otherwise.
- **Painter text sizing**: the painter is control-built (not owner-drawn), but region captions in thumbnails
  must still not clip. Use measured text, not fixed heights (CLAUDE.md).
- **This retires a teammate's fresh work.** `RoostStage` and the per-count snap flyout
  (`dionfoster/roost-snap-layouts`) go. The templates live on as painter presets, and `perch bench-roost`
  must be re-pointed at tab switch / drag / assign. Worth a heads-up to them before T7 deletes it.

## Checkpoints

The green gate for every checkpoint is the one from `roost-plan.md`: both heads build, the full .NET suite
passes, `render <dir>` is eyeballed for the Roost shots, and there's one commit per checkpoint.

### T1: Rail sort (independent; can ship first) ✅ d5cfda8
*As built:*
- The roster exposes `RailAlphabetical` (a property, rebuilt with `Rail`) instead of a `RailFor(sort)` method.
- The window's rail renders "sections" (status groups, or one `ALL SESSIONS` list), and its rebuild
  signature includes the sort.
- The `Status | A–Z` toggle is docked above the rail's scroll area.

### T2: `RoostGridLayout` ✅
*As built:*
- **Dividers are the smallest run the touching regions fit inside**, not the maximal run of the line. In a
  2×2 grid each half of the middle line moves on its own, and beside a tall region the divider is that
  region's full height.
- `MoveDivider` clamps to `DividerRange` rather than try-then-validate. Because both sides move along the
  whole run, the tiling can't break, and every edit still goes through `Create` (validation) anyway.
- The painter's × is `Remove(id)`: the neighbour from `MergeTarget` (larger, then left/top) grows into the
  space, and the removed region's id goes. `RoostRegion` is a record struct that round-trips through
  System.Text.Json as-is.

### T3: `RoostTabSet` + `RoostTabStatus` + `RoostKeys` ✅
*As built:*
- `RoostTabSet.Sync(panes, roster.Adopted)` is the one entry point per roster update. It follows
  re-keyed panes, settles persisted cells on its first call and empties regions whose session left (ended
  and dropped, or closed).
- Persisted cells that haven't been settled yet are kept in `ToState`, so a save before the first scan
  doesn't lose them.
- Presets are applied through `RoostGridLayout.AdoptIds(previous)`, which renumbers the preset in reading
  order. That leaves `ApplyLayout` with one mode: sessions follow region ids.
- `RoostKeys.Resolve(keyName, mods)` takes Avalonia's `Key.ToString()`, so the window's handler is driven
  by the same table the cheat-sheet reads. Enter / Esc are listed rows with no command (the pane handles
  them).
- `RoostTabStatus` returns a `RoostLight` (None < Quiet < Working < Done < Awaiting < Error) plus a count.
- `UniquePlacement = false` is covered by a test (the D1 door).

### T1 / T2 / T3 original scope
- `RoostRailSort` + `RoostRoster.RailFor`, a header toggle in the rail, the persisted `RoostRailSort`, and
  the row rebuild signature including the sort.
- Tests: alphabetical order, ties, ended panes last, a sort flip rebuilding rows.
- Render: `roost_rail_alpha_1x.png`.

### T2: `RoostGridLayout` (Core only)
- Regions, `Validate`, split/merge/move-divider, presets from `RoostSnapTemplate`, `ToShape`, reading
  order, and a JSON round-trip with invalid layouts falling back to Full.
- Tests: every preset validates, a split at min span is refused, a T-junction divider moves correctly and
  an illegal one is refused, merge picks, the cap of 8.

### T3: `RoostTabSet` + `RoostTabStatus` (Core only)
- The interaction table, D1 uniqueness, Focus semantics, D9 re-mapping, prune/rekey, the persisted tokens
  and seeding (pid recycled → empty), and `Changed`.
- Roster: expose `Adopted` from `Update`.
- `Locate` returns a list, and uniqueness sits behind the `UniquePlacement` policy (D1 door).
- `RoostKeys` chord table (D6).
- Tests: one per row of the interaction table, adoption keeping a region, recycled pid, D7 dot/count
  precedence, and one test with `UniquePlacement = false` proving the model holds duplicates.

### T4: Tabs in the window (the big swap)
- `RoostWindow` on `RoostTabSet`: per-tab `RoostTilePanel`s, Focus, a minimal tab strip (switch only, fixed
  names), empty-region placeholders + picker, rail drag-to-region, header swap, rail-click rule, in-tab zoom,
  both bottom pills, AttentionSeen/`PlacementChanged`, keys (D6) via the `RoostKeys` table, and the
  warm/park policy across tabs. Views are keyed by placement and feeds by session (the D1 door).
- Drop the title-bar chips. "N hidden" moves to the rail footer, next to the `⌨ Keys` row and its
  cheat-sheet flyout.
- Remove the mode toggle, snap flyout, Main + stack and Zoom mode from the window. Leave the Core types in
  place until T7.
- App wiring + persistence + D8 first-run seeding.
- Render: Focus with a one-off, a 3-region tab with one empty region, the picker open, a drag hovering a
  region, zoomed.

### T5: Tab management + lights
- `+` / rename (inline, F2) / duplicate / close, the D7 dot + count with the pulse, a session dragged onto a
  tab header, the rail tab tags and the active-tab left-bar.
- Stretch: drag to reorder tabs.
- Render: a tab strip with every light state, renaming in progress.

### T6: Layout painter + saved layouts
- `RoostLayoutPainter` edit mode, the preset/saved strip, split/merge buttons, divider drag with snapping,
  Done/Cancel, and Save/Rename/Delete for saved layouts (`RoostSavedLayouts`).
- Render: painter on a preset, mid-divider-drag (gridlines), the saved-layouts strip, a refused merge
  (× disabled).

### T7: Cleanup + docs
- Delete `RoostLayoutMode`/`RoostLayout`, `RoostStage` (+ tests), `RoostLayoutByCount`/`RoostOrder`/
  `RoostLayout` settings, the "Keep on stage" pin path, and `TypingHold` if it's unused.
- Re-point `RoostBench`.
- Update `roost-plan.md` (superseded sections), the `CLAUDE.md` Roost mention if any, and the `CHANGELOG`
  Unreleased section.
- Owed live checks list (below).

## Owed live checks (log results here as they're done)

- [ ] Tab switch keeps a half-typed composer draft and the thread scroll position.
- [ ] A session needing you in a background tab: the tab dot pulses, the "need you in other tabs" pill
      appears, the toast fires, and the taskbar flashes when the Roost is inactive.
- [ ] Take over in Perch from a pane in a tab: the new Perch session lands in the same region.
- [ ] Perch restart (update) with sessions still running: the tabs come back populated, and a recycled pid
      doesn't.
- [ ] Painter divider drag feels right at 1× and 1.5× and never produces an invalid layout.
- [ ] 8 expanded regions + 3 tabs: tab switch and drag latency (`perch bench-roost`).

## Open questions (beyond D1–D9)

- Should a region optionally **bind to a project** (cwd), so a new session started in that repo fills it
  automatically? That would make tabs useful again after a reboot (when every pid is new). It's a natural
  follow-up and out of scope here.
- A third rail sort, **"By tab"** (rail grouped under tab names)? It's cheap once T5 lands.
