# Perch Social Feed — milestone plan

> The build schedule. Companion to `social-feed-plan.md` (why Supabase) and
> `social-feed-implementation.md` (schema, RLS, interfaces). This breaks the work into shippable
> milestones, each with a **goal**, **deliverables**, **definition of done**, and **commit
> checkpoints**. Branch: `social-feed`.

## Principles

- **Each milestone is independently mergeable and demoable** — nothing half-wired left in `main`'s path.
- **Prove the authorization model before the UI.** M0–M3 get to a working friends-and-posts loop
  on plain polling; real-time and overlay polish come after.
- **Everything network/OS goes behind a `Perch.Core` interface** (`ISocialClient`, `ISecretStore`).
  The feature is fully exercisable against a **fake client** so tests and `render` mode never need
  the network.
- **Commit as we go** — small, labelled commits at the checkpoints noted per milestone.
- **The whole feature stays behind `AppSettings.SocialEnabled`, default off.** A user who never
  toggles it sees zero change and Perch makes zero network calls.

## Prerequisites (need you / external, one-time)

These I can't do from here — they need your account and a couple of clicks. M0/M2 block on them:

1. **Create a Supabase project** (free tier) → gives a project URL + **anon** public key. Non-secret,
   shipped in the app.
2. **Register a GitHub OAuth app** (or enable GitHub in Supabase Auth) → client id/secret live in
   Supabase, not in Perch.
3. Decide where the **anon key + project URL** live in the app — proposed: a compiled-in default
   overridable by `PERCH_SUPABASE_URL` / `PERCH_SUPABASE_ANON_KEY` env for dev.
4. (Optional) **Supabase CLI + Docker** locally, so RLS policies get real pgTAP tests in M0. Without
   it, M0 ships the SQL + a documented manual test instead.

I'll flag exactly when each is needed and pause for the values.

---

## M0 — Backend foundation (schema + RLS)

**Goal:** the database exists and the authorization boundary is proven, before a line of app code.

**Deliverables**
- `backend/supabase/migrations/0001_init.sql` — `profiles`, `friendships`, `posts`, enums, indexes,
  the `are_friends()` + `find_profile()` functions (from `social-feed-implementation.md`).
- `backend/supabase/migrations/0002_rls.sql` — RLS enabled on every table + all policies.
- `backend/supabase/tests/rls_test.sql` — pgTAP: a non-friend **cannot** read a post; a pending
  (un-accepted) friend cannot; an accepted friend can; a third party can't see friendship rows;
  `find_profile` returns exact-handle only.
- `backend/supabase/README.md` — how to apply migrations + run tests.

**Definition of done**
- Migrations apply cleanly to a fresh Supabase project.
- Every RLS test passes (or, without the CLI, the documented manual checks pass in the SQL editor).
- `service_role` is used nowhere client-facing.

**Commits:** `feat(social): db schema + functions` · `feat(social): RLS policies + pgTAP tests`

---

## M1 — Core contracts + secret storage

**Goal:** the seams exist. Nothing talks to the network yet, but the app compiles against the
interfaces and a fake.

**Deliverables**
- `Perch.Core/Social/` — models (`Profile`, `Friend`, `FeedItem`, `AuthState`, `PostId`) +
  `ISocialClient` + `ISecretStore` interfaces.
- `Perch.Core/Social/FakeSocialClient` — in-memory impl for tests + `render` mode (seeded from
  `Rendering/SampleData`).
- `Perch.Platform.Windows/WindowsSecretStore` — DPAPI / Credential Manager.
- `Perch.Platform.Mac/MacSecretStore` — Keychain stub (no-op with TODO, per the port pattern).
- `PlatformServices` wiring under `#if WINDOWS`.
- `AppSettings.SocialEnabled` (default `false`) + `SettingsRegistry` descriptor (coverage test).
- Tests: `WindowsSecretStore` round-trip (set/get/delete); `FakeSocialClient` friend+post+feed logic.

**Definition of done**
- Solution builds on both heads (`net10.0-windows…` and `net10.0`).
- `dotnet test` green; `SettingsRegistryTests` satisfied.
- No behavioural change to the running app (setting is off, nothing calls it).

**Commits:** `feat(social): core models + ISocialClient/ISecretStore` · `feat(social): Windows secret store + fake client + tests`

---

## M2 — Auth flow (GitHub OAuth) + handle claim

> **Blocks on prerequisites 1–3.** First point I need the Supabase URL + anon key + GitHub enabled.

**Goal:** you can sign in and exist. End-to-end against the real project.

**Deliverables**
- `Perch.Core/Social/SupabaseSocialClient` — REST client scaffold; `SignInAsync` (loopback
  `HttpListener` + PKCE), token exchange, refresh-token persistence via `ISecretStore`,
  `GetMeAsync`, `ClaimHandleAsync`.
- Minimal UI: a "Sign in" entry (Settings → Social) and a **handle-claim dialog** (validates
  format + availability via `find_profile`).
- Sign-out clears the stored token.

**Definition of done**
- Fresh machine → sign in with GitHub in the browser → returns → claim a handle → `profiles` row
  exists. Restart the app → still signed in (token restored from secret store).
- Cancelling the browser flow leaves the app clean (no dangling listener).

**Commits:** `feat(social): supabase client + OAuth loopback sign-in` · `feat(social): handle claim dialog`

---

## M3 — Friends + posting + feed (polling)

**Goal:** the whole feature, minus liveness and overlay polish. Two accounts can be friends and
see each other's statuses.

**Deliverables**
- `SupabaseSocialClient`: `FindByHandleAsync`, `SendRequestAsync`, `RespondAsync`,
  `GetFriendsAsync`, `PostAsync`, `GetFeedAsync`.
- **Friends window** (`WindowHost.ShowOrFocus`): search by handle, pending requests (accept/decline),
  friends list. Closed in `App.CloseAuxWindows`.
- **Compose popup**: mood + 280-char box + Post, opened from the overlay header menu. Live char
  counter; manual only.
- Feed on a **60s poll** (`*MonitorHost`-style: off-thread → `Dispatcher.UIThread.Post`), surfaced
  in a simple list window for now (overlay strip is M4).

**Definition of done**
- Across two real accounts: A adds B → B accepts → both post → each sees the other's posts within a
  poll cycle. A non-friend sees nothing (RLS confirmed live, not just in tests).
- 280-char / empty-post limits enforced (DB rejects, UI guards).

**Commits:** `feat(social): friend requests + friends window` · `feat(social): compose + post` · `feat(social): polling feed`

---

## M4 — Overlay feed strip + notifications

**Goal:** it lives under the overlay and feels part of Perch.

**Deliverables**
- `Views/FeedStrip` — owner-drawn (through `OverlayDraw`; **text height from line-height**, not a
  magic number), newest-first, a few rows, avatar/mood + `@handle` + status + relative time.
- Gated via `Services/OverlaySettingsGates.Apply` + a canvas `SetFeed…` gate; `SampleData` seeds it
  for `render` mode and the settings live preview (`PreviewTarget`).
- Settings: `ShowFeedStrip`, `NotifyOnFriendPost`, `FeedPlacement` — each an `AppSettings` prop **and**
  a `SettingsRegistry` descriptor.
- `INotifier` "@x just posted" nudge, respecting existing quiet-hours/DND.

**Definition of done**
- `dotnet run … -- render <dir>` shows the strip at 1× and 1.5× without glyph clipping (dark + light
  themes).
- Toggling `ShowFeedStrip` in Settings shows/hides it live; preview pane reflects it.

**Commits:** `feat(social): owner-drawn feed strip + gates` · `feat(social): post notifications + settings`

---

## M5 — Real-time + firewall fallback

**Goal:** posts appear without a manual refresh, and it still works behind a strict proxy.

**Deliverables**
- `SupabaseSocialClient.SubscribeFeed` — Realtime WebSocket subscription (RLS-filtered inserts).
- **Graceful fallback:** if the socket won't establish/stay up, transparently drop to the M3 polling
  path — the strip behaves identically, just less instantly.
- Reconnect/backoff; unsubscribes on window close / sign-out / setting off.

**Definition of done**
- New friend post appears live (no manual refresh) on a normal network.
- With WebSockets blocked (simulated), the strip still updates via polling; no errors surface to the
  user.

**Commits:** `feat(social): realtime subscription` · `feat(social): polling fallback + reconnect`

---

## M6 — Safety hardening + sign-off

**Goal:** ready to actually let friends in. Close the threat-model checklist.

**Deliverables**
- **Block** (server-side; blocked user's posts vanish and they can't see yours) + **report**.
- Per-user **post rate limit** — Supabase Edge Function or a `posts` insert trigger counting recent rows.
- **Moderation kill-switch** (a flag that can disable a handle / pull content).
- Walk the pre-launch **security checklist** in `social-feed-implementation.md §7`; write the results
  into `backend/supabase/README.md`.

**Definition of done**
- Every checklist item ticked, with the RLS/rate-limit tests to back the ones that are testable.
- Blocking is effective in both directions and confirmed live.

**Commits:** `feat(social): block + report` · `feat(social): post rate limiting` · `chore(social): security checklist sign-off`

---

## Sequencing & checkpoints

```
M0 ─ db + RLS ......... (needs Supabase project)
M1 ─ contracts + fake . (no network; safe to land immediately)
M2 ─ auth ............. (needs URL + anon key + GitHub)  ─┐
M3 ─ friends/post/feed  ................................. ├─ working loop, polling
M4 ─ overlay strip ....................................... │
M5 ─ realtime + fallback ................................. ┘
M6 ─ safety + sign-off
```

Natural feedback checkpoints for you: after **M1** (seams look right?), after **M2** (auth feels
ok?), after **M3** (the loop works end-to-end), and before **M6** (ready to invite people?).

## Suggested starting point

**M1 first** — it's pure app code, needs nothing external, lands safely behind the off-by-default
flag, and gives us the seams (`ISocialClient` + fake) to build everything else against. M0 can run in
parallel the moment the Supabase project exists.
