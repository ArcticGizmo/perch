# Draw with Perch

A secret-arcade **draw-and-guess** game (Pictionary / Draw Something style), built as the arcade's second
networked multiplayer toy alongside Connect 4. Deliberately slow and asynchronous — a "do one turn a day"
game — so it leans on the persistent DB rows with Realtime only as a nudge.

**Status: SHIPPED as code on branch `draw-with-perch` (uncommitted).** Builds on both heads, the .NET suite is
green (1377 tests, incl. 42 new), and every owner-drawn screen is render-verified. **Not yet run live:** the
SQL migration hasn't been applied, so the wire calls / RPCs / Realtime delivery and the pgTAP suite are
compile-checked only (same posture Connect 4 shipped in). Kept **out of `CHANGELOG.md`** — the arcade is a
secret.

## How it plays

1. The drawer is offered **3 words** (one Easy, one Medium, one Hard) and picks one.
2. They draw it with a limited palette (12 fixed swatches, 3 brush sizes, eraser, undo, clear).
3. They submit; the opponent sees the drawing + the **letter-count hint** and types guesses (unlimited, or
   "Give up" to reveal).
4. On solve/give-up the **roles swap** and the ex-guesser draws the next round. The game runs indefinitely
   until someone abandons it.

**Scoring** (Draw Something-style): base by difficulty (Easy 10 / Med 20 / Hard 30); the guesser earns the base
minus 2 per extra guess (floored at half base); the drawer earns ⅔ base (7/13/20) when guessed. Running total
per player. **Online-only** — a bot can neither draw nor guess a freehand sketch, so there's no vs-computer mode.

**Word secrecy is deliberately NOT enforced** (user's call — "it's for fun"). The word rides as a plain,
party-readable column on the round; the *client* just masks it from the guesser until the round resolves. This
dropped a whole drawer-only-secret-table / column-hiding layer. The RPC state-machine is still authoritative for
turn/phase transitions and scoring.

## Architecture

The existing multiplayer stack is hard-wired to Connect 4 (`GameState.Moves` is `List<int>`, `GameSummary.Turn`
is a `Connect4Disc`), so Draw is a **parallel stack** mirroring the Connect 4 blueprint (see
`docs/connect4-plan.md`), not a reuse of its types.

### Pure engine (`src/Perch.Core/Games/`, net10.0, no Avalonia/IO)
- `DrawWords.cs` — `DrawDifficulty` enum + three curated word banks + `OfferWords(seed|Random)` → one word per
  tier. The word travels with the round, so the bank is never mirrored server-side.
- `DrawGuessing.cs` — `Normalize` (lower + strip non-alphanumeric), `IsCorrect`, `LetterHint` (per-token counts,
  e.g. "ice cream" → "3 5"). `Normalize` is mirrored in the `submit_draw_guess` SQL.
- `DrawScoring.cs` — `Base(diff)` + `Points(diff, attempts)` → `(Guesser, Drawer)`. Mirrored in SQL.
- `DrawStroke.cs` — `DrawStroke`/`DrawPoint` on a fixed 0..1000 canvas, `DrawPalette` (12 fixed `Rgb` swatches +
  3 sizes; paper/eraser = white index 1), and `DrawStrokeCodec.Encode/Decode` (compact JSON, lenient decode:
  clamps coords, caps totals). This is the wire format for the DB `strokes` column.
- Tests: `tests/Perch.Tests/DrawEngineTests.cs` (words/guessing/scoring/codec, 28 cases).

### Backend (`backend/supabase/migrations/20260924120000_draw_with_perch.sql`)
- Tables `draw_games`, `draw_rounds`, `draw_requests` (the invite carries the challenger's first drawing, like
  Connect 4's `first_col`). `strokes` is `text` (the codec's JSON), not `jsonb`.
- RLS: party-select on games/rounds; no client INSERT/UPDATE (RPCs own it); requests gated by
  `public.are_friends`. Grants: select on games/rounds, select+insert+delete on requests, execute on RPCs.
- RPCs (SECURITY DEFINER, re-check `auth.uid()`): `accept_draw_request` (creates game + round 1),
  `submit_draw_round` (new round; validates draw phase + turn), `submit_draw_guess` (checks the guess via the
  SQL mirror of `draw_normalize`, scores a solve via the SQL mirror of `DrawScoring`, swaps roles),
  `give_up_draw_round`, `resign_draw_game`. Plus a per-player rate-limit trigger, a `draw_games_delete` policy +
  `cleanup_old_draw_games` sweep + guarded `pg_cron`, and the guarded Realtime publication add.
- pgTAP `backend/supabase/tests/draw_test.sql` (20 checks) — run with `supabase test db`. **Not yet run.**

### Social layer (`src/Perch.Core/Social/`)
- `DrawModels.cs` — `DrawGameStatus`/`DrawPhase`/`DrawRoundStatus`, `DrawGameSummary`, `DrawRound`,
  `DrawGameState`, `DrawRequest`.
- `ISocialClient.cs` — a "Draw with Perch" region (Request/GetRequests/Accept/Decline/GetGames/GetGame/
  SubmitRound/SubmitGuess/GiveUp/Resign/Delete/SubscribeDrawGame); `SubscribeInbox`/`SendNudge` are shared.
- `InboxModels.cs` — `InboxKind` gains `DrawInvite`/`DrawInviteAccepted`/`DrawInviteDeclined`/`DrawNudge`.
- `SupabaseSocialClient.Draw.cs` (REST + RPCs, strokes via the codec), `FakeSocialClient.Draw.cs` (mirrors the
  rules in-memory + `Simulate*` seams), `SupabaseRealtime.cs` gains `RealtimeChannel.DrawRounds(gameId)`.
- Tests: `tests/Perch.Tests/FakeDrawGameTests.cs` (14 — accept seeds round 1, guess/score, give up, role swap,
  resign/delete, inbox).

### UI (`src/Perch.App/`)
- `Windows/DrawWithPerchWindow.cs` — the window (online + compose ctors, mirroring `Connect4Window`'s
  compose-then-invite lifecycle) + the owner-drawn `DrawBoard` with screens: **WordPick → Draw** (freehand
  pointer capture into `DrawStroke`s, palette/size/eraser/undo/clear, rendered as `StreamGeometry` polylines) /
  **Guess** (opponent's strokes + letter blanks + owner-drawn typed field via `OnTextInput` + Give up) /
  **Review** (reveal + points + thumbnail + Draw next) / **Waiting** / **Over**. `Snapshot*` poses for headless.
- `Windows/DrawWithPerchOnline.cs` — `DrawOnlineController` (subscribe nudge + 3s poll + off-thread calls).
- `Windows/DrawWithPerchLobbyWindow.cs` — templated lobby (your games w/ scores, challenges, start-a-game).
- `Windows/ArcadeMenuWindow.cs` — 6th card "PERCH DRAW" + a pencil glyph; `MenuH` bumped 752 → 864; a
  `launchDraw` ctor param.
- `Theming/Palette.cs` — `DrawSwatch(int)` cached brushes from `DrawPalette.Colors` (theme-independent: a
  drawing looks the same for both players).
- `Views/OverlayCanvas.Games.cs` — the GAMES strip generalized to show Draw games/challenges with a ✎ glyph +
  `SetDrawGames` and `DrawGameOpenRequested`/`DrawGameRequestResponded` events.
- `Services/SocialFeedMonitorHost.cs` — also polls `GetDrawGamesAsync`/`GetDrawRequestsAsync` and fires a
  "challenged you to Draw" notification (reuses `NotifyOnGameInvite`, `playful:true` so Quiet mode masks it).
- `App.axaml.cs` — `OpenDrawWithPerch` (opens the lobby when signed in, else Friends), `OpenDrawGame` /
  `StartDrawChallenge`, `_drawGameWindows` dict, `FindDrawGameWindow`, `DrawNudge` routing, `CloseAuxWindows`.
- `Rendering/HeadlessRenderer.cs` — `drawwithperch_{wordpick,draw,guess,review}_1x.png`.

## Verify

- Build both heads: `dotnet build perch.slnx`. Tests: `dotnet test tests/Perch.Tests/Perch.Tests.csproj`.
- Render: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <dir>` → eyeball
  `drawwithperch_*.png` and `arcade_menu_1x.png` (6 cards).
- pgTAP: `supabase test db` (after `supabase start`) for `draw_test.sql`.

## Owed (live verification, mirrors Connect 4's status)
- Apply the migration (`supabase db push --workdir backend`) and **verify the Realtime publication actually
  delivers** (no migration creates the publication).
- Run `draw_test.sql` against the live DB.
- A two-account draw→guess playtest. **A DEBUG puppet tester is wired** — `DebugSocialWindow` (Settings →
  Social → "Testing tool", gated by `PERCH_SOCIAL_DEBUG`) has a Games section — pick "Draw with Perch",
  then "Open both boards" or "Challenge me (from puppet)" — so once the migration is applied the whole loop is playable from one machine
  against the real backend. (Draw has no direct-create, so "both boards" = puppet challenges with a seeded
  doodle → you auto-accept.)

## Later / ideas
- Animated stroke replay on the guess screen (currently static).
- Scrambled letter tiles instead of free-text typing.
