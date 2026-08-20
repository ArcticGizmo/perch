# Perch Social — feedback round plan

> Companion to `social-feed-plan.md` / `social-feed-implementation.md` / `social-feed-milestones.md`.
> This addresses the first round of real-use feedback on the shipped Social feature. Work happens on a
> dedicated branch off `main`; each phase is independently mergeable and demoable.

**Branch:** `social-feedback`

## Feedback → change map

| # | Feedback | Phase |
|---|----------|-------|
| 1 | Reactions should include **cry** and **sad** emoji | P1 |
| 2 | Reactions raised against **your own** status should be visible to you | P3 |
| 3 | There's no way to **remove a friend**, only block | P1 |
| 4 | Friends list should show only **"invited"** as a status — drop "friend" | P1 |
| 5 | Friend requests can **duplicate** (ordered pair); dedupe + prevent, migration preserves existing | P2 |
| 6 | No way to **change your handle** without losing friendships (id vs handle separate) | P2 |
| 7 | **Removed/blocked** friends should disappear from the roster/dropdown | P1 |
| 8 | Add **"Show large reactions"** — a wobbling, poppable bubble when someone reacts to your post | P4 |
| 9 | Reaction chips should have **no outline unless the row is hovered** | P1 |

## Decisions (confirmed with user)

- **Large reactions (#8) render on a full-screen layer** — a new transparent, click-through, topmost
  window covering the work area; bubbles rise from the bottom and wobble up the whole height, poppable
  on click.
- **"Show large reactions" is ON by default.** Whenever an effect plays, the full-screen layer shows a
  small inline **"Turn off big reactions"** control at the bottom of the screen that flips the setting off.

---

## Phase 1 — Friends management & list cleanup (client + UI, low risk)

Quick, high-value wins. No schema change (the RLS `friendships_delete` policy already permits either
party to delete an edge; blocks already exist).

**1a · Cry + sad reactions (#1)** — add `😢` and `😔` to `ReactionChoices`
(`OverlayCanvas.Feed.cs:33`). Emoji rendering is already owner-drawn via `OverlayDraw.Emoji`, so nothing
else changes.

**1b · Remove a friend (#3)**
- `ISocialClient.RemoveFriendAsync(Guid otherUserId)` — new method.
- `SupabaseSocialClient`: `DELETE /rest/v1/friendships?or=(and(requester.eq.{me},addressee.eq.{other}),and(requester.eq.{other},addressee.eq.{me}))`.
  RLS `friendships_delete` already allows this for either party.
- `FakeSocialClient`: remove the edge from `_edges`.
- `FriendsWindow`: add a **Remove** button to `FriendRow` (accepted → "Remove", pending-outgoing →
  "Cancel request"), behind a `ConfirmDialog` ("Remove @handle? You'll stop seeing each other's posts.").
- After a successful remove/block, **nudge the overlay to re-poll** (see 1d) so the roster updates
  immediately rather than on the next 60s tick.

**1c · Friends list status label (#4)** — in `FriendsWindow.FriendRow`, show `"invited"` for
`FriendshipState.Pending` (an outgoing request you sent) and **no tag** for `Accepted`. Drop the
"friend"/"requested" text.

**1d · Removed/blocked friends leave the roster (#7)**
- **Root cause:** `GetRosterAsync` builds the roster from `GetFriendsAsync` (raw `friendships` rows) and
  keeps every `status = 'accepted'` edge. A *block* lives in a separate table, so a blocked friend still
  reads "accepted" and stays in the overlay roster (with an empty status). Fix in **both** clients:
  - `SupabaseSocialClient.GetRosterAsync`: fetch `GetBlockedAsync()` and exclude those ids from the
    roster entries.
  - `FakeSocialClient.GetRosterAsync`: exclude `_blocked` from the accepted-friend entries (today it
    yields them with `latest = null`).
- Once **1b** lands, a removed friend's edge is gone, so it drops out naturally.
- **Immediate refresh:** add an `Action? GraphChanged` hook the `FriendsWindow` raises after
  block/unblock/remove/respond; the App relays it to `SocialFeedMonitorHost.RefreshSoon()`.

**1e · Reaction outline only on row hover (#9)** — `DrawChip` currently always outlines a "mine" chip
(`OverlayCanvas.Feed.cs:382`). Thread the row's hover state into `DrawFriendRow`/`DrawComposeRow` →
`DrawChip` and draw the accent pen only when the owning row is hovered
(`_hoveredFriendRow == index`, or `_hoveredSocialCompose` for the "you" row). The mine-fill stays; only
the border is gated.

**Tests (P1):** `FakeSocialClientTests` — `RemoveFriendAsync` drops the edge; `GetRosterAsync` excludes
blocked accepted friends. **Render check:** `-- render` to confirm chips (no stray outline) at 1×/1.5×,
dark + light.

**Commits:** `feat(social): remove friend` · `fix(social): drop blocked friends from roster` ·
`feat(social): cry/sad reactions + invited-only label + hover-gated chip outline`

---

## Phase 2 — Identity & de-duplication (backend migration)

**2a · Unordered friendships + dedupe (#5)**
- **Problem:** the PK is `(requester, addressee)`, so `A→B` and `B→A` are two rows. If both people
  request each other, the graph holds a duplicate pair, and re-requesting after a reverse request slips
  past the same-direction `merge-duplicates`.
- **Migration** `..._friendship_dedupe.sql` (idempotent):
  1. Collapse duplicate unordered pairs, **preserving** the relationship. For each
     `least(requester,addressee), greatest(...)` group with >1 row: if any row is `accepted` (or the two
     are opposite-direction `pending`, i.e. a mutual request) keep a single **accepted** row; otherwise
     keep the earliest `pending` row. Delete the rest.
  2. Add a unique index on the unordered pair:
     `create unique index friendships_unordered on friendships (least(requester,addressee), greatest(requester,addressee));`
- **`SendRequestAsync` (both clients):** before inserting, look for any existing edge in **either**
  direction. If a *reverse pending* edge exists → PATCH it to `accepted` (mutual request = instant
  friends). If any edge already exists → no-op. Else insert `pending`. This also stops the new unique
  index from throwing on a reverse insert.
- **`rls_test.sql`:** add a case that a reverse request cannot create a second row, and that a mutual
  request ends `accepted`.

**2b · Change your handle (#6)**
- **Already supported at the data layer:** `profiles.id` (the stable auth uuid friendships reference) is
  separate from the mutable `handle`, and `ClaimHandleAsync` upserts by `id` — so renaming is an
  `UPDATE profiles set handle=…` that leaves every friendship intact. The gap is purely UI + a
  data-loss bug in the call.
- **Bug to fix:** `ClaimHandleAsync` writes `display_name` and `mood_emoji` from its args; the rename
  call must pass the **existing** `me.DisplayName` / `me.MoodEmoji`, or a rename would blank them. (The
  registry/upsert uses `merge-duplicates`, which overwrites the columns sent.)
- **UI:** in the Settings → Social page "signed in with a handle" state (`RefreshSocialPage`, ~line 731)
  add a **"Change handle…"** button opening a small dialog prefilled with the current handle; on submit
  call `ClaimHandleAsync(newHandle, me.DisplayName, me.MoodEmoji)`. Reuse the existing 409 → "@x is
  already taken" and 400 → format-hint handling. Optionally surface the same action in `FriendsWindow`.

**Tests (P2):** `FakeSocialClientTests` — mutual request converges to a single accepted edge; rename
preserves friendships and display name/mood. Document the migration + manual check in
`backend/supabase/README.md`.

**Commits:** `feat(social): dedupe + unordered friendship uniqueness` ·
`feat(social): reverse-request auto-accept` · `feat(social): change handle`

---

## Phase 3 — Reactions on your own status (#2)

Prerequisite for Phase 4's detection (reuses own-post reaction data).

- **Model:** add `IReadOnlyList<ReactionGroup> MyReactions` to `RosterSnapshot` (value-equality updated
  like the existing hand-written `Equals`/`GetHashCode`, so a fresh list per poll doesn't force a
  relayout).
- **`SupabaseSocialClient.GetRosterAsync`:** include `myLatest.Id` in the `FetchReactionsAsync` batch and
  populate `MyReactions`.
- **`FakeSocialClient.GetRosterAsync`:** group reactions for `myLatest` via the existing
  `GroupReactionsLocked`.
- **`OverlayCanvas.Feed.cs` `DrawComposeRow`:** render reaction chips for your own latest post, reusing
  `ChipsFor` / `DrawChip`, laid out left of the "Update/Post" action (mirroring `DrawFriendRow`'s right
  cluster). You can't react to your own post, so the "+" affordance is omitted; chips are display-only
  (a tap could still toggle a combined-summary tooltip). Apply the #9 hover rule here too.
- **`SampleData`:** seed a couple of reactions on the "you" post so `render` mode and the settings
  preview show them.

**Tests (P3):** roster carries own-post reactions. **Render check:** compose row shows chips at
1×/1.5×, dark + light, no glyph clipping (line-height discipline).

**Commits:** `feat(social): show reactions on your own status`

---

## Phase 4 — Large reactions (#8): full-screen wobble bubbles

**4a · Setting**
- `AppSettings.ShowLargeReactions` (**default true**) + a `SettingsRegistry` `Toggle` on the Social
  surface (id `social-large-reactions`, keywords: reactions, bubbles, celebrate, animation, big) so the
  coverage test passes. `PreviewTarget.None`.
- Settings → Social divider row + a matching entry in Features → Social.

**4b · Detection (in `SocialFeedMonitorHost`)**
- Track the reaction state on **your own latest post** across polls (reuse `RosterSnapshot.MyReactions`
  from Phase 3). Prime on the first poll after activation (the backlog isn't news, mirroring
  `NotifyNewFriendPosts`). On a subsequent poll, any **new** reaction (an emoji whose count rose, or a
  new emoji) fires `Action<string /*emoji*/, int /*newCount*/> onReactionToMyPost`.
- Scope: latest post only (matches what's displayed) — noted as a deliberate limit.
- Gated on `SocialEnabled` **and** `ShowLargeReactions`; also suppressed under Do-Not-Disturb when
  `CloseFeedInDoNotDisturb` is set (consistent with toast suppression).

**4c · The full-screen bubble layer** — a new `Windows/ReactionBubbleWindow` (owner-drawn, following the
existing overlay-window discipline in `LiveOverlayWindow` / `AchievementCardWindow` /
`StickyNoteWindow`):
- Transparent, borderless, **topmost, non-activating, click-through except over live bubbles**, sized to
  the work area of the screen holding the overlay. Reuses the established Win32 window-style plumbing;
  anything genuinely OS-specific goes behind the existing platform seam rather than raw Win32 in the view.
- Each bubble: the reaction emoji in a soft rounded chip that **rises** from the bottom with a **sine
  wobble** (horizontal offset = `A·sin(ω·t + φ)`), gentle scale-in at spawn, fades near the top.
  Animated by a `DispatcherTimer` (~60fps while bubbles exist; self-stops when empty — the glow-timer
  pattern in `OverlayCanvas.Feed.cs`).
- **Pop on click:** a hit-test over each bubble's current rect; a hit triggers a short burst
  (expand + fade + a few particle specks) and removes it. Regions with no bubble stay click-through so
  the layer never steals input from the desktop.
- **Inline off switch (per the decision):** while any effect is showing, a small **"Turn off big
  reactions"** pill sits centered at the bottom of the screen; clicking it sets
  `ShowLargeReactions = false`, saves, and dismisses the layer.
- Coalescing: multiple reactions in one poll spawn a small staggered burst; a cap (e.g. ≤8 concurrent)
  avoids a swarm.

**4d · Wiring (`App.axaml.cs`)**
- Construct the layer lazily; feed it emojis from the monitor host's `onReactionToMyPost`.
- Respect the master gates; tear down on sign-out / Social-off / exit (via `CloseAuxWindows`).

**Tests / verification (P4):** unit — the detection diff fires only on genuinely-new reactions and never
on priming or your own toggles. The animation/window is UI, so eyeball it: a manual trigger (the existing
`DebugSocialWindow` puppet can react to your post) plus a dev-only "spawn test bubble" hook, and
`render` for the static chip/pill look. No automated coverage for the animation itself (consistent with
the rest of the owner-drawn UI).

**Commits:** `feat(social): show-large-reactions setting + detection` ·
`feat(social): full-screen wobble reaction bubbles`

---

## Sequencing

```
P1 friends mgmt + list cleanup ... (no schema; ship first)
P2 dedupe + change handle ........ (one migration; preserves data)
P3 own-status reactions .......... (model + roster + compose row)
P4 large reactions ............... (depends on P3's MyReactions)
```

P1 and P2 are independent; P3 precedes P4. Natural review checkpoints after **P1** (the everyday
friends UX), after **P2** (migration applied cleanly to the live project), and before **P4** ships the
splashy animation.

## Cross-cutting checks

- **Both `ISocialClient` impls stay in lockstep** — every contract change lands in `SupabaseSocialClient`
  **and** `FakeSocialClient`, with the Fake mirroring the server's authorization rules (the test/render
  reference).
- **Owner-drawn discipline** — all new overlay text sizes from line-height via `OverlayDraw`; no magic
  pixel heights (the stat-card clipping gotcha).
- **Settings coverage** — any new `AppSettings` prop gets a `SettingsRegistry` descriptor (the coverage
  test fails otherwise).
- **Off-thread IO → dispatcher marshal**, guarded against a window closed mid-flight, per the
  `*MonitorHost` pattern.
- **Migration preserves existing data** (#5) and is idempotent/re-runnable.
