# PR "Start a session" dialog: layout proposals

Review of `src/Perch.App/Windows/PrSessionWindow.cs` (Part 5 of `docs/github-dashboard-plan.md`), from the live
screenshot (mobile-app #271, 125% scale) and the `captures/20261009-091946-github-dashboard-cards/v2`
renders. It assumes the two mechanical changes have been made: Checkout comes first, and the window is wider than 640.

Wireframes are drawn at roughly **10 DIP per character**, so a 76-character box is a 760-DIP window.

---

## 1. Diagnosis: why it feels dense

The live dialog is about 825 DIP tall (1030 px at 125%) for what is usually a single decision: *confirm and launch*.

1. **Every choice explains itself in two wrapped lines.** The two tiles, New worktree, Existing worktree and No
   worktree each carry a muted description. At 640 DIP most of them wrap ("...clone of the / repo."), which adds
   about **11 lines of grey prose** that the eye has to scan past to find the controls.
2. **Some things are said twice.**
   - The worktree location is written in the New-worktree description (".claude\worktrees\pr-271 inside your
     checkout") and again in the Strategy combo directly beneath it.
   - "Your own working copy isn't touched" and "Changes land in your working copy" restate what the option name
     already implies.
   - The Checkout tile's "Work on the PR's code in your clone of the repo" adds nothing to the word *Checkout*.
3. **The nesting is four levels deep.** Window, then the "How" section, then a bordered sunken panel, then the
   worktree radios, then the Strategy row indented 28 px. Each level adds its own border, padding or indent, and
   the sunken panel sits directly under two bordered tiles, so there are three boxes stacked in about 120 px.
4. **There are four label styles and three alignments.**

   | Label | Style | Alignment |
   |---|---|---|
   | "How should it be checked out?", "Prompt" | Fg semibold | Stacked above |
   | "Clone", "Account" | Muted | 76-px label column |
   | "Worktree" | Muted 12 SubLabel | Stacked above |
   | "Strategy" | Muted 12 | Inline, indented 28 |

   No single left edge or label column ties the form together.
5. **The clone path is cut off at the end.** "C:\Users\me\Documents\git\work\mobile-ap" loses
   the one part that identifies the clone, which breaks the CLAUDE.md path rule. The cause is that a committed
   `AutoCompleteBox` shows its raw text scrolled to the start. Separately, `FolderSearchBox`'s suggestion rows trim
   the parent directory with `CharacterEllipsis` (from the right), where the rule says the head should be elided.
6. **A disabled option still costs two lines.** "Existing worktree / This repo has no other worktrees." is greyed
   out, but it takes as much room as a live option.
7. **Help text shows up even on success.** "Found among your projects. Search to use another clone." appears in
   the common case. The note only matters when there are several clones, none, or an error.
8. **The prompt controls are scattered.** The quick-prompt combo is right-aligned, far from its "Prompt" label, and
   the Read-only badge hangs off the window edge. "Show the full prompt Perch will send" is accent-coloured, so in
   an orange theme it competes with the primary button. Opening the preview grows the dialog with no cap (924 DIP
   in the `choice` render) and pushes Account below the fold.
9. **Account is placed with the prompt, but it depends on the folder.** It is resolved per folder, yet it sits
   under the prompt, on the 76-px label column that nothing else in that section uses.
10. **The header takes four lines before the first choice.** "Start a session" (16 bold) outranks the PR title
    (13.5). The PR is the subject of the dialog and should be the strongest text.

### Width recommendation: **760 DIP** for any single-column layout

- **Paths.** At 640, the clone box has about 395 DIP, roughly 60 characters at 12.5 pt (the screenshot cuts off at
  character 60 of 68). At 760 it has about 520 DIP, roughly 80 characters, which fits the typical
  `C:\Users\<name>\Documents\git\<group>\<repo>` path whole. Strategy labels such as
  ".claude\worktrees\pr-271 inside your checkout (Claude Code's default)" also stop clipping.
- **Line length.** The task box at 760 runs about 105 characters per line. That is about the limit before prose
  becomes hard to track, so going past 800 makes the prompt worse.
- **Screen fit.** At 125%, 760 DIP is 950 px, comfortable on a 1366-px laptop. It is also clearly narrower than its
  980-DIP owner (the dashboard), so it reads as a dialog rather than a second window.
- **The rule still needs enforcing.** A long path will always exist somewhere, so the path display must pin the
  name and elide the directory regardless of width (see each proposal).

A two-column layout (proposal B) needs about **960**.

---

## 2. Three proposals

### A. "Resolved summary + Change": progressive disclosure (760 wide)

The resolved checkout reads as a short summary. Details only open when the user wants to change them. In the
common case (clone found, sensible default worktree) the user scans three rows and presses Launch.

```
+--------------------------------------------------------------------------+
| START A SESSION                                                       x  |
| Vendor the payments SDK and drop the old shim                        |
| acme/mobile-app #271                             |
| vendor-payments-sdk -> master   (Checks failing)                     |
+--------------------------------------------------------------------------+
| WORKSPACE                                    [ Checkout | No checkout ]  |
| +----------------------------------------------------------------------+ |
| | Clone      mobile-app  ...\Documents\git\work Change| |
| | Worktree   New  .claude\worktrees\pr-271  on perch/pr-271      Change | |
| | Account    Acme Corp (default)                             [v] | |
| +----------------------------------------------------------------------+ |
|                                                                          |
| PROMPT     [ Fix failing checks        v ]                               |
| +----------------------------------------------------------------------+ |
| | Its CI checks are failing. Find out why with `gh pr checks 271 --repo| |
| | acme/mobile-app` and `gh run view <run-id>   | |
| | --repo acme/mobile-app --log-failed`, ...    | |
| +----------------------------------------------------------------------+ |
| > Full prompt                                                            |
+--------------------------------------------------------------------------+
|                                        [ Copy command ] [Launch in Perch]|
+--------------------------------------------------------------------------+
```

**Worktree "Change" open.** The row expands in place into a segmented control plus one row for the selected mode
(the same grammar as C):

```
| | Worktree   [ New | Existing | None ]                          Done  | |
| |            Location [ .claude\worktrees\pr-271     Claude default v ] | |
| |            (or) Worktree [ checkout . feature/checkout-form       v ] | |
| |            (or) ! On main, not the PR's branch: edits land in your    | |
| |                   working copy.                          (WarnBrush)  | |
```

**Clone "Change" open.** The summary row is replaced by the existing search box. Committing it (pick, Enter or
leaving the box) collapses it back to the summary.

```
| | Clone      [ Search your projects for mobile-app... ] [Browse] | |
```

**Requirements:**

| Requirement | How A covers it |
|---|---|
| PR header | Small-caps eyebrow, then the title as h1, then repo #n, then branches and pills on one line. |
| How | A segmented `Checkout \| No checkout` on the section heading, with Checkout first and selected. |
| Clone | Summary row; Change opens `FolderSearchBox` and Browse. |
| Clone notes | Silent when one clone is found. Several found: an inline `1 of 2` chip that opens the search. None found: the row text says so. |
| New worktree | Shown as the summary. The strategy is the Location combo in the expanded row. |
| Existing worktree | A segment, disabled (with a tooltip) when the repo has none. |
| No worktree | A segment, hidden for bare repos. |
| Prompt | Combo beside the PROMPT label, Read-only badge right after it, task box, and a `> Full prompt` disclosure. |
| Account | Third summary row, shown only when there is a choice. It belongs with the folder that drives it. |
| Footer | Status on the left, Copy, then Launch (primary). |

**Long paths.** The summary row is a `DockPanel`. The leaf ("mobile-app", Fg, 13) is pinned
left. The parent ("C:\Users\...\work", Muted, mono) is the fill child with
`TextTrimming = PrefixCharacterEllipsis`, so it collapses to "...\git\work" and then to "...". Change is pinned
right. The raw path only appears while editing, and the full path is in the row's tooltip.

**No clone found:**

```
| | Clone      None found. Perch will make a fresh clone (perch/pr-5)  Find... | |
```

The Worktree row is hidden, since a fresh clone has nothing to choose. Find... opens the search.

**No checkout.** The card shrinks to one muted line plus Account:

```
| | Nothing is checked out. Claude reads the PR with gh, in an empty       | |
| | scratch folder.                                                        | |
| | Account    Acme Corp (default)                             [v] | |
```

**Auto-picked No worktree is never silent.** When the default lands on "No worktree" and the checkout is not on
the PR branch, the summary row carries the WarnBrush consequence line without needing to be expanded. The
existing-worktree and on-PR-branch defaults read as plain summaries.

**Height:** about 500 DIP collapsed, down from about 825.

**Trade-offs:**

- **Pro:** the biggest density cut, and it matches how the dialog is actually used (defaults are right most of the
  time).
- **Pro:** it fixes the path rule naturally, because the display is no longer a text box.
- **Pro:** the warning about the user's working copy becomes more visible, not less.
- **Con:** the worktree alternatives are one click away rather than visible.
- **Con:** the expand/collapse state is new code (two bools and visibility swaps), and keyboard order has to
  include the Change links.
- **Reuse:**
  - `FolderSearchBox.Configure` as is.
  - `SettingsControls.Segmented` for How. For Worktree it needs a small extension: a programmatic select, because
    `UpdateWorktreeOptions` moves the default, and per-segment `IsEnabled`/`IsVisible`.
  - `Workspace()`, `UpdateWorktreeOptions`, `ApplyMatch` and `RunAsync` keep their logic. `UpdateWorktreeText`
    becomes "build one summary plus one consequence line".
- **Cost:** medium, about 150-200 changed lines, all in `PrSessionWindow`. No Core change.

### B. Two columns: workspace left, prompt right (960 wide)

```
+----------------------------------------------------------------------------------------------+
| START A SESSION                                                                           x  |
| Vendor the payments SDK and drop the old shim                                            |
| acme/mobile-app #271 . vendor-payments-sdk -> master (Checks failing)|
+----------------------------------------------------------------------------------------------+
| WORKSPACE                                | PROMPT          [ Fix failing checks         v ]  |
| [ Checkout | No checkout ]               | +-----------------------------------------------+ |
|                                          | | Its CI checks are failing. Find out why with  | |
| Clone                                    | | `gh pr checks 271 --repo acme/mobile-app` and | |
| [mobile-app       ...\git\work]          | | `gh run view <run-id>                         | |
|                               [Browse...]| | ...                                           | |
| Worktree                                 | |                                               | |
| (o) New       [.claude\worktrees\pr-271v]| |                                               | |
| ( ) Existing  none in this repo          | +-----------------------------------------------+ |
| ( ) None      on vendor-payments-sdk | > Full prompt                                     |
|                                          |                                                   |
| Account  [ Acme Corp (default) v ] |                                                   |
+----------------------------------------------------------------------------------------------+
|                                                          [ Copy command ] [Launch in Perch]  |
+----------------------------------------------------------------------------------------------+
```

The columns are about 420 (left) and 500 (right), each a PATHology-style `panel` card on the sunken page.

**What moves:**

- Everything about the folder (How, Clone, Worktree, Account) goes left.
- The prompt goes right and grows to fill the left column's height, so the task box gets about 10 lines with no
  scrolling.
- The full-prompt preview opens inside the right column. Either it replaces the task box via a `Task | Full prompt`
  toggle, or it sits below with a capped height. The window height then stays fixed.

**Long paths.** The left column is narrow (about 300 DIP for the box, roughly 46 characters), so the clone path
**will** truncate. The name-pinned display (as in A) is mandatory here, not just nice to have. That works against
the user's "more width so paths fit" request unless the window goes to about 1040.

**No clone found.** The Clone field shows the "none found, fresh clone" note and the Worktree group is hidden,
which leaves the left column mostly empty.

**No checkout.** The left column drops to the segmented control, one explanatory line and Account. The two halves
look badly unbalanced.

**Height:** about 520 DIP.

**Trade-offs:**

- **Pro:** best for long or edited prompts, and nothing is hidden.
- **Con:** the window is as wide as its owner (the 980 dashboard).
- **Con:** the left column is cramped for paths and strategy labels.
- **Con:** the no-checkout and not-found states look lopsided.
- **Con:** the eye has to zig-zag (left, right, then back down to the footer).
- **Reuse:** all the current controls carry over; it is mostly a `Grid` restructure.
- **Cost:** medium. Layout is easy, but getting the preview right inside a fixed-height column is the fiddly part.

### C. Compact form rows: one label column, segmented choices (760 wide)

Keep everything visible, but give it one grammar. Every row is `label | control`, on one 88-px label column. Both
choices become segmented controls. There is at most **one** muted line, for the consequence of the current choice.

```
+--------------------------------------------------------------------------+
| START A SESSION                                                       x  |
| Vendor the payments SDK and drop the old shim                        |
| acme/mobile-app #271                             |
| vendor-payments-sdk -> master   (Checks failing)                     |
+--------------------------------------------------------------------------+
| Workspace  [ Checkout | No checkout ]                                    |
| Clone      [ mobile-app  ...\Documents\git\work ] [Browse...] |
| Worktree   [ New | Existing | None ]                                     |
| Location   [ .claude\worktrees\pr-271                Claude default  v ] |
|            New branch perch/pr-271. Your working copy is untouched.      |
| Account    [ Acme Corp (default)  v ]                              |
|                                                                          |
| Prompt     [ Fix failing checks        v ]  (Read-only)                  |
| +----------------------------------------------------------------------+ |
| | Its CI checks are failing. Find out why with `gh pr checks 271 ...   | |
| |                                                                      | |
| +----------------------------------------------------------------------+ |
| > Full prompt                                                            |
+--------------------------------------------------------------------------+
|                                        [ Copy command ] [Launch in Perch]|
+--------------------------------------------------------------------------+
```

The Location row changes with the Worktree segment:

| Worktree | Row label | Control |
|---|---|---|
| New | Location | strategy combo |
| Existing | Worktree | existing-worktree combo |
| None | (no control) | the consequence line, in WarnBrush when not on the PR branch |

The Existing segment is disabled with a tooltip when the repo has none. None is hidden for bare repos. There is
no sunken panel and no tiles.

**Long paths.** The Clone box stays a `FolderSearchBox`, but when it isn't focused it shows a name-pinned overlay
(a `DockPanel` with the leaf and a head-elided directory, laid over the inner TextBox and hidden on focus). The
cheaper fallback is `CaretIndex = Text.Length` after commit, which scrolls the tail into view. The strategy combo
gets an item template with the relative path in mono on the left and the source ("matches yours", "Claude
default", "from ignore file") muted on the right.

**No clone found.** The Clone box shows its placeholder. The consequence line reads "None found. Perch will make a
fresh clone on perch/pr-5." Worktree and Location are hidden.

**No checkout.** Only the Workspace row shows, with the consequence line "Claude reads the PR with gh in an empty
scratch folder." Then Account.

**Height:** about 580 DIP.

**Trade-offs:**

- **Pro:** nothing is hidden, the alignment is fully consistent, and it is the smallest conceptual change from
  today.
- **Pro:** it keeps the screen stable while the user explores.
- **Con:** segments lose the per-option explanation (the one consequence line plus tooltips carry it).
- **Con:** it is still roughly twice the height of A's common case.
- **Reuse:**
  - `FolderSearchBox` as is.
  - `LabeledRow` becomes the only row helper (widen `LabelColumn` to 88).
  - `SettingsControls.Segmented` with the same extension A needs.
- **Cost:** low to medium, about 120 lines. It deletes `ChoiceTile`, `OptionRow`, `Indented` and the sunken panel.

---

## 3. Recommendation: **A**, using C's row grammar for its expanded editors

A cuts the most for the case that happens most often, and its summary is also where the path-rule fix and the
working-copy warning naturally live. Using C's segmented and Location rows inside A's expanded Worktree editor
means the dialog has one form grammar, not two. B's best feature, a roomy prompt, is better bought cheaply by
raising the task box's `MinHeight`.

### Control by control

1. **Window:** `Width = 760`.
2. **Header.**
   - "Start a session" becomes a small-caps eyebrow (11, SemiBold, LetterSpacing 1.5, Muted, the PATHology `phead`).
   - The PR title becomes the h1 (15-16, SemiBold).
   - `meta` drops the branches onto the pills line: `branches` in mono Muted, then the pills.
3. **How tiles** (`ChoiceTile` x2, `tiles`): replace with `Segmented(["Checkout", "No checkout"])` right-aligned on
   the WORKSPACE eyebrow row. Delete both tile descriptions.
4. **`_checkoutPanel`:** keep a single card (`Surface` with a 1-px `Border`, radius 10, the dashboard card's look)
   holding the summary rows. Drop the sunken background so cards don't stack inside cards.
5. **Clone.**
   - New summary row: label "Clone", a `DockPanel` with the leaf pinned and the parent dir using
     `PrefixCharacterEllipsis` (mono, Muted), and a "Change" link. Tooltip = full path.
   - Change swaps in the existing `_cloneBox` + Browse. Commit swaps back.
   - `_cloneNote` is hidden on a single match. For several matches it becomes an `n found` chip; for none and for
     errors it becomes the row text.
6. **Worktree.**
   - New summary row: "New · `<relative path>` · on perch/pr-n", or "Existing · `<leaf>` · `<branch>`", or "Your
     checkout · `<branch>`", plus Change.
   - Expanded: `Segmented(["New", "Existing", "None"])` (extended for disabled/hidden segments and a programmatic
     select), then **one** row: `Location [strategy]`, `Worktree [existing]`, or nothing.
   - Delete `_newDesc`, `_existingDesc`, `_noWorktreeDesc` and `SubLabel("Worktree")`.
7. **Consequence line:** one TextBlock under the Worktree row. It is visible **even when collapsed** when the
   choice edits the user's working copy off the PR branch (`Palette.WarnBrush`), and hidden otherwise.
8. **Strategy combo** (`_layoutBox`): an item template with the relative path in mono and the source muted on the
   right. Shorter suffixes: "matches yours", "Claude default", "from ignore file".
9. **Account:** moves into the workspace card as its third row (still only shown when there is a choice). Same
   label column.
10. **Prompt head:** "PROMPT" eyebrow, then the combo **directly beside it** (not right-aligned), then the
    Read-only badge immediately after.
11. **Task box:** `MinHeight` from 84 to about 110, so the default quick prompts show whole at 760.
12. **Preview:**
    - The `LinkButton` becomes a Muted chevron disclosure (`▸ Full prompt` / `▾ Full prompt`), following the
      collapsible-section convention. Accent stays reserved for Launch.
    - Cap `_previewBox` at about 220 DIP with a vertical `ScrollViewer`, so opening it no longer pushes the window
      off-screen.
13. **Footer:** unchanged. The status line stays empty unless the dialog is busy or there is an error.

### Small wins for whichever layout is chosen

- **Path rule:** never leave a committed path in a text box scrolled to its start. At minimum, set
  `CaretIndex = Text.Length` after `SetFolder`.
- **`FolderSearchBox` suggestion rows:** the parent dir uses `TextTrimming.CharacterEllipsis`, which should be
  `PrefixCharacterEllipsis` under the rule. This is shared with the session launcher, so make it a separate,
  one-line change.
- **One label style:** Muted 12.5 on one label column. Section heads use the small-caps eyebrow. Drop the stacked
  Muted "Worktree" sub-label and the 28-px indented "Strategy" label.
- **Spacing:** use `StackPanel.Spacing` (8 within a group, 18 between sections) instead of per-control margins.
  The current 16/10/12/14/18 top margins are what makes the rhythm feel uneven.
- **Copy trims:**

  | Today | Proposed |
  |---|---|
  | How should it be checked out? | WORKSPACE |
  | Just prompting. Claude reads the PR through gh, with no code on disk. | Claude reads the PR with gh. Nothing is checked out. (No-checkout state only) |
  | Work on the PR's code in your clone of the repo. | (drop) |
  | Found among your projects. Search to use another clone. | (drop; silence means found) |
  | Found 2 clones of acme/web. Using the first; search to pick another. | `1 of 2` chip |
  | No clone of tools/cli among your projects. Search or browse for one, or Perch will make a fresh clone in its data folder (on branch perch/pr-5). | None found. Perch will make a fresh clone (perch/pr-5). |
  | Checks out the PR on branch perch/pr-271 in .claude\worktrees\pr-271 inside your checkout. Your own working copy isn't touched. | New · .claude\worktrees\pr-271 · on perch/pr-271 |
  | This repo has no other worktrees. | (disabled segment, tooltip "No other worktrees") |
  | Your checkout as it is, on main, not the PR's branch. Changes land in your working copy. | On main, not the PR's branch: edits land in your working copy. (Warn) |
  | Show the full prompt Perch will send | ▸ Full prompt |

- **Tile accent brush:** `StyleTile` allocates `new SolidColorBrush(Palette.Accent) { Opacity = 0.08 }` on every
  toggle. If the tiles survive, hoist it to a field. This is moot under A or C.
