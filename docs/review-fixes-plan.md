# Review fixes plan (adversarial review, 2026-09-29)

These are the tracked fixes from the adversarial review of `main` at `57717cb`. The review covered two things: code quality and performance, and security and abuse. The full evidence behind each finding is in `.claude/artefacts/adversarial-review-2026-09-29.md`: the attack scenario, the file:line references, and the "checked and fine" list.

Fixes land on the branch `review-fixes`. The exception is roost-only findings, which land on `roost` (see CP26).

## Priority key

| | Priority | Meaning | Target |
|---|---|---|---|
| 🔴 | **P0 — Critical** | Exploitable now, against live data or a user's machine, with little effort. | Fix immediately, before anything else ships. |
| 🟠 | **P1 — High** | Real security exposure or a user-visible freeze or data loss, but needs a specific setup (a malicious repo, a crafted link, a shared host). | This branch, next. |
| 🟡 | **P2 — Medium** | Defence-in-depth, abuse or integrity gaps, or noticeable performance cost. | This branch, once the P0/P1 work is done. |
| ⚪ | **P3 — Low** | Hardening, hygiene, rare edge cases. | Opportunistic. Batch them. |

**Effort:** S is under 2h, M is about half a day, L is a day or more.

**Status:** ⬜ not started · 🟦 in progress · ✅ done · ⏸ deferred (with a reason)

**Workflow:** when a checkpoint lands, tick its tasks, set its status, and note the commit hash. Every checkpoint says how to verify it. Supabase changes get a pgTAP test in `backend/supabase/tests/`. Core logic gets an xUnit test in `tests/Perch.Tests/`. UI changes get eyeballed through `perch render`.

---

## Tracker

| CP | Pri | Area | Title | Effort | Status |
|---|---|---|---|---|---|
| [CP1](#cp1) | 🔴 P0 | Supabase | Revoke EXECUTE on internal SECURITY DEFINER functions | S | ✅ |
| [CP2](#cp2) | 🔴 P0 | Supabase | Friendship consent: no self-accepting | S | ✅ (audit-column decision open) |
| [CP3](#cp3) | 🟠 P1 | Supabase | Server-owned fields: no forged games, no backdated rows (+ least-privilege grants on every table) | M | ✅ |
| [CP4](#cp4) | 🟠 P1 | Supabase | Realtime inbox authorisation + sender validation | M | ✅ (public-access follow-up open) |
| [CP5](#cp5) | 🟡 P2 | Supabase | Draw with Perch: RPC state checks + size limits | S | 🟦 code + tests done, prod deploy owed |
| [CP6](#cp6) | 🟡 P2 | Supabase | Block/suspension coverage, `find_profile` throttle, feed query | M | 🟦 code + tests done, prod deploy owed |
| [CP7](#cp7) | 🔴 P0 | Client | Executable hijack via untrusted working directory | M | ✅ |
| [CP8](#cp8) | 🟠 P1 | Client | Link opening: scheme allowlist + browser argument injection | S | ✅ |
| [CP9](#cp9) | 🟠 P1 | Client | No UNC/remote path probing (NTLM leak + UI hang) | S | ✅ |
| [CP10](#cp10) | 🟠 P1 | Client | Named pipes: current-user only, park the valet hook | S | ✅ (cross-user squat check untested) |
| [CP11](#cp11) | 🟡 P2 | Client | Hardened shared `GitRunner` | M | 🟦 code + tests done, dogfood owed |
| [CP12](#cp12) | 🟡 P2 | Client | cmd-shim metacharacters (VS Code / GitKraken launch) | S | ✅ |
| [CP13](#cp13) | 🟡 P2 | Client | Control-pipe intent validation + launcher quoting | S | 🟦 code + tests done, dogfood owed |
| [CP14](#cp14) | 🟠 P1 | Data safety | Never wipe `.claude.json`; atomic writes everywhere | M | ✅ |
| [CP15](#cp15) | 🟡 P2 | Privacy | Recording-export redaction gaps | S | ✅ |
| [CP16](#cp16) | ⚪ P3 | Client | Small security hardening batch | M | 🟦 code + tests done, dogfood owed (incl. sign-in on the new OAuth ports) |
| [CP17](#cp17) | 🟡 P2 | Supply chain | CI permissions, pinning, deploy-secret scoping | S | 🟦 done in code; env protection (settings) + first live runs owed; lock files ⏸ |
| [CP18](#cp18) | 🟡 P2 | Supply chain | Code signing + signature verification in `install.ps1` | L | ⬜ |
| [CP19](#cp19) | ⚪ P3 | Build | Build/installer hygiene (em dashes, PATH type, versioning) | S | 🟦 code + tests done, PATH dogfood owed |
| [CP20](#cp20) | 🟠 P1 | Performance | Session scan off the UI thread + incremental transcripts | L | ✅ |
| [CP21](#cp21) | 🟠 P1 | Performance | All-time stats: cache history, don't re-parse it | M | ✅ |
| [CP22](#cp22) | 🟠 P1 | Performance | Streaming chat O(n²) + SessionThreadView leak | M | ✅ |
| [CP23](#cp23) | 🟡 P2 | Performance | Overlay paint path: no IO, no per-frame allocations | M | 🟦 code + tests done, dogfood owed |
| [CP24](#cp24) | 🟡 P2 | Performance | Misc perf batch (metrics, history tail, diff, arcade, images, watcher) | M | 🟦 code + tests done, dogfood owed |
| [CP25](#cp25) | ⚪ P3 | Correctness | Watcher race, PID reuse, Process disposal | S | 🟦 code + tests done, dogfood owed |
| [CP26](#cp26) | 🟡 P2 | roost | Roost-branch findings (land on `roost`) | M | ⬜ |

**Suggested order:**
1. CP1 → CP2 → CP7. These three are the P0s. CP1 and CP2 can ship as a single migration.
2. CP3 → CP4.
3. CP8 → CP9 → CP10 → CP14.
4. CP20 → CP22 → CP21.
5. Everything else, in priority order.

---

## Supabase (the live backend)

> These affect the **deployed** database. A merged migration is not fixed until `db-migrate.yml` has run against prod.
> Each checkpoint below needs a pgTAP test that runs the attack as an `authenticated` (or `anon`) role and asserts that it fails. `rls_test.sql` currently does its accepts as superuser, which is why none of these were caught.

<a id="cp1"></a>
### CP1 — Revoke EXECUTE on internal SECURITY DEFINER functions · 🔴 P0 · S · ✅

**Problem.** Postgres grants EXECUTE on every new function to `PUBLIC`, and Supabase's default privileges add `anon` and `authenticated` on top. No migration contains a `REVOKE EXECUTE`. As a result:
- `rpc/cleanup_old_games {"older_than":"0 seconds"}` deletes **every finished game for every user**. `cleanup_old_draw_games` does the same for draw data. The comment at `20260822140000_connect4_game_cleanup.sql:15` says "deliberately NOT granted", but that is false.
- `are_friends(a,b)` and `shares_edge` let anyone map who is connected to whom.
- `is_blocked(me, X)` shows you whether X blocked you.
- `is_suspended(u)` exposes moderation state.

**Tasks**
- [x] Audit every SECURITY DEFINER function and sort it into one of two buckets:
  - **Sweeps and internals no policy uses** (`cleanup_old_games`, `cleanup_old_draw_games`, `connect4_has_win`): `revoke execute … from public, anon, authenticated`. They stay callable by the owner (pg_cron, and `drop_disc` as SECURITY DEFINER) and `service_role`.
  - **Helpers that RLS policies call** (`are_friends`, `is_blocked`, `is_suspended`, `shares_edge`, `can_see_post`): these **can't simply be revoked**, because policies run their functions as the querying role. They were moved to a `private` schema that the Data API doesn't serve, using `alter function … set schema`. Policies reference functions by OID, so none of them needed recreating. The SQL bodies that call a moved helper by name (`are_friends`, `can_see_post`, `find_profile`, `block_suspended_posts`) were re-created with the new names. `authenticated` keeps EXECUTE on them; `anon` and PUBLIC lose it.
- [x] Check whether the client calls any moved helper directly. It doesn't: the client only calls 10 RPCs (the game/draw RPCs, `find_profile`, `list_blocked`), and none of them moved.
- [x] **Found while testing:** every client RPC was also callable with the bare **anon** key (via PUBLIC). For example, `find_profile` let anyone enumerate handles without an account. All ten are now revoked from `public, anon`; each already had an explicit `authenticated` grant.
- [x] Misleading comment: applied migrations aren't edited. The new migration's header documents that the "NOT granted" comments were wrong.
- [x] pgTAP (`tests/security_test.sql`, tests 1–11): no helper left in `public`; anon has no USAGE on `private` and no EXECUTE on the helpers or client RPCs; `authenticated` keeps EXECUTE on the client RPCs; the sweeps and `connect4_has_win` throw 42501 as `authenticated`; `posts_read` and `profiles_friends_select` still resolve through the moved helpers.
- [x] **Deploy to prod** (`db-migrate.yml`), then re-run the curl probes below against the live project. *(User-confirmed on 2026-09-30.)*

**Verify.** Done locally on 2026-09-29. `supabase test db` gave 84/84 across all four files. Local REST probes with the anon key gave `rpc/cleanup_old_games` 401, `rpc/cleanup_old_draw_games` 401, `rpc/find_profile` 401, and `rpc/are_friends` / `rpc/is_blocked` 404. The same probes against prod were user-confirmed on 2026-09-30.

**Landed:** migration `20260929120000_security_hardening.sql` (commit `ab0c93f`).

<a id="cp2"></a>
### CP2 — Friendship consent: no self-accepting · 🔴 P0 · S · ✅

**Problem.** In `20260819120100_rls.sql:33-40`, `friendships_request` only checks `requester = auth.uid()`, and `friendships_respond` has USING but no WITH CHECK. On top of that, `20260819120200_grants.sql:10` grants full-column UPDATE. So `POST /rest/v1/friendships {"requester":"<me>","addressee":"<victim>","status":"accepted"}`, or a PATCH of your own pending row, makes you the victim's friend without their consent. That gives access to their posts and profile and lets you challenge them to games.

**Tasks**
- [x] Insert policy: `with check (requester = auth.uid() and status = 'pending')`. The planned `not is_blocked(...)` term was **dropped**: a blocked user's request would then fail visibly, which tells them they were blocked. The block-related profile leak is handled in CP6, by making `shares_edge` ignore blocked pairs so the request succeeds but shows nothing.
- [x] Update policy: `using (addressee = auth.uid() and status = 'pending') with check (addressee = auth.uid() and status = 'accepted')`. Only the addressee can update, and only pending → accepted.
- [x] Trigger `private.friendships_guard` (BEFORE UPDATE) makes `requester`, `addressee` and `created_at` immutable. Without it, the addressee could accept while re-pointing the requester at a third party.
- [x] **Approach changed from the plan:** the UPDATE grant is kept, and there's no `accept_friend` RPC. Already-shipped clients send friend requests as a PostgREST `merge-duplicates` upsert, which needs table UPDATE privilege. Revoking UPDATE would have broken friend requests for everyone until they updated. The policies plus the trigger close the hole without that.
- [x] Client: `SendRequestAsync` now uses `resolution=ignore-duplicates` (ON CONFLICT DO NOTHING) instead of `merge-duplicates`. `RespondAsync` (a PATCH by the addressee) is unchanged and still works.
- [ ] Existing forged edges in prod can't be detected: there's no `accepted_at` / `accepted_by` column. Decide whether to add an audit column going forward.
- [x] pgTAP (`tests/security_test.sql`, tests 12–18): insert as `accepted` or `blocked` → 42501; pending insert and ignore-duplicates re-send succeed; the requester's PATCH matches nothing; re-pointing the requester while accepting → 42501; the addressee's accept succeeds.
- [x] **Deploy to prod** (`db-migrate.yml`). *(User-confirmed on 2026-09-30.)*

**Verify.** Done locally on 2026-09-29. Beyond pgTAP, an end-to-end run over local PostgREST with real signed-in test users checked each case:
- forged accepted insert → 403;
- new-client request (ignore-duplicates) → 201, and re-send → 201 no-op;
- **old-client `merge-duplicates` request → 201**, so shipped apps keep working;
- requester self-PATCH → `[]`;
- addressee accept → 200 accepted;
- re-point while accepting → 403;
- profile visibility through `private.shares_edge` still works.

.NET suite 1377/1377.

**Landed:** same migration as CP1, plus `SupabaseSocialClient.SendRequestAsync` (commit `ab0c93f`).

<a id="cp3"></a>
### CP3 — Server-owned fields: no forged games, no backdated rows · 🟠 P1 · M · ✅

**Problem.**
- **Forged games.** `20260821120000_connect4.sql:60-66` still allows a direct `INSERT` into `games`, where the client chooses `status`, `winner`, `turn`, `move_count` and `updated_at`. That lets someone create forged losses that sort first forever, escape cleanup, and get fetched with no limit on every 60s poll.
- **Rate-limit bypass.** `20260918120000_posts_current_only.sql:48-69` trusts a client-supplied `posts.created_at` in the flood guard, so setting `created_at: "1970-01-01"` bypasses the limit entirely.
- **Scope widened on request: least privilege on every table.** A dump of the live ACLs showed that `anon`, `authenticated` and `service_role` all held `arwdDxtm` on all 13 tables. That covers every privilege, including TRUNCATE, REFERENCES, TRIGGER and MAINTAIN, which RLS doesn't cover. The source is Supabase's default privileges, and the existing `grant` migrations only ever added to it. So comments such as "select only on moves" and "no grants to authenticated" (moderation) were false; RLS was the only barrier. Every policy was also `TO PUBLIC`, and `anon` had USAGE on `public`.

**Tasks**
- [x] `drop policy games_create`, with no INSERT on `games`, so games are only created in `accept_game_request`.
- [x] **Deny by default.** `REVOKE ALL` on every table and sequence from PUBLIC, `anon`, `authenticated` and `service_role`, then grant back per table only what the client does. SELECT is table-level; INSERT and UPDATE are **column lists** of exactly what the client sends. No generated ids, timestamps or game state are writable.
- [x] `anon` gets nothing: no table privileges, and no USAGE on `public`. `service_role` gets only `moderation` (rawd) and `reports` (rd). It has no other code path: account deletion goes through the auth admin API and cascades as the table owner (checked end-to-end), and the sweeps are SECURITY DEFINER.
- [x] Every policy re-scoped with `ALTER POLICY … TO authenticated`. `posts_delete` was dropped, since no client deletes a post and the grant is gone.
- [x] Every non-extension function in `public`/`private` revoked, then an explicit EXECUTE allowlist: the 10 client RPCs and 5 RLS helpers go to `authenticated`, and the sweeps plus `connect4_has_win` go to `service_role`. Trigger functions keep no grants, and they still fire.
- [x] **Default privileges closed.** `alter default privileges for role postgres in schema public revoke all …` on tables, sequences and functions, so a new table starts closed. The `supabase_admin` entry can't be altered from a migration; it only creates platform objects.
- [x] BEFORE triggers stamp `created_at := now()` on all 12 tables that have it, and `updated_at := now()` on insert and update of `games`, `draw_games`, `draw_rounds` and `moderation`. Doing both belt-and-braces: the column grants reject a client-supplied timestamp (42501), and the triggers override one on SECURITY DEFINER or future paths.
- [x] The flood guard stamps `now()`. **Changed from the plan:** the stamp moved out of `profiles.last_posted_at` into `private.post_throttle`, which has no grants. Before, any friend could read when you last posted, and hiding the column with a column grant breaks the client (see below).
- [x] **Found while verifying: claim-handle was broken.** PostgREST compiles the client's `merge-duplicates` upsert to `ON CONFLICT (id) DO UPDATE SET id = EXCLUDED.id, …`, and `return=representation` to `RETURNING *`. `20260918`'s `grant update (handle, display_name, mood_emoji)` left out `id`, so **every claim-handle or profile edit has returned 403 since `20260918` reached prod**; locally, exact prod grants reproduce the 403. The fix is to grant UPDATE(`id`). That's safe because `profiles_self_update` pins `id = auth.uid()` in both USING and WITH CHECK (pgTAP 38), and with the stamp moved to `private`, table-level SELECT exposes nothing server-owned.
- [x] Client:
  - `limit=50` on `GetGamesAsync` and `GetDrawGamesAsync`.
  - `CreateGameAsync` removed from `ISocialClient` and `SupabaseSocialClient`; it stays on `FakeSocialClient` as a fixture helper.
  - The debug tester's Connect 4 start and rematch now invite and have the puppet accept (`NewConnect4Game`); the `Connect4Window` rematch hook is a `Func<Task>`.
- [x] pgTAP (`security_test.sql`, tests 19–42, 44 in the file):
  - **Tripwire:** pins the exact privilege matrix: table grants for `authenticated` and `service_role`, every client-writable column, the executable-function allowlist, RLS on everywhere, all policies `TO authenticated`, nothing for `anon`, nothing on `private` tables, no TRUNCATE/REFERENCES/TRIGGER/MAINTAIN, and a probe table and function created in the test start closed.
  - **Attacks:** anon can't read; forged game insert, game UPDATE and moves insert → 42501; backdated post → 42501; the owner's backdated insert is re-stamped to `now()`; the flood-guard stamp is unreachable and is the server clock; two posts in the interval → 23514; the claim-handle upsert works in PostgREST's exact SQL shape; `id` can't be re-pointed.
  - `connect4_test` and `draw_test` fixtures now create games as the owner, and let the server generate request ids.
- [x] **Deploy to prod** (`db-migrate.yml`), then run the probes below. *(User-confirmed on 2026-09-30.)*

**Verify.** Done locally on 2026-09-29:
- `supabase test db` 110/110 across all four files.
- .NET suite 1406 passed, 1 skipped; both heads build.
- An end-to-end run over local PostgREST with real signed-in users replayed every REST call `SupabaseSocialClient` makes, and all passed: claim handle (insert and update paths), friends (old-client merge-duplicates, ignore-duplicates, re-send, accept), posts, reactions, invite → accept → move → resign → delete for Connect 4 and Draw, block/unblock, report, and account deletion through the auth admin API cascading. The same run confirmed that direct game insert, game PATCH, backdated post, post DELETE, reading `reports` or `moderation`, every anon read, and `service_role` reads outside moderation are all refused.

**After deploy** (user-confirmed on 2026-09-30):
- Against prod, with a puppet account: claim or edit a handle (this confirms the 20260918 break is fixed), post twice inside 5s (the second is refused), and send a POST `/rest/v1/games` (403).
- With the bare publishable key: `GET /rest/v1/posts` and `/profiles` (401).
- Confirm Realtime postgres_changes still delivers (moves, draw_rounds, posts). Realtime runs as `supabase_admin` and the published tables keep table-level SELECT, so it should be unaffected, but it hasn't been checked live.
- In the dashboard, check Storage has no buckets. Locally there are none, and the app doesn't use Storage.

**Landed:** commit `c996bf1`: migration `20260929130000_least_privilege.sql`, plus client changes in `SupabaseSocialClient.Games.cs`/`.Draw.cs`, `ISocialClient`, `FakeSocialClient.Games.cs`, `Connect4Window` and `DebugSocialWindow`.

<a id="cp4"></a>
### CP4 — Realtime inbox authorisation + sender validation · 🟠 P1 · M · ✅

**Problem.** `perch:inbox:<uid>` is a **public** broadcast topic (`SupabaseRealtime.cs:192`, `SupabaseSocialClient.Games.cs:233-278`). Anyone holding the anon key can:
- subscribe to any user's inbox and see who invites or nudges them, along with game ids;
- post to it with a forged `from_handle`, producing a phishing-style bubble;
- flood it, where every message triggers a roughly 14-request `RefreshSoon` poll (`App.axaml.cs:2277-2297`).

**Tasks**
- [x] Switched to private channels. Migration `20260929140000_realtime_inbox_authz.sql` adds two `realtime.messages` policies, both `TO authenticated`:
  - `perch_inbox_receive` (SELECT): only when the topic's uid is `auth.uid()`.
  - `perch_inbox_send` (INSERT): only when the sender is an accepted, unblocked friend of the topic's owner, via `private.are_friends`, which already folds in blocks.
  
  The topic is parsed strictly by `private.inbox_owner()`: only `perch:inbox:<lower-case uuid>` is accepted, and it never raises. `realtime.messages` belongs to `supabase_realtime_admin`, so its `anon`/`authenticated` grants can't be revoked from a migration; RLS with no other policies denies everything else.
- [x] Client:
  - `RealtimeChannel.Inbox` is `Private`: the join sends `config.private = true` and the REST broadcast sends `private: true`. The broadcast no longer carries `from_handle`.
  - The new pure `InboxGate` drops a message unless its claimed sender is in the accepted, unblocked friend list, and takes the handle from that list; the payload is ignored.
  - `SupabaseSocialClient` refreshes the friend list on every roster poll. On an unknown sender, most likely a friendship accepted seconds ago, it re-reads the friend graph at most once per 15s.
- [x] `RefreshSoon` is coalesced by a new `Perch.Data.CoalescingTrigger`: one poll in flight, a burst collapses into one trailing poll, and polls start at least 2s apart. An isolated request still runs immediately. The 60s tick goes through it too, and a trailing poll after `SetActive(false)` is a no-op.
- [x] Tests:
  - pgTAP `security_test.sql` 45–51: the topic parser; the owner reads her own inbox; nobody else can, not even a friend; a friend can broadcast; a stranger, a blocked friend and anon can't. Test 28's function allowlist now includes `private.inbox_owner`.
  - xUnit: `InboxGateTests` (4), `CoalescingTriggerTests` (4, with injected clock and delay, so nothing sleeps), and two `RealtimeProtocolTests`: the inbox join is private while the table streams aren't, and the broadcast name matches the server's topic parser.
- [x] Manual realtime check against the local stack, with a Node WebSocket probe and three real users (A the owner, B A's friend, C a stranger):
  - A joins her private inbox; C is refused (`Unauthorized: You do not have permissions to read from this Channel topic`); B can't join A's inbox either.
  - A receives B's private broadcast.
  - A receives none of C's private broadcast, a public broadcast on the same topic name, or B's broadcast after A blocks B.
  - A public snooper on the same name sees none of the private traffic.
- [x] **Deploy to prod** (`db-migrate.yml`). Then, from two real accounts, check that an invite and a nudge still arrive instantly, and that a non-friend's broadcast doesn't. *(User-confirmed on 2026-09-30.)*
- [x] Dogfood: invite, accept, decline and nudge flows between two current builds (the debug puppet tool works). **Mixed versions:** a pre-CP4 client still uses the public topic, so its broadcasts don't reach a CP4 client and vice versa. Both fall back to the 60s poll, so nothing breaks, it's just slower until both update. *(User-confirmed on 2026-09-30.)*
- [ ] **Follow-up to decide:** old clients can still abuse *each other's* public inbox. The only way to close that server-side is to turn off Realtime's "allow public access" in the dashboard. That also requires moving the posts, moves and draw_rounds change streams to private channels, with `realtime.messages` SELECT policies for `postgres_changes`. Without that change, old clients' realtime would fail and they would drop to polling.

**Verify.** Done locally on 2026-09-29: `supabase test db` 117/117; .NET 1416 passed, 1 skipped; both heads build; the realtime probe above passed.

**Landed:** commit `b84159e`: migration `20260929140000_realtime_inbox_authz.sql`, plus `InboxGate`, `CoalescingTrigger`, the private inbox channel and `SocialFeedMonitorHost`.

<a id="cp5"></a>
### CP5 — Draw with Perch: RPC state checks + size limits · 🟡 P2 · S · 🟦

**Problem.**
- `give_up_draw_round` and `submit_draw_guess` (`20260924120000_draw_with_perch.sql:194-282`) don't check the round, phase or game state. Replaying an old round lets a player take the drawing turn and orphan the opponent's rounds.
- There are no length CHECKs on `strokes`, `word`, `letter_hint` or individual guesses, so multi-MB payloads fill storage and every client ends up parsing them.

**Tasks**
- [x] In both RPCs, require `g.status='in_progress' and g.phase='guess' and g.whose_turn=me and r.round_no=g.round_no and r.status='guessing'`. Each failure has its own message (game is over, round is over, not the current round, not your turn). **Giving up is no longer idempotent:** a second give-up on the same round is refused, where it used to re-apply the turn change, which is what made the replay work.
  - Both RPCs now lock the **game before the round**, the same order as `submit_draw_round` and a game delete's cascade, so a guess racing a delete can't deadlock. A round deleted between the unlocked lookup and the lock reports "no such round".
- [x] CHECKs on **both** `draw_rounds` and `draw_requests`: `word` ≤ 40 and `letter_hint` ≤ 40 characters, and `strokes` ≤ 160 KiB. Each guess ≤ 64 characters is checked in `submit_draw_guess`. Existing rows over the limits are trimmed first so the constraints validate.
  - **Changed from the plan:** the strokes cap is **160 KiB, not 64 KB.** `DrawStrokeCodec`'s own caps (800 strokes, 12,000 points) let a legitimate busy drawing encode to about 138 KB, so 64 KB would have refused real drawings. The cap is the shared `DrawStrokeCodec.MaxEncodedBytes`, and a test encodes the worst-case drawing to prove it fits.
- [x] `draw_requests` rate limit: 10 challenges per requester per 10 minutes. It's a fixed-window counter in `private.draw_request_throttle` (no grants, like `post_throttle`), written by the SECURITY DEFINER trigger `private.enforce_draw_request_rate_limit`. A counter is needed because a sent-then-cancelled invite leaves no row to count. A refused insert (the limit, RLS or the unique index) rolls its count back.
- [x] Client:
  - `DrawStrokeCodec.Decode` refuses a payload over `MaxEncodedBytes` before `JsonDocument.Parse`.
  - New shared limits `DrawWords.MaxWordLength` (40) and `DrawGuessing.MaxGuessLength` (64). `SupabaseSocialClient` checks them before sending, with a friendly message.
  - `FakeSocialClient` mirrors the new gate (`RequireCurrentGuessLocked`) and the limits.
- [x] pgTAP (`draw_test.sql`, 15 new, 35 in the file):
  - give-up twice, and bob replaying give-up on the old round 1, are refused, and the game state doesn't move;
  - a 41-character word, strokes of 160 KiB + 1 on a round and on an invite, and a 65-character guess are refused, while strokes exactly at the cap are accepted;
  - with round 3 live, give-up and guess on round 1 are refused and round 3 is untouched;
  - guess and give-up on an abandoned game are refused;
  - all six constraints exist;
  - the 11th send-then-cancel challenge inside the window is refused, and an aged window lets challenges through again.
  
  Against the pre-CP5 schema the replay tests fail: bob's replay really did take the drawing turn.
- [x] xUnit (8 new cases): every bank word and its hint fit `MaxWordLength`; the worst-case drawing in two layouts fits `MaxEncodedBytes` (and exceeds 128 KB, which documents why 64 KB was wrong); an over-cap payload isn't parsed, while one exactly at the cap is; and the fake refuses a double give-up, an old-round replay, moves on an abandoned game, and over-long words and guesses.
- [ ] **Deploy to prod** (`db-migrate.yml`). Then, between two current builds: play a round through (draw, wrong guess, right guess, give up), and send a challenge.

**Verify.** Done locally on 2026-09-30: `supabase test db` 132/132 across all four files; `dotnet build perch.slnx` clean; the .NET suite passes 1644 with 1 skipped.

**Landed:** commit `d05b39a`: migration `20260930120000_draw_state_checks.sql`, plus `DrawStrokeCodec`, `DrawWords`, `DrawGuessing`, `SupabaseSocialClient.Draw.cs` and `FakeSocialClient.Draw.cs`.

<a id="cp6"></a>
### CP6 — Block/suspension coverage, `find_profile` throttle, feed query · 🟡 P2 · M · 🟦

**Problem.**
- Blocks don't gate `shares_edge`, new friendship inserts, or the game RPCs (`submit_draw_round`, `submit_draw_guess`, `drop_disc`).
- Suspension only stops posts and `find_profile`.
- `find_profile` has no throttle, so anyone can enumerate handles and harvest ids.
- `GetFeedAsync` (`SupabaseSocialClient.cs:444`) has no author filter, so the RLS predicate runs for every post in the table. That is O(N²) across the platform.

**Tasks**
- [x] `private.shares_edge` ignores a blocked pair. The friendship insert is left alone, as planned: a blocked user's request still succeeds.
  - **Widened from the plan: the friendships SELECT policy hides a blocked pair's row too, from both sides.** With only `shares_edge` changed, the blocked person would still see the accepted edge, with a profile they can no longer read, so the blocker would show as a friend called "unknown". Now, to the blocked person, a block looks like an unfriend. Their new friend request "succeeds" (ignore-duplicates, or a fresh pending row) but neither side sees it. Unblocking restores the edge as it was. The blocker's own UI already drops blocked people from the graph lists, so nothing changes for them.
- [x] **Game RPCs — changed from the plan:** instead of editing six RPCs, one BEFORE INSERT OR UPDATE guard trigger (`private.guard_game_write`) on `games` and `draw_games` refuses any write that leaves a game in progress between a blocked pair ("game is over", 23514) or that a suspended account makes (42501). Every RPC that creates or advances a game (`accept_game_request`, `accept_draw_request`, `drop_disc`, `submit_draw_round`, `submit_draw_guess`, `give_up_draw_round`) writes that row in the same transaction, so all of them are covered, and so is any future RPC. Resigning (which ends the game) and abandonment pass through.
- [x] Blocking ends shared play: an AFTER INSERT trigger on `blocks` (`private.end_play_on_block`) abandons the pair's in-progress Connect 4 and Draw games and deletes their pending invites of both kinds. The migration does the same for blocks that already exist.
- [x] Suspension also stops reacting, profile edits, friend requests, and both kinds of game invite (WITH CHECK on each policy, so a refusal is an explicit 42501, not a silent no-op), as well as playing (the guard). Blocking and reporting stay available.
- [x] `find_profile` is throttled to 20 lookups per caller per 10 minutes, through `private.find_profile_throttle` (keyed on the auth user, since you can look someone up before claiming a handle). It became VOLATILE plpgsql, because PostgREST runs STABLE functions in a read-only transaction where the counter write would fail.
  - **Changed from the plan: it still returns the id.** Shipped clients need the id to send a friend request, and since CP3/CP4 an id unlocks nothing on its own (the inbox is private and every write checks the caller).
- [x] Feed: `GetFeedAsync` sends `author=in.(<me + accepted friends>)`. The roster reuses the friend list it already fetched, and the public overload fetches it first. A large list goes in chunks of 100 authors, merged newest-first and capped. `EXPLAIN` under RLS confirms the filter uses `posts_author_created` (`uuid_eq` is leakproof, so the filter is applied before the RLS predicate), which then runs only on those rows. Old clients keep the unfiltered query until they update.
- [x] pgTAP (`blocks_test.sql`, 26 new):
  - **Block:** the blocker and the blocked user both lose the friendship row, and the blocked user can't read the profile. Both games are abandoned and both invites deleted. The blocked user can't move. A blocked friend's request, and a blocked stranger's, succeed but expose nothing to either side. Accepting a sneaked-in invite of either kind is refused. Even the owner can't put the pair's game back in progress. Unblocking restores the profile and the accepted edge.
  - **Suspension:** reacting, editing the profile, a friend request, both kinds of invite and a move are all refused. Blocking and reporting still work.
  - **Throttle:** the 21st lookup is refused, and an aged window lets lookups through again. `find_profile` is volatile, and the two new trigger functions aren't callable by clients.

  Against the pre-CP6 schema, 8 of the first 11 block tests fail before the script aborts. A second run with the block section removed fails every suspension test and the throttle test.
- [x] xUnit (3 new): the feed asks only for me and accepted friends (not a pending edge); 150 friends split into two requests, merged newest-first under the cap; and the fake hides a blocked friend's edge until unblocked (it now mirrors the policy in `GetFriendsAsync`). The existing feed test now answers the friendships call.
- [x] Live check over local PostgREST with a real signed-in user: `rpc/find_profile` calls 1–20 return 200, and call 21 returns 400 with the rate-limit message.
- [ ] **Deploy to prod** (`db-migrate.yml`). Then, with the debug puppet tool: block a friend who has a live game with you (the game ends for both, and each side's friends list drops the other), unblock (the friendship returns), and check that the feed still shows friends' statuses.

**Known side effects.**
- While a block stands, the data export's friends list omits that person too (it reads the same friendships). They still appear under blocked.
- A blocker can't unfriend someone while blocking them; unblock first. That was already true in the UI, which hides blocked people from the graph lists.

**Verify.** Done locally on 2026-09-30: `supabase test db` 158/158 across five files; `dotnet build perch.slnx` clean; the .NET suite passes 1647 with 1 skipped.

**Landed:** commit `6d80e21`: migration `20260930130000_block_suspend_coverage.sql`, plus `SupabaseSocialClient.GetFeedAsync` and `FakeSocialClient.GetFriendsAsync`.

---

## Client security

<a id="cp7"></a>
### CP7 — Executable hijack via untrusted working directory · 🔴 P0 · M · ✅

**Problem.** Bare executable names get resolved inside untrusted repos in four places:
- **`ClaudeSessionController.cs:67`** runs `cmd.exe /c "claude …"` with `WorkingDirectory = cwd`. cmd searches the current directory before PATH, so a committed `claude.cmd`, `.bat` or `.exe` runs instead of Claude, with no permission layer. The same shape is in `PluginManager.cs:133` and `SessionLauncher.cs:41-48`.
- **`Program.cs:274` (`DetachTray`)** sets the long-lived tray's cwd to the repo where `perch` was typed. The hook-launched tray inherits the session cwd too. From then on, every bare `git`, `gh`, `cmd.exe`, `explorer.exe` or `rundll32.exe` spawn looks in that repo first, for the tray's whole lifetime. Examples: `GitRepoService.cs:932`, `GitStatsService.cs:120`, `PrStatusService.cs:411`, `FileRevealer.cs:22,54`, `GitKrakenLauncher.cs:58`.
- **`Perch.Hook/Program.cs:512`** calls `ShellExecute("perch")`, which resolves from the session's cwd.
- **`StatuslineScript.cs:421`**: the generated `.mjs` calls `execFileSync('git', …, {cwd})`, and libuv searches `cwd` first.

**Tasks**
- [x] `Main` calls `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` once the launch cwd has been captured into the session intents, and before any spawn. `DetachTray` now starts the relaunch with `WorkingDirectory = AppContext.BaseDirectory`; the session cwd still travels in the intent file.
- [x] New `Perch.Data.ExecutableResolver`:
  - walks PATH plus PATHEXT and never the cwd;
  - skips relative PATH entries and unquotes quoted ones;
  - on Windows never returns npm's extensionless sh script;
  - caches hits (re-validated by existence) and retries misses after 30s.

  `Resolve()` falls back to the bare name, which is safe now that the cwd is neutral. `SystemTool()` and `WindowsTool()` give absolute `cmd`/`rundll32` and `explorer` paths.
- [x] Applied everywhere Perch starts a process:
  - `git` in GitRepo, GitStats, MarkdownProjectScan and ProjectFileScan, and `gh` in PrStatus;
  - `code` in FileRevealer (Windows and Mac), plus the Markdown, DaemonList and Overlay "open in VS Code" actions;
  - `gitkraken`, and GitKraken's `cmd`;
  - `explorer` and `rundll32`;
  - the Mac `open`, now `/usr/bin/open`.
  
  The hand-rolled PATH walks in `GitKrakenLauncher` and `MarkdownWindow` were replaced by the resolver.
- [x] `claude` goes through a new shared `ClaudeCli.CreateStartInfo`, used by both `ClaudeSessionController` and `PluginManager`:
  - a native `claude.exe` is exec'd directly, with no shell;
  - an npm `.cmd` shim runs through absolute `cmd.exe` with the path quoted, plus `NoDefaultCurrentDirectoryInExePath=1`, so the shim's own bare `node` lookup can't hit the cwd either;
  - not on PATH → a clear "Couldn't find the claude CLI" error.
  
  `PluginManager` also stops running `cmd.exe` on macOS.
- [x] `NoDefaultCurrentDirectoryInExePath` is set **only** for the cmd-shim claude launch. It isn't set tray-wide, which would change the environment of every tool a user's session runs. Everywhere else, absolute paths already remove the search.
- [x] `SessionLauncher` (reopen in terminal) resolves `claude` up front and hands every terminal an absolute path: `wt`, absolute `cmd`, and absolute `powershell` with `& '<path>'`. If claude isn't on PATH it returns false, and the app falls back to copying the command.
- [x] Hook: the new `TrayExecutable()` launches the tray from the `perch.path` breadcrumb, with `WorkingDirectory` set to the tray's own directory, falling back to a PATH-only lookup. It never uses the bare `perch`.
- [x] Statusline `.mjs`: a memoised `gitExe()` PATH walk (absolute entries only, `git.exe` on win32), and `execFileSync(git, …)`. Nothing is baked in at generation time, so the script stays portable.
- [x] Tests, 15 new:
  - `ExecutableResolverTests` (12): PATH order, PATHEXT order, the npm sh script, relative and quoted entries, cwd never searched, absolute and relative names, POSIX mode, system tools.
  - `ClaudeCliStartInfoTests` (2): exe direct; shim through absolute cmd, quoted, with the env var.
  - A statusline end-to-end test: an **empty** `git.exe` planted in a real repo. The resolver still returns the PATH git, and the generated script (run under node with `NoDefaultCurrentDirectoryInExePath` stripped) produces the real counts `S1U0`.
  
  Test fixtures only ever create empty files; no executable is copied or modified. An earlier draft that planted a copied system binary was replaced on request.
- [x] Dogfood: interactively check that a controlled session starts, "Reopen in terminal" works for each terminal choice, the hook autostarts the tray, and "Open in VS Code" and GitKraken still work. *(User-confirmed on 2026-09-29.)*
- [x] **Follow-up bug found while dogfooding: resume under the owning config dir.** Reopening a session from a non-primary config dir resumed it under the primary account, where the transcript doesn't exist. The same happened for resuming inside a Perch window and for "Hand back to a terminal". The fix:
  - `TranscriptLocator.ResumeConfigRoot` picks the transcript's owning dir, or null for the primary, meaning inherit.
  - `ISessionLauncher` takes that dir; so do SessionWindow resume, hand-back and `/login`/`/logout`.
  - A shell-opened terminal can't be given an environment block, so the dir travels on the command line: `set CLAUDE_CONFIG_DIR=<dir>&& …` for cmd/wt, `$env:…;` for PowerShell.
  - The first attempt used `set "…" && …`. Windows Terminal re-tokenises its command line and drops those quotes, so cmd stored the value with a trailing space and Claude started first-run setup. The unquoted `…&&` form survives that; a test replays wt's split-and-rejoin.
  - Dirs containing cmd metacharacters (`% " ; & | < > ^`) fall back to copying the command.
  - `LaunchLog` (`<settings dir>/logs/launch.log`) records every terminal/controlled launch: the resolved transcript, owner, the injected `CLAUDE_CONFIG_DIR` and the exact command line.

**Verify.** Done 2026-09-29: both heads build, and the .NET suite passes 1392/1392. Found along the way:
- **The environment masks the attack.** When a process inherits `NoDefaultCurrentDirectoryInExePath=1`, which Claude Code sets for the tools it spawns, Node skips the cwd search. With it unset (a normal Windows environment), Node 24's `execFileSync('git', …, {cwd})` **does** resolve a `git.exe` in the cwd. The statusline test strips the variable so it exercises the unprotected case.
- **The statusline test catches the bug.** Against the old bare-`'git'` script it produced `SU`, not `S1U0`.

<a id="cp8"></a>
### CP8 — Link opening: scheme allowlist + browser argument injection · 🟠 P1 · S · ✅

**Problem.**
- Markdown link and autolink targets (`MarkdownView.cs:612-627`, `LinkText.cs:84-86`) reach `UrlOpener.Open`, which calls `ShellExecute` (`UrlOpener.cs:19`) with no scheme check. So `file:`, UNC, `search-ms:` and `ms-*:` links execute on click.
- Middle-click (`UrlOpener.cs:35-38,60-69`) passes the URL as a raw argument to the browser exe, so a destination like `--gpu-launcher=…` becomes a browser switch.
- PR check `detailsUrl` values (`OverlayCanvas.cs:5187`) reach the same sink.

**Tasks**
- [x] New pure `Perch.Data.OpenTargets` is the one place that decides what may be opened:
  - `WebUrl(url)`: an absolute `http`/`https` URL with a host, or `mailto:`, returned as the normalised `AbsoluteUri` (always scheme-led, whitespace and control characters escaped); anything else → null.
  - `LinkFilePath(target)`: a markdown link target as a local path (relative with `#`/`?` dropped and `%`-escapes decoded, a drive path, or a local `file:` URI). Any other scheme, and anything network-shaped (UNC, `//host`, `\\?\`, `\\.\`, `file://host/…`, including after decoding), → null.
  - `IsViewerSafeFile(path)`: a fully-qualified, non-network path whose extension is on a short view-only allowlist (images, text/data, PDF, `docx`/`xlsx`/`pptx`, media). HTML, SVG, macro-capable office formats, executables, scripts, shortcuts and anything unknown are refused, as are NTFS alternate streams.
- [x] `IUrlOpener` (Windows and Mac): all three entry points run `WebUrl` first and silently no-op otherwise. A `mailto:` given to new-window/private goes to the plain shell open, never to the browser exe. The interface doc no longer claims it opens local files.
- [x] Chromium launches (new-window and private) put `--` before the URL, the form Chrome registers for itself. Gecko has no switch terminator, so Firefox relies on `WebUrl` guaranteeing a scheme-led argument.
- [x] **Found while auditing callers:** `AttachmentChip` and the image viewer's "Open" shell-opened local files **through the URL opener**, and would have broken. New `IFileRevealer.OpenWithDefault(path)` (Windows and Mac): the default handler for a view-only type, otherwise reveal in the file manager, so it's never a dead end and never runs code. Both callers moved to it.
- [x] `OpenInEditor`'s "no VS Code" fallback now goes through `OpenWithDefault` too, which closes CP12's third task (a `.bat` file ref was shell-executed). The Mac `OpenWith`, which was a bare `open` of any file, also goes through it.
- [x] `MarkLink`: only `WebUrl` targets become browser links (the span stores the normalised URL, so the hover tip shows the real destination). A `LinkFilePath` target joins the inline-code file-ref candidates, so it opens in the viewer only if `ResolveFile` finds a real file. `javascript:`, `search-ms:` and the like are inert text. Email autolinks now get their `mailto:`.
- [x] xUnit `OpenTargetsTests` (72 cases): `file:///x.exe`, `file://host/…`, `\\h\s\x`, `//h/s`, a drive path, `search-ms:`, `ms-settings:`, `ms-msdt:`, `--flag`, `-new-window`, `javascript:`, `vbscript:`, `data:`, `vscode:`, a host-less `https://` and a relative path are rejected; http(s) (case-normalised, trimmed, with query/fragment) and `mailto:` are accepted; the result is always scheme-led; the link-path and viewer-safe cases above.
- [x] Dogfood (user-confirmed on 2026-09-29):
  - middle-click a link (new window) and sign in (private window) with Chrome or Edge as the default browser, to confirm `--` is accepted in both launches;
  - Ctrl+click a relative `[plan](docs/x.md)` link in a session reply (it should open the viewer);
  - "Open" on a dropped non-image attachment.

**Not covered here (CP9):** `ResolveFile` still called `File.Exists` on a rooted inline-code span. Link targets reach it too, but `LinkFilePath` already dropped network-shaped targets before they got there. *(CP9 has since closed this: see `FileRefResolver`.)*

**Verify.** Done 2026-09-29: `dotnet build perch.slnx` is clean (both heads and the Mac platform project), and the .NET suite passes 1488 with 1 skipped.

**Landed:** commit `bcf8714`: `OpenTargets`, both `UrlOpener`s and `FileRevealer`s, `IFileRevealer.OpenWithDefault`, `MarkdownView.MarkLink`, `AttachmentChip` and `ImageViewerWindow`.

<a id="cp9"></a>
### CP9 — No UNC/remote path probing · 🟠 P1 · S · ✅

**Problem.** `MarkdownView.ResolveFile` (`MarkdownView.cs:570-583`) calls `File.Exists` on any rooted inline-code span, including `\\attacker\s\a.md`. It runs on the UI thread, and streaming repeats it about 25 times a second. The effect is that SMB/WebDAV authentication sends the user's NTLM hash to the attacker, and the UI hangs for the SMB timeout. The same pattern exists in `gitdir:` resolution (`PrStatusService.cs:464-468`, `GitHead.cs:55`, and `findGitDir` in the statusline `.mjs`).

**Tasks**
- [x] New shared `Perch.Data.LocalPath`:
  - `IsNetworkShaped` matches every spelling Windows treats as a leading `\\`: `\\`, `//`, `\/` and `/\`, which covers UNC and the `\\?\` / `\\.\` device prefixes. `OpenTargets` (CP8) now uses it too, so the two can't drift.
  - `IsSafeToProbe(path, trustedRoot)` refuses network-shaped paths and, on Windows, any drive that isn't Fixed, Removable or Ram. A mapped network drive reaches SMB just like UNC; an optical or unmapped drive can stall. Relative paths are safe, because they resolve under the trusted root and `..` can't climb off that volume.
  - **Changed from the plan:** "under the session cwd" became "on the trusted root's own volume". A user whose repo lives on `\\srv\s` may still probe `\\srv\s\…`, since that contacts no host they didn't choose. Device forms never qualify for that exemption.
- [x] `MarkdownView.ResolveFile` moved to Core as `FileRefResolver.Resolve(cwd, text)`, judged against the session cwd, so it has xUnit coverage.
- [x] The `gitdir:` resolvers (`PrStatusService.FindGitDir`, `GitHead.FindGitDir`, and `findGitDir` in the statusline `.mjs`) only follow a target on the `.git` file's own volume, or on a local drive. The `.mjs` has a port of the rule (`netShaped` / `uncVolume` / `safeGitDir`). Node has no cheap drive-type lookup, so the script doesn't check drive type. That leaves only the user's own mapped servers reachable, never a host chosen by the attacker.
- [x] `FileRefResolver` caches per (cwd, text) for 5s, capped at 1024 entries, so a streaming reply's roughly 25 re-renders a second probe once, while a file created later still gets linked.
- [x] Tests (39 new):
  - `LocalPathTests`: every UNC and device spelling; the same-share exemption versus a different share, a different host, the device form and a relative root; local absolute, relative and fixed-drive paths.
  - `FileRefResolverTests`: the existence check is injected, and unsafe spans are asserted to be refused **without a probe**. Relative spans resolve under the cwd; a span on the cwd's own share is probed; non-path code is skipped; 25 repeat resolves make 1 probe.
  - `GitHeadTests`, plus 3 new `PrStatusServiceBranchTests` and a statusline end-to-end run under node.
  
  The UNC hosts are all `.invalid`, so nothing is ever contacted. The **discriminating** cases point `gitdir:` at the `\\?\` device spelling of a real *local* git dir, which proves refusal without any network. Against the pre-fix resolvers, 5 of these fail: the old code read the branch through the device path, and `FindGitDir` returned the UNC target.
- [x] Dogfood (user-confirmed on 2026-09-29):
  - a session reply with a `` `\\host\share\x.md` `` inline span stays plain text and doesn't stall;
  - relative and absolute local file refs are still clickable;
  - PR status and the statusline branch still work in a linked worktree.

**Verify.** Done 2026-09-29: `dotnet build perch.slnx` is clean, and the .NET suite passes 1527 with 1 skipped.

**Landed:** commit `4932686`: `LocalPath`, `FileRefResolver`, the three `gitdir:` resolvers and `MarkdownView`.

<a id="cp10"></a>
### CP10 — Named pipes: current-user only, park the valet hook · 🟠 P1 · S · ✅

**Problem.**
- `perch-valet` and `perch-control` are fixed, machine-global names, with no `CurrentUserOnly` or `PipeSecurity` (`ValetServer.cs:35`, `ControlServer.cs:30`). Clients never check who owns the server (`Perch.Hook/Program.cs:270`, `Program.cs:222`).
- On a shared or RDS host, another user can squat the pipe. They then see every tool payload and can reply `allow` (approving the call without a prompt) or `deny` with an injected reason.
- The valet feature is parked, yet its hook is still registered on every PreToolUse (`ClaudeUserSettings.cs:30`). Each call pays up to about 200ms of `Connect(200)` spin when the tray is down.
- Neither server has a read timeout or line cap, and pipe creation failing once disables the pipe permanently.

**Tasks**
- [x] **Changed from the plan, on request: the permission valet is removed outright, not just unregistered.** It was never armed: no tray toggle ever set `_valetArmed`, so every relay answered "pass". Removed:
  - `ValetServer`, `ValetProtocol` and `ValetPromptWindow`;
  - the tray's server start and `DecideValet`;
  - the hook's `valet` event and its pipe client;
  - the `PreToolUse` registration.
  
  That takes the `perch-valet` pipe, its squatting exposure and the per-tool-call connect cost away entirely.
- [x] Existing installs need no migration step. `ReconcileHooks` strips every Perch-managed entry before re-adding the set, so the old valet entry goes at the next tray launch, including marker-less ones. Until then, the new hook treats `valet` as an unknown event: it exits 0 with empty stdout, after about 115ms of Debug-build startup, and never touches a pipe.
- [x] Tests:
  - `ValetServerTests` deleted. Its one non-valet assertion, `ControlledSessions.Owns(null)`, moved into the existing `ControlledSessionsTests`.
  - `ClaudeUserSettingsHookTests` drops `valet` from the expected set, and gains `Reconcile_StripsTheRetiredValetHook_FromAnOldInstall`: marked and marker-less valet entries go, and nothing re-adds them.
- [x] `perch-control` is now `perch-control[-dev]-<user>`. The user part is the Windows SID (stable across renames), or the account name on macOS/Linux, where .NET backs the pipe with a Unix socket in the per-user temp dir. Only `ControlProtocol.PipeName` builds the name, and the server and the `perch` CLI both use it. The hook never used this pipe.
- [x] Both ends open the pipe with `ControlProtocol.Options` (`Asynchronous | CurrentUserOnly`):
  - the server's ACL admits only this user (owner-only socket permissions on Unix);
  - the client's `Connect` checks that the server is owned by this user. **This check is what defeats a squat.** A SID isn't secret, so another user can pre-create the name, but the CLI then refuses with "owned by another user" before writing the intent.
- [x] Client: `TokenImpersonationLevel.Identification` (`ControlProtocol.ClientImpersonation`), so the tray can learn who is calling but never act as them.
- [x] `ControlServer`:
  - Each request goes through a new pure `ControlProtocol.ReadLineAsync`, which never buffers more than `MaxLineBytes` (64KB). An oversized line gets a "too large" reply and never reaches the handler.
  - A 5s read timeout drops a silent client.
  - A failed pipe creation retries with backoff (500ms doubling, capped at 30s) instead of returning. Before, one failure left the tray deaf for its whole lifetime.
  - The timeout and the first backoff are constructor test seams.
- [x] Tests (13 new, 23 in the file):
  - the existing round-trips, now using the hardened client;
  - an oversized line is refused and the handler never called;
  - a silent client is dropped after the (200ms test) timeout;
  - a pipe name held by a one-instance blocker is retried and answered once the blocker goes;
  - the pipe name is per profile and per user (it ends in the SID on Windows);
  - the options are current-user-only with identification;
  - 8 cases for the bounded reader: CRLF, trailing bytes, partial, empty, exactly at the cap, one byte over, over with no newline.
  
  These couldn't be run against the old server, which lacks the seams, but each one targets something the old code did: the silent client hung forever, the oversized line got "didn't understand", and the held name was never retried.
- [ ] **Not unit-testable:** the cross-user squat itself needs a second Windows account. Manual check: as user B, create a pipe with user A's name, then run `perch --resume …` as user A. Expected: "owned by another user", and B receives nothing.
- [x] Dogfood: with the tray running, `perch`, `perch -c` and `perch --resume <id>` from a terminal still open windows in the tray (the name changed, so the CLI and the tray must come from the same build). *(User-confirmed on 2026-09-29 against the dev tray via `run.bat`, which now works from any folder and forwards Perch's arguments after `--`; commit `296de4a`.)*
- **Known edge:** .NET makes the pipe owner the token's *owner*, which is the Administrators group for an elevated admin token. An **elevated** tray plus a non-elevated `perch` CLI therefore fail the owner check and the CLI reports "owned by another user". Acceptable, since the tray isn't meant to run elevated, but it's recorded here in case someone hits it.

**Verify (valet removal).** Done 2026-09-29: `dotnet build perch.slnx` is clean, and the .NET suite passes 1522 with 1 skipped (six valet tests deleted, one migration test added). The built hook run as `perch-hook valet perch-valet` exits 0 with empty stdout.

**Landed (valet removal):** commit `6165cbe`.

**Verify (control pipe).** Done 2026-09-29: `dotnet build perch.slnx` is clean, and the .NET suite passes 1535 with 1 skipped.

**Landed (control pipe):** commit `2ab614c`: `ControlProtocol`, `ControlServer`, `Program.ForwardSessionIntent` and `ControlServerTests`.

<a id="cp11"></a>
### CP11 — Hardened shared `GitRunner` · 🟡 P2 · M · 🟦

**Problem.**
- git runs automatically in untrusted repos: `GitStatsService` every 3s, plus `GitRepoService` status/diff and the statusline `.mjs`. None of these neutralise `core.fsmonitor`, `diff.external` or textconv. A repo that arrives with a pre-populated `.git` (zip, shared drive, or a config written by an injected session) runs its command on the next poll.
- There are four copy-pasted `RunGit` implementations (GitRepoService, GitStatsService, MarkdownProjectScan, ProjectFileScan), and no global concurrency cap.
- `PrStatusService.cs:198` blocks thread-pool threads on `_gate.Wait()`.

**Found first (git 2.55, a scratch repo whose own config pointed each key at a marker-writing `echo`):**
- `status` and `ls-files --others` run **core.fsmonitor**.
- `diff` and `diff --numstat` run fsmonitor, the **filter driver** `.gitattributes` selects (`filter.<driver>.clean`), and **diff.external**.
- `diff` and `show` run **textconv** (`diff.<driver>.textconv`).
- `log --format` ran nothing.

The planned `-c` set doesn't cover filter drivers at all. Their names are arbitrary, so they can't be blanked by a fixed override.

**Tasks**
- [x] One `Perch.Core` `GitRunner` (`Data/GitRunner.cs`). It:
  - uses the absolute git path (CP7);
  - has two trust levels:
    - **Automatic**, for everything Perch runs by itself, including every read the UI triggers: `-c core.fsmonitor=false -c log.showSignature=false`, plus `--no-ext-diff --no-textconv` inserted after a `diff`/`show`/`log` subcommand. It also blanks **every filter driver the repo's own config defines** (`filter.<name>.clean/smudge/process=` and `required=false`), found with `git config --show-scope --show-origin -z --list`, which reads config and runs nothing. Drivers from global or system config, such as git-lfs, are the user's and keep working. That probe is cached per directory until one of its config files changes (length or mtime), and at most for a minute. It is never cached when the repo's config has an `include`, an `includeIf` or per-worktree config, since those can pull in a file that doesn't exist yet.
    - **UserAction**, for the stage/unstage/discard/apply/commit the user clicks: git behaves as in their terminal, so their hooks and filters run. Only fsmonitor is off.
  - applies a timeout with tree-kill;
  - caps git at **4 processes at once, app-wide**. A caller waits up to its own timeout for a slot, then gets exit -1.
  - **Changed from the plan:**
    - `core.hooksPath=` isn't passed. No read-only command fires a hook, and the only hook-firing call is the user's own commit, where their hooks (lint-staged, commit-msg checks) are expected to run, as they would in a terminal.
    - `diff.external=` is replaced by `--no-ext-diff`, which also covers `diff.<driver>.command`.
    - Filter drivers are added: the plan missed them, and they're the vector no fixed override can reach.
    - The gate is a synchronous `Wait` with a timeout, since every caller is already a synchronous background call that blocks on the process anyway.
- [x] All four callers migrated: `GitRepoService` (reads Automatic, writes UserAction), `GitStatsService`, `MarkdownProjectScan` and `ProjectFileScan`. Their copy-pasted runners are gone.
- [x] `PrStatusService` awaits its gate (`WaitAsync`) instead of blocking a pool thread, and tolerates being disposed while queued.
- [x] Statusline `.mjs`: `gitHardening()` builds the same overrides (fsmonitor off, the repo's own filter drivers blanked, via `git config --show-scope -z --get-regexp ^filter\.`), and both counts pass `--no-ext-diff --no-textconv`.
- [x] `GetChangeStats` no longer runs a `git diff --no-index` per untracked file (up to 100 processes in a row). It counts lines in-process with `CountUntrackedLines`, the way git's added-file diff does: every line, including a final one without a newline, and 0 for binary (a NUL in the first 8000 bytes). It skips links and files over 8 MB, and stops after 500 files.
- [x] Tests (19 new cases):
  - `GitRunnerTests`: the argument builder (Automatic/UserAction, the insertion point, a path named `diff` not mistaken for a subcommand, dotted filter names); the config parser (only local/worktree drivers, case kept, stamps invalidated by a config edit, include/includeIf/worktree config never cached); and line counting.
  - **End to end:** a real repo whose own config sets all four vectors to marker-writing `echo` commands. They're config strings git's own shell runs; no executable file is planted. A plain-git control run creates every marker. Then every Automatic path (status, working/tree/commit diff, change stats, numstat, the file scan) creates none and still returns the right answers. The probe is warmed **before** the config turns hostile, so the test also proves the cache notices the change.
  - A statusline end-to-end test does the same under node: the control fires, and the script counts `S0U1` with no marker.

  With the hardening switched off (GitRunner passing args through, the script's old call), both end-to-end tests fail.
- [x] **Found in dogfood: a repo-local git-lfs.** Git for Windows puts git-lfs in *system* config, which was never blanked, so the result there is unchanged. But `git lfs install --local` writes `filter.lfs.*` into the repo's own config. Blanking that made every stat-dirty LFS file read as ` M`, and its diff showed the pointer text against the real content. The statusline counts had the same problem.
  - Fix: a repo-local driver named exactly `lfs` is left alone when every entry is one of the stock values `git lfs install --local` writes. Those are `git-lfs clean -- %f`, `git-lfs smudge [--skip] -- %f`, `git-lfs filter-process [--skip]` and `required=true|false`. It runs the same `git-lfs` a system install would, so the exposure is no greater than before. If any single entry is anything else, the whole driver is blanked as before.
  - The fix covers both `GitRunner.ParseConfigList` and the statusline's `gitHardening()`.
  - Tests (3): a parser unit, where the stock form and the `--skip` form are kept, while one hostile entry, a hostile duplicate, `LFS` or the stock commands under another name are blanked. Then two end-to-end runs, a `GitRepoService` status/numstat and the statusline (`S0U0`), each in a real `git lfs install --local` repo with a stat-dirty LFS file. The GitRunner run includes a blanked-filter control that shows the file as modified. With the exemption switched off, all three tests fail.
- [ ] Dogfood: the git-stats glyph, the changed-files panel with an untracked file, the review window's diffs, stage/commit from the Tree window, and a statusline with git counts, all in a normal repo and one that uses git-lfs. *(The git-lfs cases are checked from the command line, for both system-scope and repo-local lfs. The UI walk-through is still owed.)*

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean; the .NET suite passes 1666 with 1 skipped. After the lfs fix: 1711 passed, 1 skipped.

**Landed:** commit `c8557d9`: `GitRunner`, plus `GitRepoService`, `GitStatsService`, `MarkdownProjectScan`, `ProjectFileScan`, `PrStatusService` and `StatuslineScript`. The lfs fix is commit `1a8283c`.

<a id="cp12"></a>
### CP12 — cmd-shim metacharacters (VS Code / GitKraken launch) · 🟡 P2 · S · ✅

**Problem.**
- `FileRevealer.cs:35-38` (`code` → `code.cmd`) and `GitKrakenLauncher.cs:55-67` (`cmd.exe /c <cli.cmd>`) go through cmd. .NET only quotes arguments that contain whitespace or quotes, so `&`, `|` and `^` pass through. A file named `x&calc&.md` plus "Open in VS Code" runs calc. A benign `C:\work\R&D` breaks the GitKraken launch.
- `FileRevealer.cs:43` falls back to shell-opening the path itself, so a `.bat` file ref gets executed.

**Also found:** three more places shell-executed `code` with a hand-quoted path: "Open transcript in VS Code" on the overlay and in the daemon list, and the markdown window's "Open in VS Code". Quoting stops `&`, but cmd still expands `%` inside quotes, so a name like `%PATH%.md` was rewritten.

**Tasks**
- [x] **Chosen: quote every argument through cmd, and refuse what quoting can't neutralise.** Windows can only run a `.cmd`/`.bat` through cmd, which re-parses the command line (the "BatBadBut" class), so handing the launch to the system doesn't avoid the problem. The plan's first option was to launch `Code.exe`/`gitkraken.exe` directly. A first cut did that by reading each Electron shim down to its `.exe` (commit `cd8dc69`), but it was replaced (on review) by the simpler rule below.
  - `Perch.Data.CmdShim.StartInfo`: a real `.exe` starts directly with an argument list. A `.cmd`/`.bat` runs as `cmd /d /s /c ""tool" "arg" …"`, with every argument quoted (which makes `& | < > ^` literal) and AutoRun skipped. `/s` makes cmd strip exactly the outer pair of quotes.
  - It **refuses** (no launch) when the tool or an argument holds `% ! " & | ^ < >` or a line break. `%` still expands inside quotes, `!` does under delayed expansion, and `"` would end the quoting. The others are refused too, to keep one rule rather than caret-escaping. The cost: a file whose name holds one of those characters isn't opened in VS Code; it falls back to the viewer-safe default handler (CP8), or GitKraken just doesn't launch.
- [x] Callers:
  - `FileRevealer.OpenInEditor` (Windows) goes through `CmdShim`; not found or refused → the viewer-safe default handler. VS Code no longer flashes a console (the shim runs windowless).
  - `GitKrakenLauncher.RunCli` goes through `CmdShim`; refused → no launch. A path with a space or `&` now launches, which the old unquoted `cmd /c` broke.
  - The overlay's and daemon list's "Open transcript in VS Code" and the markdown window's "Open in VS Code" now call `IFileRevealer.OpenInEditor` instead of shell-executing `code` themselves.
- [x] Remove the default-handler fallback, or restrict it to viewer-safe extensions (`.md .txt .json .png …`). *(Done in CP8: the fallback is `IFileRevealer.OpenWithDefault`, gated on `OpenTargets.IsViewerSafeFile`.)*
- [x] xUnit `CmdShimTests` (12 cases; the planted tools are empty files, and nothing is ever started): a shim runs through cmd with the exact quoted command line; `x&calc&.md`, `R&D`, `|`, `%PATH%`, `!`, `^`, `<>`, a quote and a line break are each refused; a shim on a path holding a metacharacter is refused; a real `.exe` gets an argument list with `&` passed through untouched.
- [x] Dogfood: "Open in VS Code" from a file ref, a changed file and the markdown window; "Open transcript in VS Code" from the overlay and the daemon list; "Open in GitKraken" on a repo whose path has a space or `&`. VS Code should open without a console flash. *(User-confirmed on 2026-09-30.)*

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean; the .NET suite passes 1678 with 1 skipped.

**Landed:** commit `cd8dc69`, simplified in `a29a4d1`: `CmdShim`, plus the Windows `FileRevealer`, `GitKrakenLauncher`, `OverlayCanvas`, `DaemonListWindow` and `MarkdownWindow`.

<a id="cp13"></a>
### CP13 — Control-pipe intent validation + launcher quoting · 🟡 P2 · S · 🟦

**Problem.**
- `SessionOpenIntent.Parse` (`ControlProtocol.cs:116-123`) doesn't validate `mode`, `cwd` or `resume`. Any same-user process can open a `bypassPermissions` session in a folder of its choosing, focused.
- `--open-intent-file <any path>` deletes that file (`Program.cs:303-310`).
- `SessionLauncher.cs:19,41-48` places the session id unvalidated into `powershell -Command "claude --resume {id}"`. `-d "{cwd}"` breaks on a trailing `\`, and wt treats `;` as a subcommand separator.
- `SessionLock.Acquire` (`ClaudeSessionController.cs:91,144`) always writes to the primary `sessions/` dir and ignores a `false` return.

**Tasks**
- [x] `Parse` holds every field to `FromArgs`' rules: the folder must be an existing, fully qualified directory, a resume id must be a session id, the model a plain token, and the mode one of `default`, `auto`, `plan`, `acceptEdits` or `bypassPermissions` (the session window's list). **Any** invalid field rejects the whole request, rather than opening a session on a dropped or guessed value; the CLI gets "didn't understand". `FromArgs` uses the same mode list, so an unknown `--permission-mode` is dropped there too.
  - **Changed from the plan: `bypassPermissions` is still accepted over the pipe.** The pipe is current-user-only (CP10), and a process running as the user can already start `claude --permission-mode bypassPermissions` in any folder itself, so refusing it would only break `perch --permission-mode bypassPermissions` while the tray runs.
- [x] `--open-intent-file` reads, and then deletes, only a path `SessionOpenIntent.NewHandoffFile()` could have produced: directly in the temp folder, named `perch-intent-<32 lower-case hex>.json`. Any other path is ignored and never touched. `DetachTray` writes its file through the same helper.
- [x] `SessionLauncher`:
  - `Reopen` refuses (returns false, so the app offers to copy the command) any id that isn't a plain session id (`ClaudeCli.IsSessionId`), since the id lands unquoted on a cmd, PowerShell or wt command line.
  - The wt `-d` value comes from `ClaudeCli.WindowsTerminalStartDir`: a trailing backslash gets a harmless `.` (`"C:\."`), so it can't escape the closing quote, and `;` is escaped as `\;`.
  - **Changed from the plan: no `ArgumentList`.** wt and cmd re-tokenise their own command lines, so an argument list gives no protection there; validating the tokens that go on the line does.
- [x] `SessionLock`: the controller writes, reads and releases the lock in the **launching config dir's** `sessions/` (`SessionLock.SessionsDirFor(configDir)`, null = the primary), which is where that account's `perch-hook` looks. It **refuses to start** (`InvalidOperationException`, surfaced as the window's launch failure) when another live Perch holds the lock, checked before anything else. The session window's own pre-launch check reads the same dir.
  - **Changed from the plan: a lock that merely can't be written still doesn't block.** `Acquire` returns false for both "held by another live Perch" and "write failed", and the lock is documented as best-effort; only the first means two writers on one transcript.
- [x] xUnit (30 new):
  - `Parse` rejects a missing or relative folder, a short or non-id resume, an unknown mode, a mode with a flag smuggled in, and a non-token model; it accepts each of the five modes (bypass included); `FromArgs` drops an unknown mode.
  - The handoff check accepts only Perch's own file (not the wrong case, extension or folder, a subfolder, a `..` climb, a relative path, or an arbitrary document).
  - `WindowsTerminalStartDir` handles a plain path, spaces, a drive root, a trailing backslash and `;`; `IsSessionId` refuses `;`, `&`, a backtick, `$(…)`, empty and null.
  - `SessionsDirFor`; and the controller refuses a session a live other process (a real one on the host, nothing spawned) holds in a non-primary config dir, writing nothing to the primary.
  - Existing control-pipe tests now use a real folder, since a non-existent `C:\proj` is (correctly) rejected.
- [ ] Dogfood: with the tray running, `perch`, `perch -c`, `perch --resume <id>` and `perch --permission-mode plan` from a terminal still open windows; "Reopen in terminal" (Windows Terminal) for a session whose folder is a drive root, like `D:\`; and a session under a non-primary account shows its `.perch-lock` in that account's `sessions/` folder while it runs.

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean; the .NET suite passes 1708 with 1 skipped.

**Landed:** commit `2cf418a`: `SessionOpenIntent` (`Parse`, handoff file), `ClaudeCli` (`IsSessionId`, `WindowsTerminalStartDir`), `SessionLock.SessionsDirFor`, `ClaudeSessionController`, `SessionLauncher`, `Program` and `SessionWindow`.

<a id="cp14"></a>
### CP14 — Never wipe `.claude.json`; atomic writes everywhere · 🟠 P1 · M · ✅

**Problem.**
- `DirectoryTrust.cs:136-139,176-186` treats a read failure as "no file". Accepting trust while `.claude.json` is locked or 0 bytes replaces it with `{"projects":{…}}`, which wipes the OAuth account and all project state.
- `ClaudeUserSettings.cs:95,132,346,402,426` and `Perch.Hook/Program.cs:451` write `settings.json` by truncate-then-write, and `ReconcileHooks` rewrites it on every launch even when nothing changed.
- `TodoStore.cs:117` is non-atomic, and a failed parse loads as an empty list that the next save persists.
- `AppSettings.Save` (`AppSettings.cs:878`) uses a fixed temp name, doesn't flush to disk, and swallows errors.

**Tasks**
- [x] `DirectoryTrust.GrantAt`:
  - Starts a fresh object **only** when the file is provably missing (`AtomicFile.TryRead` → `Missing`).
  - Refuses (returns false, bytes untouched) on a read failure, an empty or whitespace file, unparseable JSON, or a non-object (`[]`, `null`).
  - Re-reads just before the atomic replace and redoes the merge if Claude changed the file in between (up to 3 attempts, then gives up).
  - An already-trusted folder returns true with **no write**.
  
  The old `ReadAllText` returned null for both "missing" and "locked", and null meant `new JsonObject()`: that was the wipe.
- [x] New shared `Perch.Data.AtomicFile`:
  - `Write` writes a unique `<path>.<guid>.tmp` beside the target, UTF-8 with no BOM, and `Flush(true)` to disk. It then does `File.Move(overwrite)`, retried 5 times over about 0.6s on `IOException` or access denied. It never leaves a temp behind, and throws once the retries run out.
  - `WriteIfChanged` skips identical text.
  - `TryRead` reads with shared access and returns `Missing` / `Ok` / `Failed`, so every read-modify-write can tell "no file" from "couldn't read it".
  
  The hook, which doesn't reference Core, has a mirrored `AtomicWrite` for its self-heal strip.
- [x] Converted to `AtomicFile`:
  - the planned set: `settings.json` (every `ClaudeUserSettings` writer, `PluginManager.RemoveRegistration`, and the hook's self-heal), todos, `AppSettings` and `.claude.json`;
  - **widened to "everywhere" that holds user state:** `AchievementStore`, `FileSecretStore`, `StatuslineStore` (the user's own profiles), and `ClaudeConfigSet`'s self-reported roots, which used a fixed `.tmp` name.
  
  Left alone: small ephemeral sidecars (`.mode`, locks, markers) and the Markdown editor's save of the user's own document, where a rename would change the file's identity (ACLs, hard links).
- [x] **Changed from the plan:**
  - `ClaudeUserSettings` compares **meaning, not bytes**. `OpenForWrite` + `Commit` skip the write when the result is `JsonNode.DeepEquals` to what was read. A byte comparison would rewrite the file on every launch after Claude Code reformats it.
  - `OpenForWrite` also refuses a non-object root; the old `as JsonObject ?? new JsonObject()` replaced one.
  - `ReconcileHooks` still returns true when there was nothing to write.
- [x] `TodoStore`:
  - A file that exists but can't be read loads as an empty stand-in with `SaveSuppressed`, so it's never overwritten that session.
  - One that won't parse is first copied to `todos.unreadable.json`, and then the list starts over.
  - **Changed from the plan:** `todos.unreadable.json`, not `.bak`, to match the existing `settings.unreadable.json`. If the copy can't be written, saving is suppressed too.
- [x] `AppSettings.Save`: saves run through a lock (`SaveGate`), go through `AtomicFile`, and failures are logged to `logs/settings.log`. That uses a new `DiagnosticLog` (the capped-append routine lifted out of `LaunchLog`, which now delegates to it).
- [x] Tests (23 new):
  - `AtomicFileTests` (7): no BOM, no temp left, creates directories; `WriteIfChanged` leaves the mtime alone; 16 concurrent writers never tear the file; a briefly held target is retried; a held target throws with the target and temps untouched; `TryRead` distinguishes missing, ok and failed.
  - `DirectoryTrustTests` (+6): empty, whitespace, `[]` and `null` files are left byte-for-byte; a **locked** file keeps its `oauthAccount`; granting an already-trusted folder leaves the mtime alone.
  - `ClaudeUserSettingsHookTests` (+7): reconcile of an already-reconciled file makes no write (mtime pinned); a reformat alone makes no write; an empty, `[]`, `null` or unparseable file is left alone; a locked file is refused.
  - `TodoStoreTests` (+2): a torn file is copied aside and the next save works; a file locked at load is never overwritten that session.
- [x] Dogfood: accept folder trust for a new folder from a Perch session and confirm `.claude.json` keeps its `oauthAccount` and other projects. Restart the tray twice and confirm `~/.claude/settings.json`'s modified time doesn't change the second time. *(User-confirmed on 2026-09-29.)*

**Verify.** Done 2026-09-29: `dotnet build perch.slnx` is clean, and the .NET suite passes 1557 with 1 skipped. One full run hit a single unnamed intermittent failure. The two timing-sensitive new tests were hardened (the concurrent-writers race also tolerates access denied, and the retry test releases its lock from a dedicated thread rather than the pool), and 15 repeated runs of the affected classes were clean. The machine was slow throughout: a baseline run without CP14 also took about 2 minutes, against about 40s earlier in the day. The classes on the new write path took 3.2s in total, so fsync isn't the cost.

**Landed:** commit `3d1dd96`.

<a id="cp15"></a>
### CP15 — Recording-export redaction gaps · 🟡 P2 · S · ✅

**Problem.**
- `TranscriptRedactor.cs:52` ships unparseable lines verbatim, including the partially written trailing line of a live transcript.
- `:104-105` deep-clones preserve-listed keys (`name`, `id`, `type`, `model`) at any depth, so `{"name":"Customer A"}` in a tool input survives.
- `:146-148` keeps whole `<local-command-stdout>` strings that contain "Set model to".
- `RecordingExporter.cs:267-276` keeps the `/rename` title, `waitingFor` and `bridgeSessionId` in the snapshot.

**Tasks**
- [x] Unparseable lines (and an unparseable agent meta sidecar) become the redaction token. The line count stays the same, and the readers skip the token as malformed, just as they skipped the original partial line.
- [x] Preserve keys apply only at the structural path the readers use: the record, its `message`, that message's content blocks, and an image block's `source`. Inside a `tool_use` block's `input`, only `subagent_type` is kept, because the roster reads it. Everything else is payload, where nothing is preserved: `toolUseResult`, a `tool_result`'s content, nested input values and progress data. Preserved values must also be scalars, so an object under a preserved key is recursed into, not cloned. Numbers and bools still pass everywhere, so token counts and `toolUseResult.isAsync` survive. `usage` left the preserve list, since its token counts are numbers and pass anyway. The meta sidecar's keys apply at its top level only.
- [x] Model-switch stdout is rebuilt around the extracted name: `<local-command-stdout>Set model to ESC[1m{name}ESC[22m</local-command-stdout>`. `Kept model as` is handled the same way; it was previously scrubbed, which lost the model. The name goes through `ModelContext.ParseDisplayName` on the unwrapped text and must look like a model name (letters, digits and ` .,:()[]/_+·-`, at most 64 characters). Otherwise the whole stdout is scrubbed.
- [x] The redacted snapshot is an allowlist (`RecordingExporter.RedactSnapshot`). It keeps `pid`, `sessionId` and `cwd` (rewritten), plus only what the projector reads back: `entrypoint` if it's a plain token, and `bridgeSessionId` replaced by the placeholder `session_redacted`. Replay still shows the session as remote-controlled, but the real claude.ai id never leaves the machine. The title, `waitingFor`, `status` and any future field are dropped. Unredacted exports keep the old rewrite.
- [x] xUnit (`ReplayRedactorTests`, 7 new cases, 2 updated):
  - a partial trailing line and a partial meta sidecar;
  - `name`/`id`/`type`/`model` inside a tool input, a nested input, a `toolUseResult` and a `tool_result`'s content blocks, with the tool's own name, id pairing, `subagent_type`, `isAsync` and token counts still intact;
  - an object under a preserved key, and an image source's shape;
  - nested meta keys;
  - model lines with trailing secrets (`Set`/`Kept`), plus one whose "name" isn't a name;
  - the snapshot allowlist, covering the bridge placeholder, no bridge, an odd entrypoint and unparseable JSON.
- [x] Dogfood: export a redacted recording of a session that has sub-agents, a `/model` switch and Remote Control, and replay it. The model, token burn, sub-agent roster and RC glyph should match the unredacted replay. *(User-confirmed on 2026-09-30.)*

**Known residual:** object *keys* aren't redacted, only values. Tool inputs have schema-defined keys, but a free-form map (such as an `env` object) could carry a name in a key. This is left as is. Scrubbing keys would make them collide, since one object can't hold two `[redacted]` keys, and it would change the input shapes the tool summaries read.

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean; the .NET suite passes 1718 with 1 skipped.

**Landed:** commit `601107c`: `TranscriptRedactor`, `RecordingExporter`.

<a id="cp16"></a>
### CP16 — Small security hardening batch · ⚪ P3 · M · 🟦

- [x] **Statusline template injection** (`StatuslineScript.cs:88-96`). The chained `.Replace` substitutes `@@NAME@@` inside the already-substituted template, which breaks out of the JS string literal.
  - *Done:* `Generate` now does one regex pass over the body with a placeholder → value map, so substituted text is never scanned again.
  - Tests: two cases, `@@NAME@@`/`@@PERCHSETTINGS@@`/`@@DEV@@`/`@@TEMPLATE@@` inside the template and a hostile profile name. The generated script contains the template's JSON literal verbatim, and under node it prints the template text unchanged.
- [x] **Loopback OAuth listener** (`LoopbackListener.cs:51-67`). The plan said: "Loop accepting until you get `GET /callback` with a matching random `state`, ignoring everything else. Add `state` to the auth URL."
  - **Changed from the plan (user decision):** no `state`. GoTrue runs its own `state` with GitHub and doesn't echo a client one back to `redirect_to`. The equivalent, a per-sign-in nonce in the redirect path, needs the Supabase Redirect URLs allowlist widened to a wildcard first, so it was declined.
  - *Done:*
    - The listener keeps accepting until a connection is `GET /callback` with a non-empty `code` or `error`. Everything else (a favicon, a port scan, a page poking 127.0.0.1) gets a 404 and the wait goes on.
    - Each connection has 5s to send its request line, so a silent one can't block the real redirect.
    - A forged callback that does carry a code still can't sign anyone in, because PKCE binds the code to this sign-in's verifier.
    - What's left: a local process that wins the race with a bogus `code`, failing that one sign-in.
  - Tests: `IsCallback` (9 cases), plus an end-to-end run where a stray request is answered 404, a silent client is dropped, and the real callback then completes the wait.
  - **Found along the way:** see "Loopback port range" below.
- [x] **Supabase config override** (`SupabaseConfig.cs:29-47`, `DotEnv.cs`). *Done, both halves:*
  - `Resolve` honours `PERCH_SUPABASE_URL`/`.env.local` only for a dev instance, or a build with no compiled-in project (a fork).
  - The refresh token is **stored per origin**, under the key `supabase.refresh_token@https://<host>`, and only ever read back for that origin. An override pointing elsewhere finds no token, and nothing goes to it. The pin matters because `PERCH_DEV=1` can force dev mode on a release build, and the macOS keychain entry (service `Perch`) is shared between profiles.
  - Upgrade: the compiled-in origin adopts a token at the old unkeyed key once and clears it, so release users stay signed in. A dev checkout (no compiled-in project) doesn't adopt it, so **a dev instance signs in again once**.
  - Tests: the release/dev/fork gate. An overridden origin sends no request and leaves both real tokens untouched. The legacy token is adopted into the keyed slot.
- [x] **Token refresh single-flight** (`SupabaseSocialClient.cs:187,760`). *Done:* a `SemaphoreSlim` around every refresh (restore and `ValidAccessTokenAsync`), re-checking for a fresh token once inside, so queued callers reuse the first caller's refresh. Test: five concurrent calls against a token endpoint slowed to 150ms make exactly one refresh.
- [x] **Realtime reconnect** (`SupabaseRealtime.cs:241-263,315`). *Done:*
  - Backoff delays are jittered ±25%.
  - A new `FrameAssembler` gathers a message's bytes and decodes them once at the end. Decoding each 16 KB fragment separately corrupted a multi-byte character split across a fragment boundary.
  - **Also added:** messages over 1 MiB are dropped whole instead of buffered without limit.
  - Tests: a rocket emoji split mid-sequence decodes intact; an oversized message is dropped and the next one still arrives; the jitter bounds.
- [x] **`PERCH_SESSION_LOG` / `PERCH_VDM_DEBUG`** (`ClaudeSessionController.cs:127`). *Done* (`Perch.Data.DebugSwitches`):
  - A value only switches a log on: any non-empty value other than `0`/`false`. It's **never used as a path**.
  - The session stream goes to `logs/session-stream.log`, capped at 32 MB. The VDM log goes to `logs/vdm.log` (1 MB), instead of `%TEMP%`. Both are in Perch's per-user settings folder, via `DiagnosticLog.AppendRaw`, which also refuses any file name with a directory part.
  - While either is on, the tray tooltip reads "debug logging on" and the tray menu opens with a disabled "⚠ Debug logging on: … — see <logs dir>" item.
  - Tests: the on/off values (an old-style path value just means on), the warning text, and names with a directory part being refused.
- [x] **Login Run key** (`App.axaml.cs:562`, `LoginItem.cs:27`). *Done:* `SyncLoginItem` registers only when `InstallChannel` is `Setup`. A portable or dev copy **leaves an existing entry alone** rather than removing it: settings are shared with the install, so removing it would delete the installed Perch's own entry. It still unregisters when the user turns start-at-login off. In a portable copy, choosing "At login" does nothing. The setting doesn't say so yet; that's a small follow-up if wanted.
- [x] **Hook exe location.** Moved from Roaming `%APPDATA%\Perch[ (Dev)]\bin` to `%LOCALAPPDATA%\Perch[ (Dev)]\bin` on Windows only; Unix is unchanged. *Done:*
  - The release reconcile rewrites every hook onto the new path, since it recognises its entries by the binary's name.
  - On the first launch after the move, a dev instance also strips entries that name the old path, before reconciling. Dev entries whose `_perch` marker Claude Code dropped are only recognisable by path.
  - **7-day grace (user decision):** the old folder stays for a week, stamped at the move, because sessions already running keep calling it until they restart. After that, a later launch deletes it. Uninstall removes both folders.
  - `perch-hook` finds its `perch.path` breadcrumb beside its own binary first, then at the old location.
  - Test: the dev move leaves exactly one set, at the new path, with release's entries untouched. The release move is covered by the existing `Reconcile_MatchesBinaryPath_AcrossSeparatorsAndExeSuffix`.
- [x] **Artifact detection** (`TranscriptReader.cs:576-583`). *Done:* `IsArtifactUrl` requires an absolute `https` URL on host `claude.ai` with the default port, no userinfo, and `/artifact/` in the path, not the query. Tests: 12 cases, including `claude.ai.evil.example`, `claude.ai@evil.example`, http, a non-default port, and `/artifact/` only in the query.
- [ ] Dogfood:
  - Social sign-in end to end (see the port-range finding below);
  - an upgrade from a signed-in release, which should stay signed in;
  - the tray warning with `PERCH_SESSION_LOG=1`;
  - the hook move on a machine with an existing install: hooks rewritten to `%LOCALAPPDATA%`, the old folder still there, sessions keep working;
  - start-at-login from the installed copy.

**Loopback port range (found here, fixed as a follow-up).** Windows reserves dynamic TCP port ranges for Hyper-V/WSL/Docker, and they can change on reboot. On the dev machine, 53588–53687 was excluded, which covered **all four** old OAuth candidate ports (53682–53685). There, `LoopbackListener.Start()` threw "Couldn't open a local port", so Social sign-in couldn't work.
- [x] The candidates are now **41532–41535**. They sit below the dynamic range (49152–65535 by default), which is where those reservations are carved. All four were checked free and bindable on the affected machine, and the real `Start()` now binds there.
- [x] The user added the four `http://127.0.0.1:4153x/callback` entries to the Supabase Redirect URLs allowlist (Authentication → URL Configuration) **before** the code change. The old `53682`–`53685` entries stay until no installed Perch still uses them.
- [x] `backend/supabase/README.md` lists the exact entries. Tests pin the list and the below-49152 rule, and check that the real candidates bind on the test host.
- [ ] Dogfood: a real GitHub sign-in end to end (the redirect must land on the loopback, not the Site URL).
- [ ] Later: remove the old `53682`–`53685` allowlist entries once released builds have moved over.

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean (0 warnings); the .NET suite passes 1781 with 1 skipped.

**Landed:** commit `a236499`.

---

## Supply chain

<a id="cp17"></a>
### CP17 — CI permissions, pinning, deploy-secret scoping · 🟡 P2 · S · 🟦

**Problem.**
- **`release.yml`.**
  - It sets `permissions: contents: write` for the whole workflow (`release.yml:8-9`), so the build jobs get it too, and checkout keeps the token.
  - Actions are pinned by tag rather than SHA, including `softprops/action-gh-release@v2`.
  - `vpk` is unpinned (`dotnet tool install -g vpk`, `dnx vpk` in `publish.bat:55`).
  - `dotnet-version: '10.x'` floats, and there are no lock files.
- **Deploy workflows (`db-migrate.yml`, `functions-deploy.yml`).**
  - `SUPABASE_ACCESS_TOKEN` and `SUPABASE_DB_PASSWORD` are job-level env.
  - `supabase/setup-cli@v1` runs with `version: latest`.
  - There is no `environment:` gate, and `workflow_dispatch` accepts any branch.

**Tasks**
- [x] `release.yml`: the workflow default is now `contents: read`, and only the `release` job gets `contents: write`. That job checks nothing out and builds nothing. Both build checkouts use `persist-credentials: false`.
- [x] Every action is pinned to a commit SHA, with the version in a trailing comment. The pin is the latest release **within the major already in use** (checkout v4.4.0, setup-dotnet v4.3.1, upload-artifact v4.6.2, download-artifact v4.3.0, action-gh-release v2.6.2, setup-cli v1.7.3), so behaviour is unchanged. Newer majors exist (checkout v7, the artifact actions v7/v8, action-gh-release v3, setup-cli v3). `.github/dependabot.yml` (github-actions, weekly, one grouped PR) proposes those upgrades as reviewable PRs rather than taking them silently.
- [x] `vpk` is pinned to 1.2.0 in `.config/dotnet-tools.json`, matching the `Velopack` 1.2.0 library the app references. `release.yml`, `publish.bat` and `publish-mac.sh` run `dotnet tool restore` + `dotnet vpk`, replacing `dotnet tool install -g vpk` and the unpinned `dnx vpk`. The README drops its global-install step. vpk 1.2.161 exists, but the CLI and library move together, so bumping them is a deliberate separate change.
- [x] `global.json` pins SDK 10.0.401 (`rollForward: latestFeature`, no prerelease). Both build jobs set up .NET with `global-json-file: global.json` instead of the floating `'10.x'`.
- [ ] ⏸ **NuGet lock files: deferred.** The app head's target frameworks differ by host (the csproj drops the Windows TFM on macOS). So one committed `packages.lock.json` can't satisfy `--locked-mode` on both runners, since NuGet treats a framework mismatch as an inconsistent lock file. `publish -r win-x64` / `-r osx-arm64` would also need every RID declared in the lock file. The remaining gap is small: every direct `PackageReference` is already an exact version, and restore verifies nuget.org's repository signatures. Revisit if the head stops varying its TFMs by host.
- [x] Supabase workflows (`db-migrate.yml`, `functions-deploy.yml`):
  - `permissions: contents: read`, and `persist-credentials: false` on checkout.
  - The job runs only on `main` (`if: github.ref == 'refs/heads/main'`), so a dispatch from another branch is skipped.
  - `environment: production`.
  - Secrets are passed only to the steps that use them: link/push get the token and DB password, deploy gets the token. The project ref and the event SHAs go through `env:` rather than being interpolated into the script.
  - setup-cli is pinned by SHA, and the CLI to **2.115.0**, the version the migrations are tested with locally (pgTAP).
- [ ] **Owed (repo settings, a manual step):** in Settings → Environments → `production`, add required reviewers and set the deployment branches to `main` only. Until then, GitHub creates the environment on first use with no protection rules, so the workflows run as before without the approval gate.
- [ ] Verify on the next real runs: a `v*` tag builds both heads and publishes (the release job is the only writer), a migration push waits for approval once the environment is protected, and Dependabot opens its first grouped PR.

**Verify.** Done 2026-09-30. All four YAML files parse. The permissions, gates and pins were checked by reading the parsed YAML back. `dotnet tool restore` restores vpk 1.2.0 and `dotnet vpk` runs. `dotnet --version` resolves 10.0.401 through `global.json`, and `dotnet build perch.slnx` is clean. The pipeline files stay ASCII: the diff adds no non-ASCII characters, and `publish-mac.sh`'s two pre-existing em dashes are CP19's. The repo stores `publish-mac.sh` with LF line endings (`git ls-files --eol`).

**Landed:** commit `0c0b18d`: the three workflows, `dependabot.yml`, `.config/dotnet-tools.json`, `global.json`, `publish.bat`, `publish-mac.sh` and the README.

<a id="cp18"></a>
### CP18 — Code signing + signature verification · 🟡 P2 · L · ⬜

**Problem.**
- `install.ps1:77-110` verifies against a `SHA256SUMS.txt` taken from the same release, so it proves the download is intact but not who built it.
- There is no Authenticode signing, and MOTW is deliberately skipped, so SmartScreen never sees the exe.
- `install.ps1` is served from `main` HEAD.
- Velopack updates only check hashes within the same release.

The upshot is that a stolen maintainer or CI token means every one-liner install, and every existing install at its next update, runs attacker code. (This is noted as a known gap in `docs/distribution-plan.md`.)

**Tasks**
- [ ] Choose a signer (Azure Trusted Signing or SignPath OSS) and wire it into `vpk pack` signing in `release.yml`. Sign `perch.exe`, `perch-hook.exe` and Setup.
- [ ] `install.ps1`: check `Get-AuthenticodeSignature`: status `Valid` and the expected signer subject. Keep the SHA256 check.
- [ ] Add `actions/attest-build-provenance` for the release assets.
- [ ] Pin the one-liner to a tag or release ref, not `main`. Update README and the docs to match.
- [ ] Hash and execute without a gap: hash while streaming the download, and hold the file open with `FileShare.Read` until `Start` (`install.ps1:94-124`).
- [ ] Extend `tools/test-install.ps1` to cover the signature-check path (mocked).

---

## Build and hygiene

<a id="cp19"></a>
### CP19 — Build/installer hygiene · ⚪ P3 · S · 🟦

- [x] `publish-mac.sh:48,140` contains em dashes, which fails `tools/test-install.ps1`'s ASCII check. **The test currently fails.** Replace them with plain hyphens. *(Done 2026-09-30, alongside the new CI workflow, which runs `test-install.ps1` on every push.)*
- [x] `test-install.ps1`: build the ASCII file list from a glob so it covers `db-migrate.yml`, `functions-deploy.yml`, `tools/gen-dmg-background.sh` and `tools/focus.ps1`. Replace `Invoke-Expression` (line 21) with dot-sourcing a temp copy.
  - The glob is the three root release scripts, plus `tools/*.ps1|sh|cmd|bat`, plus every workflow. Compiled sources under `tools/` (the Swift DMG generator, IconGen) aren't shell-parsed, so they're out.
  - **Found:** the `.sh` LF check failed on any `core.autocrlf=true` checkout, which covers this machine and the GitHub Windows runner, so the new CI job would have gone red. A new `.gitattributes` pins `*.sh` to `eol=lf`. The four scripts were re-checked-out, so their stored bytes didn't change.
- [x] `PathInstaller.cs:17-34`: read and write `HKCU\Environment` with `DoNotExpandEnvironmentNames`, keep `REG_EXPAND_SZ`, and write only when the value changed. Today every install or uninstall permanently expands `%VAR%` entries in the user's PATH.
  - The editing is a pure Core helper, `Perch.Platform.PathList` (`WithEntry` / `WithoutEntry`, which return null for no change). An entry matches literally, or after `%VAR%` expansion, ignoring case, padding and a trailing slash.
  - Uninstall used to re-trim every entry and drop empty ones; it now removes only ours and leaves the rest byte-for-byte.
  - The value keeps its registry type. A new value is `REG_EXPAND_SZ`, as Windows creates it.
- [x] `publish.bat`: pass `-p:Version=%VERSION%` to both publishes, and hash only this version's files, not stale nupkgs. `release.yml`: pass `-p:Version` to the Windows hook build.
  - Also the AOT-fallback hook publish.
  - Only `Perch-<version>-*.nupkg` are hashed. Every other file is rewritten by each pack.
  - **Found:** `-Exclude 'SHA256SUMS.txt'` is silently ignored alongside `-LiteralPath` in Windows PowerShell 5.1, so a second `publish.bat` run hashed the old manifest into the new one. It's now filtered by name. Checked against a dummy `releases\` folder holding two versions' nupkgs and an old manifest: only the current version's 5 files are hashed.
- [x] Tests: `PathListTests` (10 cases: append, no-op when present literally, by variable, by case, padded or with a trailing slash, remove-only-ours keeping the rest verbatim, a prefix isn't a match).

**Verify.** Done 2026-09-30:
- `dotnet build perch.slnx` is clean, and the .NET suite passes 1841 with 1 skipped.
- `tools/test-install.ps1` passes 33 with 0 failed, now 14 pipeline files instead of 8. The download checks skip without a local `releases\`.

**Dogfood owed:** after an install and an uninstall, `reg query HKCU\Environment /v Path` is still `REG_EXPAND_SZ`, with its `%VAR%` entries intact. The registry IO itself has no automated test; the test project is Core-only.

**Landed:** commit `054ef00`.

---

## Performance

<a id="cp20"></a>
### CP20 — Session scan off the UI thread + incremental transcripts · 🟠 P1 · L · ✅

**Problem.**
- **Scan runs on the UI thread.** Every trigger (watcher, the 2s controlled poll, reconcile) calls `SessionMonitor.Scan()` on the dispatcher (`SessionMonitorHost.cs:81-88`).
- **Active transcripts are re-read in full.** Per session, Scan re-runs five or six full-file passes over an actively growing transcript: `ScanContext`, `ParseArtifacts`, `ParseTasks`, `ParseOutstandingAsyncAgent`, `ParseTitle` and the legacy `SubAgentReader.Parse`. The length+mtime cache misses on every write. `ParseTitle` reads the **whole file** when there's no `/rename`, although the comment says 32KB.
- **The hook triggers it on every tool call** by writing `.mode`. That means a full re-read on the UI thread per tool call: roughly 100-300ms at 10MB, and seconds at 100MB.
- **Triggers aren't coalesced.** Git-stats updates and process exits skip the debounce.
- **Path fallback is expensive.** `TranscriptLocator` falls back to scanning every project dir, about 10 times per session per scan, with no negative cache.
- **Sub-agent work repeats.** `SubAgentReader` re-classifies whole agent transcripts, and stats every agent file on every scan.

**Tasks**
- [x] **Scan off the UI thread.** `SessionMonitorHost` runs `SessionMonitor.Scan` on a single-flight worker, reusing CP4's `CoalescingTrigger` with a zero gap: one scan at a time, and a burst collapses into one trailing scan.
  - `SessionsChanged` and every alert event `Scan` raises are posted to the UI thread in the order they were raised, so a scan's alerts still land before the overlay update that follows them.
  - The deadline and controlled-poll values are captured on the worker and passed along with the post.
  - UI requests that mutate monitor state (`Acknowledge`, `ToggleExternalNotify`, `SetProjectNote`) are queued and run on the worker just before its next scan, so the monitor's dictionaries only ever see one thread.
  - Timers, file events and user actions all just request a scan.
  - A scan over 250 ms is logged to `logs/scan.log` with the session count.
  
  The services the scan calls were already safe off-thread: git stats and PR status use concurrent caches, the IDE detector locks its snapshot, and the replay projector's probe uses `Interlocked`.
- [x] **Incremental fold.** New `TranscriptFold`: per path, the consumed offset plus one state per registered `LineFolder`. On growth it reads `[offset, EOF)` once and feeds every folder.
  - Only whole lines are consumed. A trailing fragment that is already a complete JSON object is taken early, since a strict prefix of an object can never parse as one.
  - It resets on truncation, or when the first 256 bytes change (replacement or rotation).
  - An unchanged length and mtime costs a stat.
  - A leading BOM is stripped, as `StreamReader` does.
  - A throwing folder can't starve the others.
  
  `TranscriptReader`'s five whole-file readers (async agents, tasks, artifacts, context, title) share one fold. `MarkdownFilesReader` and `SubAgentReader` (`Classify` and the legacy parse) fold too. The static one-shot readers (`ReadContextUsage`, `ReadTitle`) reuse the same step functions through `LineFolder.FoldAll`, so the two paths can't disagree. The tail readers (activity, bare command, interrupted, awaiting assistant, burn rate, API error, stuck) stay on their 32KB tail window. Written fresh rather than porting roost's `TranscriptTailReader`, which lives on an unmerged branch.
- [x] **Title — changed from the plan.** The live scan path now folds "the latest `custom-title` anywhere in the file", with no head or tail heuristic, which is also correct for a mid-session rename that a 32KB head window would miss. The one-shot static `ReadTitle`, read once when a session window opens, keeps its tail-then-whole-file fallback for the same correctness reason. Its doc, which wrongly claimed a 32KB head, was corrected instead.
- [x] **Locator — changed from the plan.** The path isn't cached per scan; the direct `<projects>/<enc-cwd>/<id>.jsonl` check is one stat and must stay live so a new session's transcript is found at once. What was expensive is the fallback scan of every project folder, which a session with no transcript yet triggered from every reader on every scan. A fallback **miss** is now remembered for 30s, and forgotten early when any `projects/` root's mtime changes. Watching only the root, not every project folder, keeps the check cheap.
- [x] **`SubAgentReader` — changed from the plan.** Classifying from the tail became unnecessary once `Classify` folds incrementally. It still skips ordinary (non-teammate) agent files that have been quiet past the stale window, before parsing them: such an agent can never be surfaced, so this is exactly equivalent, and it spares a long session's dozens of finished agent files a first-time parse.
- [x] Every trigger goes through `RequestScanDebounced`: git-stats and PR refreshes, process exits, watcher errors, and the PR/Jira toggles, which used to fire `ChangeDetected` directly.
- [x] **Measured** (opt-in `TranscriptFoldBenchmark`, `PERCH_BENCH=1`): one append to a **50 MB** transcript cost the whole-file readers **715 ms** per session before (a fresh read, which is what the old length+mtime cache did on every append), and costs **2.8 ms** after. The "before" is conservative: a fresh fold still shares one read across `TranscriptReader`'s five values, whereas the old code did seven separate full reads. The first read of a 50 MB transcript is about 1.6 s, once, now on the worker. The 20-session live measurement is replaced by `logs/scan.log`, which records any scan over 250 ms.
- [x] Tests (26 new):
  - `TranscriptFoldTests` (11): arbitrary chunk splits, mid-line and mid-UTF-8-character; an unchanged file reads nothing; an append to about 1 MB reads only the append plus the head check; a record without its newline is taken once; a partial record waits; truncation; replacement by a no-shorter file; BOM; a missing then created file; a throwing folder.
  - `TranscriptFoldEquivalenceTests` (7): a combined real transcript grown in random chunks with 3 seeds; after **every** chunk, the readers that watched it grow equal fresh readers for every folded value. Also the task fixtures grown alone, the async launch-then-notify transition, truncation and replacement, and an append to a 5 MB transcript that reads only the append.
  - `TranscriptLocatorMissCacheTests` (3), a `SubAgentReader` skip test (a quiet ordinary agent is never parsed, a quiet teammate still is), and a `CoalescingTrigger` test: 400 requests from 8 threads never overlap, the burst collapses, and the last request is served.
- [x] Dogfood: *(User-confirmed on 2026-09-30.)*
  - the overlay still fills at launch and tracks sessions;
  - a finished session's "done" badge and toast still arrive on time;
  - acknowledging (focus or click) clears it;
  - the notify toggle and project note still refresh the row;
  - a busy session with a large transcript no longer makes the overlay or other windows stutter.

**Also closes CP25's first item:** the watcher map is now guarded by a lock in `EnsureWatchers`, `OnWatcherError` and `Dispose`, and the tracked-process map by another in `SyncProcessSubscriptions` and `Dispose`.

**Verify.** Done 2026-09-29: `dotnet build perch.slnx` is clean, and the .NET suite passes 1582 with 1 skipped. The new `CoalescingTrigger` test was repeated 10 times, all clean.

**Landed:** commits `ceecfc9` (the fold, readers, locator and sub-agent skip) and `e6fdbe8` (the background scan worker, trigger debounce and locks).

<a id="cp21"></a>
### CP21 — All-time stats: cache history, don't re-parse it · 🟠 P1 · M · ✅

**Problem.** `App.axaml.cs:1166-1188` → `SessionStatsService.ReportAllTime` (`:252-395`) and `TeamReader.cs:86` JSON-parse every line of every transcript whenever a session finishes, throttled to once per 3 minutes. With GBs of history, that is tens of seconds of a pegged core and heavy LOH churn in an all-day process. The Stats and Achievements windows pay the same cost.

**Tasks**
- [x] **Cache per file, per day, read incrementally — using CP20's `TranscriptFold`.** The fold already does "offset + (length, mtime) + head check, read only what was appended", so a new `SessionStatsCache` holds two folds:
  - one over session transcripts: per-day `SessionDayData`, plus the file's project and branch;
  - one over teammate transcripts: per-day tokens, plus the file position of each day's first record, which is what lets a day-filtered report find a teammate's first in-range day without re-reading.
  
  The parsing moved into fold steps (`StepSession`, `StepTeam`), which the one-shot `ParseSession` / `ParseContributions` now share, so the cache and the golden parsers can't disagree.
  - **Kept exact rather than approximate.** Records carry raw timestamps (sorted lazily, via a flag set only by an out-of-order append), so active time and the hourly histogram still follow the user's idle-threshold setting. Whoops is derived from `PromptsByParent`, so an append that adds a sibling to an earlier prompt still counts.
  - **All three reports share the cache.** Every caller's bounds are day-aligned, so day, range and all-time reports filter by local day. The range reports keep their mtime prefilter.
  - **Teammate meta files are memoised** by (length, mtime).
  - **The all-time report prunes** state for transcripts that no longer exist.
  - **Serialised.** Every report runs inside `SessionStatsCache.Use` under one lock, so the Stats window, the Achievements window and the tray check can overlap safely.
  - **Behaviour change (an improvement):** a line with an unexpected shape used to abort the rest of that transcript's parse. Now only that line is skipped, in both the fold and the one-shot parsers.
- [x] **Persisted** to `stats-cache.bin` beside `settings.json`, through a new streaming `AtomicFile.Write(path, Action<Stream>)`.
  - `TranscriptFold` gained `Checkpoint` `Export`/`Import`/`Forget` and a `Generation` counter.
  - The format is versioned binary, with timestamps as 7-bit tick deltas.
  - The snapshot is discarded, never trusted, on a mismatch in its header (format version, `SessionFoldVersion`, `TeamFoldVersion`, local time zone), on trailing bytes, or on any read error. The next report then rebuilds from the transcripts.
  - Saves happen at once after a first build, then at most every 10 minutes when something changed.
  - It honours `AppSettings.PersistenceDisabled` (newly exposed), so tests and `perch render` never touch the real file.
  - **Bump `SessionFoldVersion`/`TeamFoldVersion` whenever the step rules change** (including `SwearFilter`'s list).
- [x] **Achievements — changed from the plan.** There is no separate incremental achievements path: `AchievementCatalog.Evaluate`/`Sync` is a cheap pass over a `RangeReport`. The cost was always the report, and the report now comes from the cache.
- [x] xUnit (12 new, in `SessionStatsCacheTests`):
  - the cache agrees with the one-shot parsers on the fixtures;
  - a history grown from empty in random chunks (mid-line and mid-character splits, sessions and teammates together, 2 seeds) gives a cached report equal to a fresh one after **every** step;
  - changing the idle threshold is honoured from the cache;
  - out-of-order appends;
  - an unchanged report reads 0 bytes, and an append to a 2 MB transcript reads only the append plus the 256-byte head check;
  - the snapshot round-trips, and a restarted cache reads 0 transcript bytes, then only an append;
  - a file replaced while Perch was closed resets;
  - a damaged snapshot (garbage, truncated, empty) is discarded and rebuilt;
  - a deleted session and its teammates are pruned.
- [x] **Measured** (opt-in `SessionStatsCacheBenchmark`, `PERCH_BENCH=1`) on a real history of 553 session transcripts and 1.19 GB of `.jsonl`:
  - **cold**, the full parse every report used to cost: **5.6 s**;
  - **warm**, nothing changed: **79 ms**;
  - **restart**, load the snapshot and report: **110 ms**, reading 0 transcript bytes;
  - snapshot size: **1.3 MB**.
- [x] Dogfood: *(User-confirmed on 2026-09-30.)*
  - the Stats window (Today, 7 days, 30 days, All time) and the Achievements window show the same figures as before;
  - `stats-cache.bin` appears in the profile dir after the first report;
  - a later launch's first all-time report is quick.

**Verify.** Done 2026-09-29. `dotnet build perch.slnx` is clean, and the .NET suite passes 1623 with 1 skipped.

**Landed:** commit `cc9a436`.

<a id="cp22"></a>
### CP22 — Streaming chat O(n²) + SessionThreadView leak · 🟠 P1 · M · ✅

**Problem.**
- **Settled prefix is rebuilt each time.** `SessionThreadView.cs:871-909` (`RenderRevealed`) rebuilds the whole settled prefix (Markdig parse, control tree, `File.Exists` calls) at every block boundary.
- **Open blocks are rebuilt every frame.** An open fenced code block or table is re-parsed and re-highlighted every 40ms.
- **Text concatenation is quadratic.** `SessionConversation.cs:210` (`Text += delta`) copies the whole reply per delta.
- **Closing a window leaks its chat view.** `SessionWindow.Detach` (`SessionWindow.cs:837-847`) never unbinds `_thread`, and the session outlives the window by design. Every close and reopen leaves a whole chat tree subscribed and processing deltas.

**Tasks**
- [x] **Settled blocks are append-only.** When a block boundary appears in the tail, `StreamingProse` builds only the newly settled slice and inserts it just above the tail. Slices already on screen are never rebuilt. The prose is tracked as offsets into the part's text (`_settledLen`, `_shown`), so a frame allocates only its tail, not the revealed prefix. The finalise step is unchanged: one clean whole-message build, which also corrects anything a slice boundary got wrong (e.g. a reference link defined in a later slice).
- [x] **Tail throttle.** The policy is in a new UI-free `Perch.Data.Control.StreamingTail`:
  - **Open code fence → plain text.** `TryOpenFence` follows CommonMark fence rules (≤3 spaces of indent, ≥3 backticks or tildes, no backtick in a backtick fence's info string, a closer at least as long with nothing after it). While the fence is open, the tail is a plain code panel (`MarkdownView.BuildStreamingCode`, the same chrome as a finished block) whose text is updated in place: no parse, no highlighting, no new controls. It gets its real build and syntax colours once the fence closes. While it's open the settle scan is skipped entirely, since no block can start until it closes.
  - **Large tail → slower cadence.** A rich tail over 4 KB, or a plain-fence tail over 16 KB, re-renders at most every 250 ms (`MinInterval`). It still re-renders immediately when its text just moved into a settled slice, or when it switches between plain and rich. A throttled frame marks the tail stale, and a caught-up tick settles it, so the last text always lands.
  - **No per-frame `File.Exists`.** The tail renders without file-ref probing; settled slices and the final build still arm file references.
- [x] **`TextPart` uses a `StringBuilder`.** `Append` adds the delta and `Text` materialises and caches the string only when read. `StreamingProse` no longer receives the text per delta: `Poke()` just restarts pacing, and the control reads `part.Text` once per 40 ms frame. **Changed is batched in the view rather than in the conversation**, which keeps `SessionConversation` deterministic for its tests. `Updated` notifications are collected into a set and applied in one pass, posted at the same priority as the queued deltas (FIFO, so it can't starve). An `Added` flushes the set first, so the column always matches applying every change in order. `SyncParts` is idempotent, which is what makes coalescing safe.
- [x] **`SessionThreadView.Unbind()`**, ported from roost and extended. It unsubscribes, and also drops the column and any pending sync; dropping the column detaches the controls, which stops any streaming timer. `Bind` goes through it. `SessionWindow.Detach` calls it, which covers both `OnClosed` and re-pointing via `Attach`. `HistoryWindow` owns its conversation, so it can't leak this way and is unchanged.
- [x] **Verify.** Both checks live in `perch render`: the streaming scene always runs, and the benchmark and leak check run with `PERCH_BENCH=1`.
  - **Mid-stream capture** (`session_streaming_1x.png`, cut in the middle of the trailing code fence). It's identical to the old render, apart from the still-open fence being unhighlighted, which is intended.
  - **Frame-time benchmark.** A 43 KB reply (40 headed steps, lists, tables and a 250-line fenced block) streamed in 24-char deltas, one paced frame per delta, with the layout pass timed. Same machine, before → after:
    - total: **154 s → 9.7 s**
    - mean per frame: **85 → 5.4 ms** (the frame budget is 40 ms)
    - p50: **33 → 2.1 ms**
    - p95: **175 → 19 ms**
    - p99: **1,240 → 25 ms**
    - max: **1,783 → 35 ms**
    - inside the open fence: mean **141 → 7.4 ms**
  - **Leak check.** Ten windows are opened and closed onto one live, still-streaming session, then GC runs. Before: **10/10** windows still reachable (checked by temporarily removing the `Unbind` call). After: **0/10**. Avalonia keeps the most recently closed window reachable until another window opens, whatever the fix; the check opens one untracked window to account for that.
  - xUnit `StreamingTextTests` (28): deltas accumulate to their exact concatenation over 2,000 random deltas; `Text` is cached until the next delta; the final text replaces the accumulation; an empty delta is a no-op; 23 fence cases (open, opener still arriving, closed, the wrong closer character, a shorter closer, an indented closer, a 4-space indented code block, inline code, a fence inside a list, CRLF); the `MinInterval` thresholds.
- [x] Dogfood: *(User-confirmed on 2026-09-30.)*
  - a long live reply streams smoothly;
  - a code block shows plain while it streams, then highlights when it closes;
  - the finished message matches a non-streamed render;
  - closing and reopening a live session's window mid-stream still shows the full thread;
  - find (Ctrl+F) and the jump buttons still work mid-stream.

**Verify.** Done 2026-09-29. `dotnet build perch.slnx` is clean, and the .NET suite passes 1610 with 1 skipped.

**Landed:** commit `84b8576`.

<a id="cp23"></a>
### CP23 — Overlay paint path: no IO, no per-frame allocations · 🟡 P2 · M · 🟦

**Problem.**
- **File IO while painting.** `OverlayCanvas.cs:2838` → `AccountMismatch` → `OrgProvider.GetLive` (`OrgProvider.cs:43-87`) does four file stats per rule-governed row per frame, and a full `.claude.json` parse inside `Render` whenever the mtime changes.
- **The mismatch pulse never stops.** The 60ms pulse (`:3186-3201`) keeps invalidating even while the overlay is hidden.
- **Allocation on every frame.** `OverlayDraw.cs:17-18,38-40,105-123` creates a new `Typeface` and `FormattedText` per text call, about 25-30 shapings per row per frame.
- **Truncation is expensive and unsafe.** `Truncate` re-shapes on every binary-search probe, and can split surrogates and ZWJ sequences.
- **Brushes are recreated.** New `SolidColorBrush` objects are created in the row loop.
- **UsageMonitor blocks the UI thread first.** `UsageMonitor.FetchAsync` does synchronous file IO before its first await (`UsageMonitor.cs:86,93`).

**Found first (a new `PERCH_BENCH=1` overlay benchmark, measured on the full sample overlay with the attention chase and a mismatch row):** the per-frame allocation wasn't text. It was **mutable `Pen`s**. A `Pen` is a full AvaloniaObject (property store, change events) of about a kilobyte, and the chase built one per perimeter sample per glow pass, thousands a frame. The review's list missed this, and it was about 95% of the 5 MB/frame. An in-process GC allocation-tick sampler named the types.

**Tasks**
- [x] The account mismatch is computed off the paint path and cached per session (`RecomputeMismatches`): when the sessions, the rules or the provider change, and on the 1s tick and pulse ticks, so a `/login` still shows promptly. The paint path's `AccountMismatch` is now a dictionary read. `OrgProvider` trusts a cached answer for **2s** before looking at the file stamps again (the test seam takes a clock). `ClaudeJsonReader` now streams the top level with `Utf8JsonReader` and materialises only `oauthAccount`, skipping everything else (the project history).
  - **Changed from the plan:** it runs on the UI thread, not a pool thread. What's left is at most two file stats per config dir every 2s, plus a streaming parse of a file of about 190 KB only after it changes. Keeping it synchronous also keeps the render harness and preview deterministic, since they set the rules right after `Update()`.
- [x] The pulse stops once the overlay can't be seen (not effectively visible, window hidden or minimised). Before, no frame cleared `_anyMismatchThisFrame`, so it invalidated forever. The next paint restarts it.
  - **Not done:** invalidating only the outline rect. Avalonia re-renders a control whole, so there's no sub-rect invalidation of a single `Render` short of splitting the outline into its own visual. The caches make the whole repaint cheap instead.
- [x] `OverlayDraw`:
  - typefaces cached per weight;
  - `Text`/`Emoji` served from a 1024-entry LRU keyed by (text, size, weight, **colour + opacity**, emoji face), each entry painting with its own immutable brush. Keying by brush *reference* would never hit (brushes built per frame), and would hand back a stale colour after a theme swap mutates the palette's cached brushes. They're shared, so they must not be mutated: the one caller that did (`UsageBarRenderer`) uses the new `NewText`;
  - `Brush(color)`, a cached immutable brush. 52 per-paint `new SolidColorBrush` in `OverlayCanvas*.cs` now use it;
  - `Pen(...)` (same signature as `new Pen`) returns an `ImmutablePen`. It replaces 26 per-paint `new Pen`;
  - `SolidPen(color, width)`, a cached immutable pen for the chase comet.

  Fields keep `new Pen`/`new SolidColorBrush`, and the CLAUDE.md convention records this.
- [x] Truncation cuts only between graphemes (`Perch.Data.TextFit`, `StringInfo.GetNextTextElementLength`) and is memoised (a 512-entry LRU keyed by text, size, width and weight). That gives the "once, not per frame" effect without moving the width-dependent layout into `Update`.
- [x] `UsageMonitor.FetchAsync` reads the token and CLI version inside `Task.Run`, so none of that IO (or the macOS keychain call) runs on the UI thread before the first await.
- [x] Tests (18 new cases):
  - `TextFitTests`: plain text, surrogate pairs, a ZWJ family emoji, a combining mark, and a logarithmic probe count;
  - `LruCacheTests`: hits, LRU eviction, and bounded under concurrency;
  - `OrgProviderTests`: the recheck window (no stat inside it, a stat and re-read after it, and `Invalidate` still forcing a read);
  - `ClaudeJsonReaderTests`: the account found behind 2,000 nested projects containing decoy `oauthAccount` keys, after a BOM, and null, junk, numeric and blank values read as before.

  The BOM case caught a real bug in the first cut: a `"\xEF…"u8` literal isn't the BOM bytes, and the old `StreamReader` path had stripped the BOM silently.
- [x] Verify:
  - **Visual diff:** 154 of 167 renders are pixel-identical to the baseline. The 13 that differ are run-to-run noise. Twelve of them differ just as much between two renders of the *same* new code (animations, clocks, random games). The thirteenth, `reaction_bubbles_1x`, shows the same glyphs at a slightly different float-animation phase. The mismatch outline's pulse alpha also moves with the wall clock.
  - **Benchmark:** the same harness, old paint code vs new:

    | | before | after |
    |---|---|---|
    | full frame (raster included), mean | 15.6 ms | 11.6 ms |
    | overlay draw calls only | 4.00 ms | 1.67 ms |
    | allocated per frame | 5,057 KB | 270 KB |
    | org lookups per frame | 1.0 | 0 |

    The remaining ~26 KB/frame of text layout is text whose content or alpha really changes each frame: live elapsed labels, and a fading pill.
- [ ] Dogfood: the overlay with the attention chase, an account-mismatch row (it should appear and clear within about 2s of a `/login`), and a theme swap (text colours must follow). Hide the overlay while a mismatch pulses and check that the pulse stops (no CPU).

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` clean; the .NET suite passes 1736 with 1 skipped.

**Landed:** commit `63db67d`: `OverlayDraw`, `OverlayCanvas*`, `UsageBarRenderer`, `HeadlessRenderer` (the benchmark), `OrgProvider`, `ClaudeJsonReader`, `UsageMonitor`, and the new `LruCache`/`TextFit`.

<a id="cp24"></a>
### CP24 — Misc perf batch · 🟡 P2 · M · 🟦

- [x] **Metrics.** `MetricsMonitor` reads each process through a new `ISystemMetrics.ReadProcess(pid)` seam instead of `Process.GetProcessById(pid).WorkingSet64`, which on Windows snapshots every process on the machine per call.
  - Windows (`WindowsSystemMetrics`) opens one `PROCESS_QUERY_LIMITED_INFORMATION` handle and reads `GetProcessTimes` + `K32GetProcessMemoryInfo`, with no snapshot.
  - The default interface method (used on macOS, where `Process` is already per-pid) keeps the old `Process` path.
  - A pid shared by two sessions' trees is read once per tick.
  - **Changed from the plan:** there's no extra working-set snapshot. The parent map is already one Toolhelp snapshot per tick, and a per-pid handle read is cheaper than a second whole-machine snapshot.
- [x] **History tail.** `HistoryWindow` tails by byte offset through a new `TranscriptLineTail`, a thin wrapper over CP20's `TranscriptFold` (one collecting folder), so it shares the fold's rules and tests: partial lines wait, the head check spots a replacement, and a BOM is stripped. This replaces porting roost's `TranscriptTailReader`, which is still unmerged. A truncated or replaced file reports `Reset` and rebuilds.
  - The whole-transcript decode (JSON parse, `StreamJsonParser`, base64 image recovery) now runs off the UI thread. The initial conversation is built on the worker, since it isn't bound to a view yet. Tail lines are decoded on the worker by the new `SessionConversation.ParseTranscriptLine` and applied on the UI thread by `AppendParsedTranscriptLine`. `AppendTranscriptLine` is now those two halves, so the paths can't disagree.
  - A change that lands while a tail read is in flight is no longer dropped (`_tailAgain`).
- [x] **DiffView.** Past 10,000 lines or 100 files across the whole diff, files start collapsed. A collapsed file builds **no rows** until it's expanded; the first expand rebuilds, so its rows join the selection streams and find order in document order.
  - The budget counts every file's lines in file order, whatever the user has expanded, so expanding one file never flips the files after it.
  - The user's toggles are remembered per file key across rebuilds. A one-line note says when later files start collapsed.
  - Lines over 2,000 characters are cut for display, with a "… [N more characters]" suffix and never splitting a surrogate pair. Copy still takes the whole line.
  - **Not done:** virtualisation. Lazy files bound the worst case (a 4,000-line file cap × whatever the user expands), which covers the review's 800k-row commit.
- [x] **Arcade.** A new `ArcadeLoopGate` pauses each of the six windows' loop on deactivate or minimise and resumes it on activate. The games count time in ticks, so a pause just freezes play.
  - Wordle's timer runs only while something moves (a shake, a toast, the end-of-game shimmer); typing repaints directly.
  - Connect 4 skips the drop animation for a move that arrives while paused, rather than leaving the disc hanging at the top.
  - **Changed from the plan:** the menu animates continuously by design (the shimmering prompt, the bobbing sprite), so the gate is what stops it.
- [x] **Images.** A new `ImageHeader` (Core) reads PNG/JPEG/GIF/WebP/BMP dimensions from the header. A JPEG's frame header is found by walking its segments, past any size of EXIF.
  - `BoundedBitmap` (App) refuses anything over 64 MP (a decode would be over 256 MB, however small the file) and decodes previews at up to 1,120 px wide (2× their 560 DIP box) via `DecodeToWidth`.
  - Every decode is off the UI thread: the viewer (still full resolution, with a "can't preview" caption past the cap), the chip thumbnail (glyph until it lands), the chip hover preview and the `[Image #N]` hover preview. Stale results are dropped.
  - Attachments are capped at **5 MB** (the API's per-image limit). The composer refuses a bigger one with a note saying why, and `BuildImageContents` checks again before reading and base64-encoding.
- [x] **Git tree watcher.** The debounce is now a pool timer that the watcher thread re-arms, so a burst costs one UI post when it settles, not one per event. `Error` (buffer overflow) now triggers a refresh.
  - A new `RepoWatchFilter` (Core) ignores `node_modules` and `.git` internals other than `HEAD`, `index`, `packed-refs` and `refs/`.
  - **Changed from the plan:** `bin/` and `obj/` are **not** filtered. Plenty of repos track a `bin/` of scripts, and once events are coalesced a build costs one refresh.
- [x] **CodeHighlight.** Each language profile is built once per tag and cached in a `ConcurrentDictionary`. The sets are never mutated after construction. Unknown tags aren't cached, so arbitrary fence tags can't grow the map.
- [x] **Resume history.** `PerchSession.LoadHistoryAsync` keeps the last 4,000 lines in a ring buffer while it reads (`TranscriptScan.LastLines`).
  - **Not done:** `Conversation.LoadHistory` still decodes those 4,000 lines on the UI thread. Out of scope here; the `ParseTranscriptLine` split makes it a small follow-up.
- [x] Tests (38 new cases):
  - `MetricsMonitorSamplingTests`: a tree is summed, and each pid is read once per tick even when a session is listed twice;
  - `TranscriptLineTailTests` (5): the first read then only appends, a partial line waits, an append to about 200 KB reads only the append plus the head check, truncation and replacement reset, and a missing file;
  - `SessionConversationTests`: parsing on another thread then applying equals `AppendTranscriptLine`, including image recovery;
  - `ImageHeaderTests` (8): every format, including a JPEG frame behind a 40 KB APP1 and a DHT, and truncated, zero-sized and unknown inputs;
  - `RepoWatchFilterTests` (19 cases);
  - `TranscriptScanLastLinesTests` (3);
  - `CodeHighlightTests`: shared profiles under `Parallel.For`, and derived profiles (TS, C++) don't leak keywords into their base.
- [x] **Verify.** Done 2026-09-30: `dotnet build perch.slnx` is clean, and the .NET suite passes 1821 with 1 skipped.
  - `perch render` against the latest baseline: 155 of 167 renders are identical. The 12 that differ are the known run-to-run set (the random games, clocks, the streaming timer, reaction bubbles). Every touched surface (git tree, history, Wordle, the arcade menu, Connect 4, Draw) is byte-identical.
  - The Windows `ReadProcess` interop has no automated coverage, since the test project is Core-only.
- [ ] Dogfood:
  - per-session CPU/RAM figures look as before (Windows `ReadProcess`);
  - the history viewer tails an active session, and a `/clear`-rotated or truncated transcript rebuilds;
  - a huge commit in the git tree opens quickly with later files collapsed, and expanding one works (selection, find, staging);
  - the arcade games pause when you click away and resume on return;
  - image thumbnails, hover previews and the viewer still show;
  - a > 5 MB image is refused with a note;
  - `npm install` in a watched repo doesn't make the git tree churn.

**Landed:** commit `d683e87`.

---

## Correctness

<a id="cp25"></a>
### CP25 — Watcher race, PID reuse, Process disposal · ⚪ P3 · S · 🟦

- [x] `SessionMonitor.cs:1264-1277`: `OnWatcherError` mutates `_watchers` on a thread-pool thread while the UI thread uses it. Post the teardown to the owning thread, or lock. After CP20 this becomes "the scan worker's thread". *(Done in CP20: `_watchGate` guards every access.)*
- [x] `IProcessProbe.cs:23-33`: liveness should compare the process start time against the session file's `startedAt`, as `SessionTerminator` already does, so a reused PID doesn't keep a dead session alive. Dispose `Process` objects.
  - New `IProcessProbe.IsAlive(pid, startedAt)`. A default interface method ignores the start time, so replay's `Projector` and the test fakes are unchanged.
  - `SystemProcessProbe` implements it and now disposes its `Process`. `SessionMonitor.ReadSession` passes the session file's `startedAt`, read leniently.
  - The shared tolerance is `Perch.Data.ProcessIdentity`, which `SessionTerminator` now uses too.
  - **Changed from the plan: the check is one-sided.** A pid counts as recycled only when the live process started more than 2 min *after* the recorded start. A process older than the record holds the pid, so it must be the one that wrote the record. That stays correct even if Claude Code ever refreshed `startedAt` mid-session, which a two-sided check would turn into hidden live sessions. Measured on 4 live sessions, including ones hours old: `startedAt` trails the process start by 1-3 s.
  - An unreadable start time keeps the old pid-only answer. The kill check in `SessionTerminator` stays two-sided, since refusing a kill is the safe failure.
  - Also fixed: `SessionMonitor.SyncProcessSubscriptions` leaked the `Process` when `EnableRaisingEvents` threw (access denied).
- [x] `DaemonRosterReader`: one worker with a mistyped field shouldn't drop the whole roster. Each worker is parsed on its own (`ReadWorker`, inside its own try), through the tolerant `TranscriptJson` readers. A mistyped optional field reads as absent; only an unusable `pid` or `sessionId` skips the worker. The roster's `startedAt` feeds the same recycled-pid check. New `Parse(json, probe)` seam for tests.
- [x] Tests (10 new):
  - `ProcessIdentityTests`: the one-sided rule; the real probe against the test process's own pid and start time; a real `SessionMonitor.Scan` that drops a live-pid session file recorded an hour before that process started, and keeps it with the right start;
  - `DaemonRosterReaderTests`: mistyped `pid`/`sessionId`/`cwd`/`startedAt`/`source`/`name` skip only their worker or field, a pid written as a double reads, and the recorded start reaches the probe.

**Verify.** Done 2026-09-30: `dotnet build perch.slnx` is clean, and the .NET suite passes 1831 with 1 skipped. No dogfood beyond the overlay still listing live sessions and daemon workers as before.

**Landed:** commit `477a682`.

---

## roost branch

<a id="cp26"></a>
### CP26 — Roost-branch findings · 🟡 P2 · M · ⬜

These live on `roost` (not merged). Fix them there before it merges; they are tracked here so nothing gets lost. Line numbers are as on `roost`.

- [ ] 🟠 **Enter/Esc can approve or deny a pane you can't see** (`RoostWindow.cs:626,641`). They act on the *focused* Perch pane's pending permission even after paging or filtering has taken it off screen. Require `_placed.ContainsKey(_focused)` and an expanded pane, or clear `_focused` when the pane leaves view.
- [ ] 🟠 **New-session panes never recover** (`RoostFeed.cs:79`). A null transcript path (a new session with no `.jsonl` yet) is never retried, so the pane stays on "No activity yet" forever. Tail the direct encoded path, or re-resolve on each roster scan.
- [ ] 🟡 **Closed panes are remembered by bare PID** (`RoostRoster.cs:105,149`). They are persisted that way and pruned only in memory, so after a restart a recycled PID pre-hides an unrelated session. Key by pid + sessionId, and persist the pruned set.
- [ ] 🟡 **Tailing depends entirely on the watcher** (`TranscriptTailHost.cs:48,55`). `Poke()` is never called, `Error` is unhandled, and a missing directory means it never watches. Call `Poke()` from `UpdateRoost`, and re-arm on `Error`.
- [ ] 🟡 **"Load earlier" freezes the UI** (`RoostFeed.cs:95,113`). It reads the whole transcript and folds it in a single UI post. Reuse HistoryWindow's size gate, and fold in batches at Background priority.
- [ ] ⚪ **Mini cards re-parse on every rebuild** (`ActivitySummary.cs:123` / `SessionPane.cs:486-507`). They re-parse tool `InputJson`. Cache per item version.
- [ ] ⚪ **`TranscriptTailReader.Read` can throw** (`TranscriptTailReader.cs:77,101,104`). It throws when the file shrinks between length and read (`n == 0`) and on files over 2GB. Guard, read in bounded chunks, and catch everything.
- [ ] ⚪ **Take-over uses a stale snapshot** (`App.axaml.cs:833`, `OnElevateToPerch`). Re-read the pane after the confirm, and abort unless `Terminate` returns Terminated or AlreadyGone.
- [ ] ⚪ **Suppressed toasts are lost.** A toast suppressed as "seen in Roost" is dropped for good. Queue it and replay on Deactivated, or when the pane leaves view.
- [ ] ⚪ **Rail rows rebuild every second** (`RoostWindow.cs:229`), and pointer presses trigger a full `Refresh`. Update rows in place, and skip when focus is unchanged.
- [ ] ⚪ **`RoostLayout` isn't validated.** Normalise it with `Enum.IsDefined`.
