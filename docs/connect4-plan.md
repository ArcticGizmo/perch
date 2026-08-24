# Connect 4

A secret-arcade Connect 4 toy, in two phases:

- **Phase 1 — local (SHIPPED as code):** a fourth game behind the arcade chooser. Hot-seat 2-player and
  vs-computer, fully offline.
- **Phase 2 — networked ("play a friend"):** the same engine driven by moves exchanged over the existing
  Supabase social backend. Not yet built; this doc is its blueprint.

The design rule that ties the phases together: **all rules live in the pure `Perch.Core` engine**, so the
networked mode is *transport only* — it never re-implements the game.

---

## Phase 1 — local (done)

Files:

- `src/Perch.Core/Games/Connect4Game.cs` — the pure, UI-free engine (`net10.0`, no Avalonia/IO/network):
  - `Connect4Game` — mutable board state, `Drop(col)` (returns the settled row, or -1 if illegal),
    `CanDrop`/`LandingRow` queries, `WouldWin(col, disc)` non-mutating hypothetical, `Reset`, and
    `Status`/`Turn`/`WinningLine`/`MoveCount`. Win detection scans the four axes through the placed disc.
  - `Connect4Ai` — a deterministic opponent: take an immediate win → else block the opponent's immediate
    win → else play the most central legal column. No randomness, so it's fully testable.
- `tests/Perch.Tests/Connect4GameTests.cs` — engine + AI unit tests (win in every direction, draw, full/OOB
  column rejection, no-move-after-win, `WouldWin` purity, AI win/block/centre).
- `src/Perch.App/Windows/Connect4Window.cs` — `Connect4Window` shell + owner-drawn `Connect4Board`
  (header with mode pills + turn indicator, 7×6 grid with a gravity drop animation and a hover drop-preview
  ghost, win-line glow, footer hints). Red = `Palette.ErrorBrush`, Yellow = `Palette.AwaitingBrush`; board
  frame/holes are theme chrome. Human is red vs the bot.
- Wiring: `ArcadeMenuWindow` gains a 4th card + `launchConnect4` param + a mini-board card glyph (and
  `MenuH` bumped 528→640); `App.axaml.cs` gains `_connect4Window`, `OpenConnect4()`, the close-all line, and
  the launcher arg; `HeadlessRenderer` renders `connect4_play_1x.png` via `Connect4Board.SnapshotPlaying()`.

Eyeball: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <dir>` →
`connect4_play_1x.png`, `arcade_menu_1x.png`.

Kept out of the changelog on purpose — the arcade is a secret (see the arcade memory note).

---

## Phase 2 — networked "play a friend"

Chosen transport (per the design decision): **authoritative DB tables + Supabase Postgres Changes**. The
database is the source of truth; Realtime only nudges the opponent's client to re-read. This mirrors how the
social feed already works (60s poll authoritative, Realtime as an accelerator) and reuses the existing
Phoenix-channel client. Broadcast/Presence are explicitly *not* used in this phase (a later polish could add
Presence for "opponent online" and Broadcast for lower-latency nudges).

### 2.1 Schema (`backend/supabase/migrations/`)

Two tables, keyed on `auth.users.id`, RLS scoped to the two players — mirror the `friendships_party_select`
pattern (`player_a = auth.uid() or player_b = auth.uid()`).

```sql
-- games: one row per match between two friends.
create table public.games (
  id          uuid primary key default gen_random_uuid(),
  player_red  uuid not null references auth.users(id) on delete cascade,
  player_yellow uuid not null references auth.users(id) on delete cascade,
  status      text not null default 'in_progress'      -- in_progress | red_won | yellow_won | draw | abandoned
              check (status in ('in_progress','red_won','yellow_won','draw','abandoned')),
  turn        text not null default 'red' check (turn in ('red','yellow')),
  move_count  int  not null default 0,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),
  check (player_red <> player_yellow)
);

-- moves: append-only, one row per disc dropped. (game_id, ply) is unique so a double-submit can't duplicate.
create table public.moves (
  id       uuid primary key default gen_random_uuid(),
  game_id  uuid not null references public.games(id) on delete cascade,
  mover    uuid not null references auth.users(id) on delete cascade,
  ply      int  not null,          -- 0-based move index; equals games.move_count at insert time
  col      int  not null check (col between 0 and 6),
  created_at timestamptz not null default now(),
  unique (game_id, ply)
);
```

RLS: on both tables, `select`/`insert` allowed only when `auth.uid()` is one of the game's two players.
`games` gets no client `update`/`delete` policy — the server (the move trigger below) owns those transitions.

### 2.2 Server-side move validation (the trust boundary)

**Never trust the client** (the project's whole philosophy). A `BEFORE INSERT` trigger on `moves`
(`SECURITY DEFINER`) enforces every rule the engine enforces, so a hacked client can't cheat:

1. The game exists and is `in_progress`.
2. `mover = auth.uid()` and it is that player's `turn`.
3. `ply = games.move_count` (rejects stale/replayed/out-of-order submits; the unique index is the backstop).
4. `col` is legal — not full (compute the landing row from existing `moves`).

Then an `AFTER INSERT` trigger (or the same function) re-derives board state from the move list, applies the
**same win/draw logic as `Connect4Game`**, and updates `games.status` / `games.turn` / `games.move_count` /
`updated_at`. Port the four-axis check into PL/pgSQL (small) — or keep it minimal by only checking the axes
through the just-placed disc, exactly as the C# engine does.

> Option to reduce PL/pgSQL: do validation + status in an RPC (`rpc/drop(game_id, col)`) instead of triggers,
> so there's one code path. Either works; the trigger keeps a raw `insert` honest even if a client bypasses
> the RPC. Recommend: RPC for the happy path **and** the trigger as the guard.

### 2.3 Realtime

- Add `games` and `moves` to the `supabase_realtime` publication. **Note:** no migration adds *any* table to
  that publication today (even `posts` relies on it being toggled in the dashboard) — so this must be an
  explicit migration or a documented dashboard step, and verified, or Postgres Changes won't fire.
- Generalize the Realtime client. `SupabaseRealtime.cs` / `SupabaseRealtimeConnection` currently hard-wire a
  single channel to `INSERT on public.posts` (`JoinPosts`, `TryParseInsert`). Parameterize the join to accept
  a `{schema, table, event}` and a row parser, then subscribe to `INSERT on public.moves` for the active
  game. A second connection is fine, or extend the one connection to multiple topics.
- On a `moves` INSERT (or a `games` UPDATE) for the current game, re-fetch and reduce the move list into a
  fresh `Connect4Game`, then repaint. A short fallback poll (~2-3s while a game is on screen) covers a
  dropped socket — the 60s feed poll is far too slow for live play.

### 2.4 Client surface (`Perch.Core/Social`)

Extend `ISocialClient` (+ `SupabaseSocialClient`, `FakeSocialClient` for tests):

- `CreateGameAsync(opponentUserId)` → new `games` row (invite). Reuse the friend graph
  (`FindByHandleAsync`/`GetFriendsAsync`) to pick an opponent — two friends already share an accepted edge.
- `ListGamesAsync()` → this user's active/recent games (for an "invites / your turn" list).
- `DropAsync(gameId, col)` → the RPC/insert; returns the updated game or a rejection.
- `GetMovesAsync(gameId)` / `SubscribeGame(gameId, onChange)`.

Client-side, still gate every move through the local `Connect4Game` for instant feedback, but treat the
server's derived state as authoritative (reconcile on each realtime/poll update). Optimistic drop → confirmed
or rolled back.

### 2.5 UI

Add a third mode to `Connect4Board` beyond `VsComputer`/`TwoPlayer`: **Online**. Entry point is an "invite a
friend" affordance (reuse the social roster). While online: show the opponent's @handle, disable input when
it's their turn, show "waiting for <handle>…", and surface connection state. Keep the same board rendering —
only the move source changes.

Quiet-mode / opt-in: if surfaced anywhere non-secret, respect the `QuietMode` layer and mark it
`playful:true` in `SettingsRegistry` (see the quiet-mode note). As part of the arcade it stays secret and
out of the changelog.

### 2.6 Milestones

1. **DONE (code):** Schema + RLS + grants + validated `drop_disc` RPC (with a PL/pgSQL `connect4_has_win`
   reconstruction) + `resign_game` RPC + realtime publication —
   `backend/supabase/migrations/20260821120000_connect4.sql`, plus `20260822120000_connect4_fix.sql`, a
   forward-fix that replaces `connect4_has_win` (the base used `array_fill('n', …)`, whose untyped literal
   fails at call time with "could not determine polymorphic type"; the fix casts to `text` and uses a flat
   42-cell array). pgTAP tests (alongside `rls_test.sql`) not yet written.
2. **DONE (code, in the migration):** `games`/`moves` added to `supabase_realtime` (guarded). **Delivery not
   yet verified against the live project.**
3. **DONE (code + tested):** `ISocialClient` game methods (`CreateGameAsync`/`GetGamesAsync`/`GetGameAsync`/
   `DropAsync`/`ResignGameAsync`/`SubscribeGame`); models `GameSummary`/`GameState`/`GameStatus`
   (`src/Perch.Core/Social/GameModels.cs`); the pure reducer `Connect4Game.Replay(columns)`;
   `FakeSocialClient.Games.cs` (mirrors the backend rules, `SimulateOpponentDrop` seam);
   `SupabaseSocialClient.Games.cs` (REST + the two RPCs). Tests: `Connect4ReplayTests`, `FakeSocialGameTests`.
4. **DONE (code + tested):** Realtime client generalized — `RealtimeChannel` descriptor +
   `RealtimeProtocol.Join`/`AccessToken(topic,…)`/`IsChange`; `SupabaseRealtimeConnection` now takes a channel
   + a raw-frame callback. `SubscribeFeed` reworked onto it; `RealtimeProtocolTests` still green.
5. **DONE (code, layout-verified):** `Connect4Board` Online mode (third mode) — renders "vs @handle", a Resign
   pill, "Your turn / Waiting for @handle…", the drop indicator, and the falling animation for both players'
   moves; reconciles against authoritative `GameState` and locks input off-turn / while a move is in flight.
   `Connect4OnlineController` (`Connect4Online.cs`) does the off-thread refresh (subscribe nudge + 3s fallback
   poll) and send, marshalling back to the UI. `Connect4LobbyWindow` lists your games + accepted friends to
   start/resume. `Connect4Window` gains the online ctor + a "Play a friend" pill (shown when Social is signed
   in); `App.OpenConnect4` passes `_social`. Headless snapshot: `connect4_online_1x.png`.
6. **DONE (code):** resign (RPC + client + fake + a "Rematch" pill on a finished online board that starts a
   fresh game vs the same opponent — in the debug tester it reopens *both* boards and is double-click-guarded);
   per-player `moves` rate-limit trigger (`20260822130000_connect4_moves_rate_limit.sql`, mirrors the posts one
   — 30 moves / 10s); **game removal + retention** (`20260822140000_connect4_game_cleanup.sql`): a
   `games_delete` RLS policy + `DeleteGameAsync` on the client (a "Remove" button per game in the lobby; moves
   cascade), plus a `cleanup_old_games()` sweep that drops decided games older than 2 days, scheduled daily via
   pg_cron when the extension is present (guarded). **Games are NOT reused** — every match (rematch included) is
   a new row, hence the removal + sweep. Block-awareness is enforced (games_create uses `are_friends`). pgTAP:
   `backend/supabase/tests/connect4_test.sql` (14 checks — RLS/RPCs, rate limit, delete; run via
   `supabase test db`).

**Testing tool (DEBUG builds):** the puppet-account tester (`DebugSocialWindow`, Settings → Social → "Testing
tool", gated by `SocialDebug.Enabled`) has a **"Start Connect 4 vs puppet (both boards)"** button. It signs a
second `SupabaseSocialClient` in via email/password (in-memory token store), befriends it if needed, creates a
real-vs-puppet game, and opens two online boards side by side — so the whole networked loop is playable from
one machine against the real backend, no second account/computer required.

**Verification status:** everything builds on both heads and the .NET suite passes (reducer, fake game flow,
realtime protocol); the online UI is layout-verified via the headless render. **Not yet verified live:** the
SQL migration hasn't been applied (`supabase db push --workdir backend`), so the `SupabaseSocialClient` wire
calls + the `drop_disc`/`resign_game` RPCs + Postgres-Changes delivery are compile-checked only. Next step is a
two-account live playtest and pgTAP tests for the new RLS/RPC.

### Watch-outs (carried from the social-layer review)

- Realtime publication is not enabled by any migration today — do it explicitly and verify.
- `TryParseInsert` only knows the `posts` row shape — a `moves` parser is new.
- Move legality/turn **must** be server-enforced; client checks are courtesy only.
- Feed poll cadence (60s) is unusable for live play — use a dedicated short fallback while a game is open.
