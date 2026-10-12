# Sticky card headers — plan

Branch: `sticky-card-headers`.

## Problem

In the session thread (`Views/SessionThreadView`), an expanded collapsible card can be taller than the viewport.
Once its header scrolls off the top you lose what the content belongs to, and the only way to fold it again (or
get past it) is to scroll all the way back to the header.

Not every card has this problem. Expanded tool output, a background task's live tail and the plan card already
cap their height and scroll inside themselves (320/320/360 DIP). The ones that grow without bound are:

- **Edit/Write diffs** — up to 200 rows (`ToolCard.BuildDiffPanel`), open by default.
- **Thinking** — the full thought (`BuildThinking`).
- **Expanded tool folds** — a "Searched for 9 patterns, read 4 files" group holding many cards, any of which may be
  an open diff (`ToolGroup`).

## Design: one floating header strip, VS Code sticky-scroll style

Avalonia has no `position: sticky`, so rather than making each card's own header stick, the thread owns a single
overlay that pins a *summary* of the card you're inside to the top of the viewport.

- **Registration.** Each collapsible card registers a `Sticky` with its `ItemView`: the card's outer control (its
  bounds are the pinned span), whether it's open, how to collapse it, how to label it (glyph, verb, object, status),
  and its parent (a card folded inside a group). The registry lives on `ItemView`, so virtualization's
  dehydrate/hydrate drops and rebuilds it for free — a card on screen is never dehydrated.
- **Which card pins.** On every `ScrollChanged` (the same hook the jump buttons use) and after each batched update,
  find the item spanning the viewport's top line, then the open stickies whose top is above that line, whose
  bottom is still below it, and which are at least `MinStickyHeight` tall (short cards never flash a strip). At
  most two rows: a fold and the card inside it.
- **Push-up.** Each row sits at the viewport top, or higher once the card's bottom reaches it
  (`top = min(viewportTop + rowsAbove, cardBottom − rowHeight)`), so the strip slides off with the card's end
  rather than vanishing while there's still content under it.
- **Placement.** A `Canvas` layer in the scrolled content panel (beside the find-highlight and selection layers,
  topmost). Rows are positioned in content coordinates (`Canvas.Top = Offset.Y …`) and match their card's left
  edge and width, so a pinned row reads as the card's own header stuck in place.
- **Actions** (per row):
  - **Click the row → collapse** (the same gesture as clicking a card header), then scroll so the card's header
    lands at the viewport top. Without that re-scroll, folding a 4,000-DIP diff would drop you somewhere unrelated.
  - **↑** — jump to the card's header.
  - **↓** — jump past the card's end (to whatever follows it).

Hosted inside `SessionThreadView`, so the session window, History viewer, sub-agent tabs and Roost panes all get it.

## Things to watch

- **Streaming.** The tail card can grow while pinned; its row re-labels (status) on each batched update.
- **Find / selection.** The strip has an opaque background over the content. Drag-select only starts on text under
  `_stack`, so the strip's own text doesn't start one.
- **Jitter.** The strip is repositioned in `ScrollChanged`, which Avalonia raises after layout, before render — it
  should track without lag. If it visibly lags, move it out of the scrolled content into a viewport-level host.
- **Paths.** A file-tool row pins the verb and elides the *directory* of the object (`PrefixCharacterEllipsis`),
  per the path-trimming convention.

## Feel knobs

`MinStickyHeight` (how tall a card must be before it pins), the row's padding, and whether a click collapses or
jumps. All are constants in `SessionThreadView.Sticky.cs`. Tune after dogfooding.

## Verification

No automated UI coverage. `render` gains a `session_thread_sticky_*` frame: a thread scrolled into the middle
of a long diff, inside a fold, in dark and light. The feel (tracking, push-up, collapse landing) needs a live run.

## Status

- [x] Plan
- [x] Strip + registry + actions (`SessionThreadView.Sticky.cs`)
- [x] Headless render frame
- [ ] Dogfood: tracking smoothness, push-up, collapse landing, Roost compact panes
