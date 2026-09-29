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
| [CP1](#cp1) | 🔴 P0 | Supabase | Revoke EXECUTE on internal SECURITY DEFINER functions | S | 🟦 code + tests done, prod deploy owed |
| [CP2](#cp2) | 🔴 P0 | Supabase | Friendship consent: no self-accepting | S | 🟦 code + tests done, prod deploy owed |
| [CP3](#cp3) | 🟠 P1 | Supabase | Server-owned fields: no forged games, no backdated rows | M | ⬜ |
| [CP4](#cp4) | 🟠 P1 | Supabase | Realtime inbox authorisation + sender validation | M | ⬜ |
| [CP5](#cp5) | 🟡 P2 | Supabase | Draw with Perch: RPC state checks + size limits | S | ⬜ |
| [CP6](#cp6) | 🟡 P2 | Supabase | Block/suspension coverage, `find_profile` throttle, feed query | M | ⬜ |
| [CP7](#cp7) | 🔴 P0 | Client | Executable hijack via untrusted working directory | M | ⬜ |
| [CP8](#cp8) | 🟠 P1 | Client | Link opening: scheme allowlist + browser argument injection | S | ⬜ |
| [CP9](#cp9) | 🟠 P1 | Client | No UNC/remote path probing (NTLM leak + UI hang) | S | ⬜ |
| [CP10](#cp10) | 🟠 P1 | Client | Named pipes: current-user only, park the valet hook | S | ⬜ |
| [CP11](#cp11) | 🟡 P2 | Client | Hardened shared `GitRunner` | M | ⬜ |
| [CP12](#cp12) | 🟡 P2 | Client | cmd-shim metacharacters (VS Code / GitKraken launch) | S | ⬜ |
| [CP13](#cp13) | 🟡 P2 | Client | Control-pipe intent validation + launcher quoting | S | ⬜ |
| [CP14](#cp14) | 🟠 P1 | Data safety | Never wipe `.claude.json`; atomic writes everywhere | M | ⬜ |
| [CP15](#cp15) | 🟡 P2 | Privacy | Recording-export redaction gaps | S | ⬜ |
| [CP16](#cp16) | ⚪ P3 | Client | Small security hardening batch | M | ⬜ |
| [CP17](#cp17) | 🟡 P2 | Supply chain | CI permissions, pinning, deploy-secret scoping | S | ⬜ |
| [CP18](#cp18) | 🟡 P2 | Supply chain | Code signing + signature verification in `install.ps1` | L | ⬜ |
| [CP19](#cp19) | ⚪ P3 | Build | Build/installer hygiene (em dashes, PATH type, versioning) | S | ⬜ |
| [CP20](#cp20) | 🟠 P1 | Performance | Session scan off the UI thread + incremental transcripts | L | ⬜ |
| [CP21](#cp21) | 🟠 P1 | Performance | All-time stats: cache history, don't re-parse it | M | ⬜ |
| [CP22](#cp22) | 🟠 P1 | Performance | Streaming chat O(n²) + SessionThreadView leak | M | ⬜ |
| [CP23](#cp23) | 🟡 P2 | Performance | Overlay paint path: no IO, no per-frame allocations | M | ⬜ |
| [CP24](#cp24) | 🟡 P2 | Performance | Misc perf batch (metrics, history tail, diff, arcade, images, watcher) | M | ⬜ |
| [CP25](#cp25) | ⚪ P3 | Correctness | Watcher race, PID reuse, Process disposal | S | ⬜ |
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
### CP1 — Revoke EXECUTE on internal SECURITY DEFINER functions · 🔴 P0 · S · 🟦

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
- [ ] **Deploy to prod** (`db-migrate.yml`), then re-run the curl probes below against the live project.

**Verify.** Done locally on 2026-09-29. `supabase test db` gave 84/84 across all four files. Local REST probes with the anon key gave `rpc/cleanup_old_games` 401, `rpc/cleanup_old_draw_games` 401, `rpc/find_profile` 401, and `rpc/are_friends` / `rpc/is_blocked` 404. **Still owed:** the same probes against prod after deploy.

**Landed:** migration `20260929120000_security_hardening.sql` (commit: _pending_).

<a id="cp2"></a>
### CP2 — Friendship consent: no self-accepting · 🔴 P0 · S · 🟦

**Problem.** In `20260819120100_rls.sql:33-40`, `friendships_request` only checks `requester = auth.uid()`, and `friendships_respond` has USING but no WITH CHECK. On top of that, `20260819120200_grants.sql:10` grants full-column UPDATE. So `POST /rest/v1/friendships {"requester":"<me>","addressee":"<victim>","status":"accepted"}`, or a PATCH of your own pending row, makes you the victim's friend without their consent. That gives access to their posts and profile and lets you challenge them to games.

**Tasks**
- [x] Insert policy: `with check (requester = auth.uid() and status = 'pending')`. The planned `not is_blocked(...)` term was **dropped**: a blocked user's request would then fail visibly, which tells them they were blocked. The block-related profile leak is handled in CP6, by making `shares_edge` ignore blocked pairs so the request succeeds but shows nothing.
- [x] Update policy: `using (addressee = auth.uid() and status = 'pending') with check (addressee = auth.uid() and status = 'accepted')`. Only the addressee can update, and only pending → accepted.
- [x] Trigger `private.friendships_guard` (BEFORE UPDATE) makes `requester`, `addressee` and `created_at` immutable. Without it, the addressee could accept while re-pointing the requester at a third party.
- [x] **Approach changed from the plan:** the UPDATE grant is kept, and there's no `accept_friend` RPC. Already-shipped clients send friend requests as a PostgREST `merge-duplicates` upsert, which needs table UPDATE privilege. Revoking UPDATE would have broken friend requests for everyone until they updated. The policies plus the trigger close the hole without that.
- [x] Client: `SendRequestAsync` now uses `resolution=ignore-duplicates` (ON CONFLICT DO NOTHING) instead of `merge-duplicates`. `RespondAsync` (a PATCH by the addressee) is unchanged and still works.
- [ ] Existing forged edges in prod can't be detected: there's no `accepted_at` / `accepted_by` column. Decide whether to add an audit column going forward.
- [x] pgTAP (`tests/security_test.sql`, tests 12–18): insert as `accepted` or `blocked` → 42501; pending insert and ignore-duplicates re-send succeed; the requester's PATCH matches nothing; re-pointing the requester while accepting → 42501; the addressee's accept succeeds.
- [ ] **Deploy to prod** (`db-migrate.yml`).

**Verify.** Done locally on 2026-09-29. Beyond pgTAP, an end-to-end run over local PostgREST with real signed-in test users checked each case:
- forged accepted insert → 403;
- new-client request (ignore-duplicates) → 201, and re-send → 201 no-op;
- **old-client `merge-duplicates` request → 201**, so shipped apps keep working;
- requester self-PATCH → `[]`;
- addressee accept → 200 accepted;
- re-point while accepting → 403;
- profile visibility through `private.shares_edge` still works.

.NET suite 1377/1377.

**Landed:** same migration as CP1, plus `SupabaseSocialClient.SendRequestAsync` (commit: _pending_).

<a id="cp3"></a>
### CP3 — Server-owned fields: no forged games, no backdated rows · 🟠 P1 · M · ⬜

**Problem.**
- **Forged games.** `20260821120000_connect4.sql:60-66` still allows a direct `INSERT` into `games`, where the client chooses `status`, `winner`, `turn`, `move_count` and `updated_at`. That lets someone create forged losses that sort first forever, escape cleanup, and get fetched with no limit on every 60s poll.
- **Rate-limit bypass.** `20260918120000_posts_current_only.sql:48-69` trusts a client-supplied `posts.created_at` in the flood guard, so setting `created_at: "1970-01-01"` bypasses the limit entirely.

**Tasks**
- [ ] `drop policy games_create` and `revoke insert on games from authenticated`, so games are only created in `accept_game_request`.
- [ ] Add BEFORE INSERT triggers forcing `new.created_at := now()` on `posts`, `reactions` and `draw_requests`, and `updated_at := now()` on `games`. Alternatively, use column-level insert grants that exclude timestamps.
- [ ] Stamp `last_posted_at = now()` instead of `new.created_at`.
- [ ] Client: add a `limit` to `GetGamesAsync` and `GetDrawGamesAsync`.
- [ ] pgTAP: a direct games insert fails; a backdated post gets `created_at ≈ now()`; two posts inside 5s trip the limit.

<a id="cp4"></a>
### CP4 — Realtime inbox authorisation + sender validation · 🟠 P1 · M · ⬜

**Problem.** `perch:inbox:<uid>` is a **public** broadcast topic (`SupabaseRealtime.cs:192`, `SupabaseSocialClient.Games.cs:233-278`). Anyone holding the anon key can:
- subscribe to any user's inbox and see who invites or nudges them, along with game ids;
- post to it with a forged `from_handle`, producing a phishing-style bubble;
- flood it, where every message triggers a roughly 14-request `RefreshSoon` poll (`App.axaml.cs:2277-2297`).

**Tasks**
- [ ] Switch to private channels. Add a `realtime.messages` RLS policy: SELECT is allowed when the topic uid equals `auth.uid()`; INSERT is allowed only when the sender is an accepted, unblocked friend of the topic owner.
- [ ] Client: ignore the payload's `from_handle`, resolve the sender from the friend graph, and drop messages from non-friends or blocked users.
- [ ] Coalesce `RefreshSoon`: one poll in flight plus a trailing debounce of about 2s.
- [ ] Test: a subscription from a non-owner is refused, and a non-friend's broadcast is not delivered. The realtime part needs a manual check against a local stack.

<a id="cp5"></a>
### CP5 — Draw with Perch: RPC state checks + size limits · 🟡 P2 · S · ⬜

**Problem.**
- `give_up_draw_round` and `submit_draw_guess` (`20260924120000_draw_with_perch.sql:194-282`) don't check the round, phase or game state. Replaying an old round lets a player take the drawing turn and orphan the opponent's rounds.
- There are no length CHECKs on `strokes`, `word`, `letter_hint` or individual guesses, so multi-MB payloads fill storage and every client ends up parsing them.

**Tasks**
- [ ] In both RPCs, require `g.status='in_progress' and g.phase='guess' and g.whose_turn=me and r.round_no=g.round_no and r.status='guessing'`.
- [ ] Add CHECKs: `octet_length(strokes) <= 65536`, `word` ≤ 40, `letter_hint` ≤ 40, each guess ≤ 64 (checked in the RPC).
- [ ] Add a rate limit on `draw_requests` inserts.
- [ ] Client: check payload size before `JsonDocument.Parse` in `DrawStrokeCodec`.
- [ ] pgTAP: give-up on a stale round fails, and oversized strokes are rejected.

<a id="cp6"></a>
### CP6 — Block/suspension coverage, `find_profile` throttle, feed query · 🟡 P2 · M · ⬜

**Problem.**
- Blocks don't gate `shares_edge`, new friendship inserts, or the game RPCs (`submit_draw_round`, `submit_draw_guess`, `drop_disc`).
- Suspension only stops posts and `find_profile`.
- `find_profile` has no throttle, so anyone can enumerate handles and harvest ids.
- `GetFeedAsync` (`SupabaseSocialClient.cs:444`) has no author filter, so the RLS predicate runs for every post in the table. That is O(N²) across the platform.

**Tasks**
- [ ] Add `and not private.is_blocked(...)` to `private.shares_edge` and every game RPC. Leave the friendship insert alone: a visible failure there would tell a blocked user they were blocked (see CP2).
- [ ] Mark shared games abandoned when one player blocks the other.
- [ ] Apply `is_suspended` to reactions, game writes and profile edits.
- [ ] Add a per-caller rate limit to `find_profile` (a counter table), and stop returning the id until a request exists. (CP1 already stopped **anonymous** calls; signed-in enumeration remains.)
- [ ] Filter the feed with `author=in.(<accepted friend ids>)`, or add a join RPC. Reuse the friends and profiles `GetRosterAsync` already fetched.
- [ ] pgTAP: a blocked user can't create an edge, read the profile, or move in a shared game.

---

## Client security

<a id="cp7"></a>
### CP7 — Executable hijack via untrusted working directory · 🔴 P0 · M · ⬜

**Problem.** Bare executable names get resolved inside untrusted repos in four places:
- **`ClaudeSessionController.cs:67`** runs `cmd.exe /c "claude …"` with `WorkingDirectory = cwd`. cmd searches the current directory before PATH, so a committed `claude.cmd`, `.bat` or `.exe` runs instead of Claude, with no permission layer. The same shape is in `PluginManager.cs:133` and `SessionLauncher.cs:41-48`.
- **`Program.cs:274` (`DetachTray`)** sets the long-lived tray's cwd to the repo where `perch` was typed. The hook-launched tray inherits the session cwd too. From then on, every bare `git`, `gh`, `cmd.exe`, `explorer.exe` or `rundll32.exe` spawn looks in that repo first, for the tray's whole lifetime. Examples: `GitRepoService.cs:932`, `GitStatsService.cs:120`, `PrStatusService.cs:411`, `FileRevealer.cs:22,54`, `GitKrakenLauncher.cs:58`.
- **`Perch.Hook/Program.cs:512`** calls `ShellExecute("perch")`, which resolves from the session's cwd.
- **`StatuslineScript.cs:421`**: the generated `.mjs` calls `execFileSync('git', …, {cwd})`, and libuv searches `cwd` first.

**Tasks**
- [ ] `Main`: call `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` (or the profile dir) before anything spawns, and drop `DetachTray`'s `WorkingDirectory`. The cwd already travels in the intent file.
- [ ] Add a Core `ExecutableResolver`. It walks PATH plus PATHEXT, **never** includes the cwd, caches the result, and has a test with a fake PATH.
- [ ] Use it for `claude`, `git`, `gh` and `code`. Use `Environment.SystemDirectory` for `cmd`, `explorer` and `rundll32`.
- [ ] Set `NoDefaultCurrentDirectoryInExePath=1` in the child environment wherever Perch spawns `cmd`.
- [ ] Hook: launch the absolute tray path from the `perch.path` breadcrumb, with an explicit `WorkingDirectory`.
- [ ] Statusline `.mjs`: resolve git from PATH explicitly. That means a PATH walk in the script, or baking in the absolute path at generation time.
- [ ] Test: a temp dir containing a `claude.cmd` that writes a marker. Start a controlled session there and assert the marker is absent.

<a id="cp8"></a>
### CP8 — Link opening: scheme allowlist + browser argument injection · 🟠 P1 · S · ⬜

**Problem.**
- Markdown link and autolink targets (`MarkdownView.cs:612-627`, `LinkText.cs:84-86`) reach `UrlOpener.Open`, which calls `ShellExecute` (`UrlOpener.cs:19`) with no scheme check. So `file:`, UNC, `search-ms:` and `ms-*:` links execute on click.
- Middle-click (`UrlOpener.cs:35-38,60-69`) passes the URL as a raw argument to the browser exe, so a destination like `--gpu-launcher=…` becomes a browser switch.
- PR check `detailsUrl` values (`OverlayCanvas.cs:5187`) reach the same sink.

**Tasks**
- [ ] `IUrlOpener` (Windows and Mac): accept only an absolute `Uri` with scheme `http`, `https` or `mailto`. Reject everything else in one place.
- [ ] `MarkLink`: apply the same filter, so relative and file targets never become links. Route them to the existence-gated FileRef viewer instead.
- [ ] Put `--` before the URL in the new-window and private launches.
- [ ] xUnit on the (pure) validator: `file:///x.exe`, `\\h\s\x`, `search-ms:`, `--flag`, `javascript:` are rejected; `https://…` is accepted.

<a id="cp9"></a>
### CP9 — No UNC/remote path probing · 🟠 P1 · S · ⬜

**Problem.** `MarkdownView.ResolveFile` (`MarkdownView.cs:570-583`) calls `File.Exists` on any rooted inline-code span, including `\\attacker\s\a.md`. It runs on the UI thread, and streaming repeats it about 25 times a second. The effect is that SMB/WebDAV authentication sends the user's NTLM hash to the attacker, and the UI hangs for the SMB timeout. The same pattern exists in `gitdir:` resolution (`PrStatusService.cs:464-468`, `GitHead.cs:55`, and `findGitDir` in the statusline `.mjs`).

**Tasks**
- [ ] Add a shared `LocalPath.IsSafeToProbe(path)`. It rejects a leading `\\` or `//`, `\\?\` and `\\.\` prefixes, and non-fixed drives. Optionally, it also requires the path to be under the session cwd.
- [ ] Apply it in `ResolveFile`, the `gitdir:` resolvers, and the `.mjs`.
- [ ] Cache `ResolveFile` results per (cwd, text), so streaming doesn't re-probe.
- [ ] xUnit covering UNC, device-path, relative and absolute-local cases.

<a id="cp10"></a>
### CP10 — Named pipes: current-user only, park the valet hook · 🟠 P1 · S · ⬜

**Problem.**
- `perch-valet` and `perch-control` are fixed, machine-global names, with no `CurrentUserOnly` or `PipeSecurity` (`ValetServer.cs:35`, `ControlServer.cs:30`). Clients never check who owns the server (`Perch.Hook/Program.cs:270`, `Program.cs:222`).
- On a shared or RDS host, another user can squat the pipe. They then see every tool payload and can reply `allow` (approving the call without a prompt) or `deny` with an injected reason.
- The valet feature is parked, yet its hook is still registered on every PreToolUse (`ClaudeUserSettings.cs:30`). Each call pays up to about 200ms of `Connect(200)` spin when the tray is down.
- Neither server has a read timeout or line cap, and pipe creation failing once disables the pipe permanently.

**Tasks**
- [ ] Stop registering the `valet` hook until the feature ships. `ReconcileHooks` should strip it from existing installs.
- [ ] Use `PipeOptions.CurrentUserOnly` on both servers and all clients, and append the user SID to the pipe names. Hook and tray must agree on the name.
- [ ] Clients: pass `TokenImpersonationLevel.Identification`.
- [ ] Servers: add a read timeout of about 5s and a line cap of about 64KB, and retry pipe creation with backoff instead of `return`.
- [ ] Hook: check the pipe exists (`File.Exists(@"\\.\pipe\…")`) before `Connect`, so a missing tray costs nothing.
- [ ] Test: ControlServer and ValetServer round-trips still pass (`ValetServerTests`). Add a test for the oversized line.

<a id="cp11"></a>
### CP11 — Hardened shared `GitRunner` · 🟡 P2 · M · ⬜

**Problem.**
- git runs automatically in untrusted repos: `GitStatsService` every 3s, plus `GitRepoService` status/diff and the statusline `.mjs`. None of these neutralise `core.fsmonitor`, `diff.external` or textconv. A repo that arrives with a pre-populated `.git` (zip, shared drive, or a config written by an injected session) runs its command on the next poll.
- There are four copy-pasted `RunGit` implementations (GitRepoService, GitStatsService, MarkdownProjectScan, ProjectFileScan), and no global concurrency cap.
- `PrStatusService.cs:198` blocks thread-pool threads on `_gate.Wait()`.

**Tasks**
- [ ] One `Perch.Core` `GitRunner`. It:
  - uses the absolute git path (CP7);
  - always passes `-c core.fsmonitor= -c diff.external= -c core.hooksPath=` and `--no-ext-diff --no-textconv` on diff and status;
  - applies a timeout with tree-kill;
  - is limited by a global `SemaphoreSlim` (for example 4) with `WaitAsync`.
- [ ] Migrate all four callers, and the statusline `.mjs` flags.
- [ ] Cap `GetChangeStats`' sequential `git diff --no-index` calls, or batch them.
- [ ] xUnit: a fixture repo with `core.fsmonitor` pointed at a marker script; the marker is never created.

<a id="cp12"></a>
### CP12 — cmd-shim metacharacters (VS Code / GitKraken launch) · 🟡 P2 · S · ⬜

**Problem.**
- `FileRevealer.cs:35-38` (`code` → `code.cmd`) and `GitKrakenLauncher.cs:55-67` (`cmd.exe /c <cli.cmd>`) go through cmd. .NET only quotes arguments that contain whitespace or quotes, so `&`, `|` and `^` pass through. A file named `x&calc&.md` plus "Open in VS Code" runs calc. A benign `C:\work\R&D` breaks the GitKraken launch.
- `FileRevealer.cs:43` falls back to shell-opening the path itself, so a `.bat` file ref gets executed.

**Tasks**
- [ ] Launch `Code.exe` and `gitkraken.exe` directly, resolved from the install location or registry, and not the `.cmd` shims.
- [ ] If a shim is unavoidable, caret-escape the arguments and refuse `% & | ^ < >`.
- [ ] Remove the default-handler fallback, or restrict it to viewer-safe extensions (`.md .txt .json .png …`).

<a id="cp13"></a>
### CP13 — Control-pipe intent validation + launcher quoting · 🟡 P2 · S · ⬜

**Problem.**
- `SessionOpenIntent.Parse` (`ControlProtocol.cs:116-123`) doesn't validate `mode`, `cwd` or `resume`. Any same-user process can open a `bypassPermissions` session in a folder of its choosing, focused.
- `--open-intent-file <any path>` deletes that file (`Program.cs:303-310`).
- `SessionLauncher.cs:19,41-48` places the session id unvalidated into `powershell -Command "claude --resume {id}"`. `-d "{cwd}"` breaks on a trailing `\`, and wt treats `;` as a subcommand separator.
- `SessionLock.Acquire` (`ClaudeSessionController.cs:91,144`) always writes to the primary `sessions/` dir and ignores a `false` return.

**Tasks**
- [ ] `Parse`: allowlist the modes (refuse `bypassPermissions` over the pipe), require `Directory.Exists(cwd)`, and run `IsSafeToken` on ids. Reuse `FromArgs`' validation.
- [ ] Accept, and delete, only `%TEMP%\perch-intent-<32 hex>.json`.
- [ ] `SessionLauncher`: validate the id with `IsSafeToken`, trim trailing `\` from the cwd, escape `;` for wt, and use `ArgumentList`.
- [ ] `SessionLock.Acquire`: use `Path.Combine(configDir, "sessions")`, and refuse to launch on `false`.
- [ ] xUnit covering `Parse` and the launcher command-line builder.

<a id="cp14"></a>
### CP14 — Never wipe `.claude.json`; atomic writes everywhere · 🟠 P1 · M · ⬜

**Problem.**
- `DirectoryTrust.cs:136-139,176-186` treats a read failure as "no file". Accepting trust while `.claude.json` is locked or 0 bytes replaces it with `{"projects":{…}}`, which wipes the OAuth account and all project state.
- `ClaudeUserSettings.cs:95,132,346,402,426` and `Perch.Hook/Program.cs:451` write `settings.json` by truncate-then-write, and `ReconcileHooks` rewrites it on every launch even when nothing changed.
- `TodoStore.cs:117` is non-atomic, and a failed parse loads as an empty list that the next save persists.
- `AppSettings.Save` (`AppSettings.cs:878`) uses a fixed temp name, doesn't flush to disk, and swallows errors.

**Tasks**
- [ ] `DirectoryTrust`: create a fresh object only when `!File.Exists`. On a read error, or an existing file that's empty or unparseable, **refuse** (return false). Re-read immediately before replacing.
- [ ] Add a shared `AtomicFile.Write(path, text)`: unique temp in the same directory, `Flush(true)`, then `File.Replace` / `File.Move(overwrite)`, retried on sharing violations. Use it for settings.json (tray and hook), todos, AppSettings and `.claude.json`.
- [ ] `ReconcileHooks` and the other mutators: skip the write when the serialized output equals what was read.
- [ ] `TodoStore`: on a parse failure, keep a `.bak` and don't overwrite.
- [ ] `AppSettings.Save`: serialize saves through a lock, and log failures.
- [ ] xUnit: `DirectoryTrust` against a locked or empty file leaves the bytes unchanged; reconcile against an already-reconciled file does no write (check the mtime).

<a id="cp15"></a>
### CP15 — Recording-export redaction gaps · 🟡 P2 · S · ⬜

**Problem.**
- `TranscriptRedactor.cs:52` ships unparseable lines verbatim, including the partially written trailing line of a live transcript.
- `:104-105` deep-clones preserve-listed keys (`name`, `id`, `type`, `model`) at any depth, so `{"name":"Customer A"}` in a tool input survives.
- `:146-148` keeps whole `<local-command-stdout>` strings that contain "Set model to".
- `RecordingExporter.cs:267-276` keeps the `/rename` title, `waitingFor` and `bridgeSessionId` in the snapshot.

**Tasks**
- [ ] Replace unparseable lines with the redaction token, or drop them.
- [ ] Apply preserve keys only at known structural paths, and only to scalar values.
- [ ] Keep only the extracted model name from the model-switch stdout.
- [ ] Use an allowlist for snapshot fields when redact is on.
- [ ] xUnit using fixture lines for each leak.

<a id="cp16"></a>
### CP16 — Small security hardening batch · ⚪ P3 · M · ⬜

- [ ] **Statusline template injection** (`StatuslineScript.cs:88-96`). The chained `.Replace` substitutes `@@NAME@@` inside the already-substituted template, which breaks out of the JS string literal. Do a single-pass substitution with the template last, and add an xUnit with `@@NAME@@` in the template.
- [ ] **Loopback OAuth listener** (`LoopbackListener.cs:51-67`). Loop accepting until you get `GET /callback` with a matching random `state`, ignoring everything else. Add `state` to the auth URL.
- [ ] **Supabase config override** (`SupabaseConfig.cs:29-47`, `DotEnv.cs`). In release builds, ignore `PERCH_SUPABASE_URL` and ancestor `.env.local` files unless a dev flag is set, and pin the refresh token to the compiled-in origin.
- [ ] **Token refresh single-flight.** `SemaphoreSlim` around refresh (`SupabaseSocialClient.cs:187,760`).
- [ ] **Realtime reconnect.** Add jitter to the backoff, and decode UTF-8 once per whole message rather than per fragment (`SupabaseRealtime.cs:241-263,315`).
- [ ] **`PERCH_SESSION_LOG`** (`ClaudeSessionController.cs:127`). Show a visible warning when it's active, write only under the per-user app-data folder, and cap its size. Do the same for `PERCH_VDM_DEBUG`.
- [ ] **Login Run key** (`App.axaml.cs:562`, `LoginItem.cs:27`). Register only when `InstallChannel` is `Setup`, so portable or dev copies don't hijack it.
- [ ] **Hook exe location.** Move the hook exe out of Roaming `%APPDATA%\Perch\bin` to `%LocalAppData%`. That needs a migration of the hook paths in settings.json.
- [ ] **Artifact detection.** `TranscriptReader.cs:576-583`: host-check `toolUseResult.url` as `claude.ai` before treating it as an artifact.

---

## Supply chain

<a id="cp17"></a>
### CP17 — CI permissions, pinning, deploy-secret scoping · 🟡 P2 · S · ⬜

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
- [ ] `release.yml`: `contents: read` on the build jobs and `write` only on the release job. Add `persist-credentials: false` to checkout.
- [ ] Pin every action to a commit SHA, with the version in a comment. Add Dependabot for `github-actions`.
- [ ] Pin `vpk` to the Velopack library version (`--version 1.2.0`, or `.config/dotnet-tools.json`). Add `global.json`, plus NuGet lock files with `--locked-mode`.
- [ ] Supabase workflows: secrets only on the steps that use them; pin the setup-cli SHA and CLI version; add `environment: production` with required reviewers, limited to `main`; add `permissions: contents: read`.

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
### CP19 — Build/installer hygiene · ⚪ P3 · S · ⬜

- [ ] `publish-mac.sh:48,140` contains em dashes, which fails `tools/test-install.ps1`'s ASCII check. **The test currently fails.** Replace them with plain hyphens.
- [ ] `test-install.ps1`: build the ASCII file list from a glob so it covers `db-migrate.yml`, `functions-deploy.yml`, `tools/gen-dmg-background.sh` and `tools/focus.ps1`. Replace `Invoke-Expression` (line 21) with dot-sourcing a temp copy.
- [ ] `PathInstaller.cs:17-34`: read and write `HKCU\Environment` with `DoNotExpandEnvironmentNames`, keep `REG_EXPAND_SZ`, and write only when the value changed. Today every install or uninstall permanently expands `%VAR%` entries in the user's PATH.
- [ ] `publish.bat`: pass `-p:Version=%VERSION%` to both publishes, and hash only this version's files, not stale nupkgs. `release.yml`: pass `-p:Version` to the Windows hook build.

---

## Performance

<a id="cp20"></a>
### CP20 — Session scan off the UI thread + incremental transcripts · 🟠 P1 · L · ⬜

**Problem.**
- **Scan runs on the UI thread.** Every trigger (watcher, the 2s controlled poll, reconcile) calls `SessionMonitor.Scan()` on the dispatcher (`SessionMonitorHost.cs:81-88`).
- **Active transcripts are re-read in full.** Per session, Scan re-runs five or six full-file passes over an actively growing transcript: `ScanContext`, `ParseArtifacts`, `ParseTasks`, `ParseOutstandingAsyncAgent`, `ParseTitle` and the legacy `SubAgentReader.Parse`. The length+mtime cache misses on every write. `ParseTitle` reads the **whole file** when there's no `/rename`, although the comment says 32KB.
- **The hook triggers it on every tool call** by writing `.mode`. That means a full re-read on the UI thread per tool call: roughly 100-300ms at 10MB, and seconds at 100MB.
- **Triggers aren't coalesced.** Git-stats updates and process exits skip the debounce.
- **Path fallback is expensive.** `TranscriptLocator` falls back to scanning every project dir, about 10 times per session per scan, with no negative cache.
- **Sub-agent work repeats.** `SubAgentReader` re-classifies whole agent transcripts, and stats every agent file on every scan.

**Tasks**
- [ ] Run Scan on a single-flight background worker (a pending flag plus one trailing re-run), then post the result list to the UI. The events currently fired "from within Scan" must still reach consumers on the UI thread.
- [ ] Add an incremental transcript fold: `(path → offset, accumulated state)`. Read `[offset, EOF)` on growth, reset on shrink or fingerprint change, and do one pass that feeds every reader. The roost branch's `TranscriptTailReader` can be the primitive.
- [ ] `ParseTitle`: use a real 32KB head window.
- [ ] Resolve the transcript path once per session per scan, and negative-cache misses until the `projects/` mtime changes.
- [ ] `SubAgentReader`: classify from the tail; skip non-teammate agent files older than the stale window.
- [ ] Route every trigger through `RequestScanDebounced`.
- [ ] Measure before and after: log scan duration with 20 sessions and a 50MB transcript.
- [ ] xUnit: an incremental fold gives the same result as a full scan across appends, truncation and rotation.

<a id="cp21"></a>
### CP21 — All-time stats: cache history, don't re-parse it · 🟠 P1 · M · ⬜

**Problem.** `App.axaml.cs:1166-1188` → `SessionStatsService.ReportAllTime` (`:252-395`) and `TeamReader.cs:86` JSON-parse every line of every transcript whenever a session finishes, throttled to once per 3 minutes. With GBs of history, that is tens of seconds of a pegged core and heavy LOH churn in an all-day process. The Stats and Achievements windows pay the same cost.

**Tasks**
- [ ] Cache per-file, per-day `SessionDayData` keyed by (path, length, mtime), plus the read offset, so growing files are read incrementally. Persist the cache to disk under the profile dir.
- [ ] Achievements: update incrementally from the cache rather than recomputing all-time.
- [ ] xUnit: a cached result equals a fresh result, and an appended file only reads the delta.

<a id="cp22"></a>
### CP22 — Streaming chat O(n²) + SessionThreadView leak · 🟠 P1 · M · ⬜

**Problem.**
- **Settled prefix is rebuilt each time.** `SessionThreadView.cs:871-909` (`RenderRevealed`) rebuilds the whole settled prefix (Markdig parse, control tree, `File.Exists` calls) at every block boundary.
- **Open blocks are rebuilt every frame.** An open fenced code block or table is re-parsed and re-highlighted every 40ms.
- **Text concatenation is quadratic.** `SessionConversation.cs:210` (`Text += delta`) copies the whole reply per delta.
- **Closing a window leaks its chat view.** `SessionWindow.Detach` (`SessionWindow.cs:837-847`) never unbinds `_thread`, and the session outlives the window by design. Every close and reopen leaves a whole chat tree subscribed and processing deltas.

**Tasks**
- [ ] Make settled blocks append-only: render only the newly settled slice and add it as a new child.
- [ ] Throttle tail rendering by size: fall back to plain text while the tail is over N KB or inside an open fence, or re-highlight at most every 250ms.
- [ ] Use a `StringBuilder` in `TextPart`, and batch `Changed` notifications.
- [ ] Add `SessionThreadView.Unbind()` (the roost branch has one; port or merge it), and call it from `Detach` and `OnClosed`.
- [ ] Verify: stream a long reply from a fixture replay and watch frame time. Close and reopen a window 10 times and confirm the old views are collected (a debug counter or the memory profiler).

<a id="cp23"></a>
### CP23 — Overlay paint path: no IO, no per-frame allocations · 🟡 P2 · M · ⬜

**Problem.**
- **File IO while painting.** `OverlayCanvas.cs:2838` → `AccountMismatch` → `OrgProvider.GetLive` (`OrgProvider.cs:43-87`) does four file stats per rule-governed row per frame, and a full `.claude.json` parse inside `Render` whenever the mtime changes.
- **The mismatch pulse never stops.** The 60ms pulse (`:3186-3201`) keeps invalidating even while the overlay is hidden.
- **Allocation on every frame.** `OverlayDraw.cs:17-18,38-40,105-123` creates a new `Typeface` and `FormattedText` per text call, about 25-30 shapings per row per frame.
- **Truncation is expensive and unsafe.** `Truncate` re-shapes on every binary-search probe, and can split surrogates and ZWJ sequences.
- **Brushes are recreated.** New `SolidColorBrush` objects are created in the row loop.
- **UsageMonitor blocks the UI thread first.** `UsageMonitor.FetchAsync` does synchronous file IO before its first await (`UsageMonitor.cs:86,93`).

**Tasks**
- [ ] Compute the account mismatch per scan in `Update()`, off-thread, and cache it on the row. Throttle the `.claude.json` stamp check to about 2s, and parse only the `oauthAccount` field with a streaming reader (`Utf8JsonReader`).
- [ ] Stop the pulse when the overlay isn't visible, and invalidate only the outline rect.
- [ ] Static cached typefaces, cached frozen brushes, and a small LRU of `FormattedText` keyed by (text, size, weight, brush).
- [ ] Compute truncated labels once per `Update`, on grapheme boundaries (`StringInfo`).
- [ ] Move `UsageMonitor`'s token and CLI-version reads into the `Task.Run`.
- [ ] Verify: a render-mode visual diff shows no change, and a frame-time counter during the chase animation shows an improvement.

<a id="cp24"></a>
### CP24 — Misc perf batch · 🟡 P2 · M · ⬜

- [ ] `MetricsMonitor.cs:292-308`: take one process snapshot per tick (pid → parent, working set), rather than calling `GetProcessById().WorkingSet64` for each process (each of which triggers a system-wide snapshot). Read CPU times with `GetProcessTimes`.
- [ ] `HistoryWindow.cs:442-468,548-560`: offset-based tail, reading only appended bytes, via `TranscriptTailReader` from roost. Decode base64 images off the UI thread.
- [ ] `DiffView.cs:82`: add a global line budget, collapse files past N by default, truncate very long lines, and consider virtualisation.
- [ ] Arcade windows (`WordleWindow.cs:143`, `ArcadeMenuWindow.cs:144`, Frogger, SpaceInvaders, Connect4, DrawWithPerch): pause the 16ms timer on deactivate or minimize, and stop Wordle and the menu when nothing is animating.
- [ ] Image decode (`ImageViewerWindow.cs:94`, `ImageRefText.cs:165`, `AttachmentChip.cs:156`): read the header dimensions first and cap the pixel count, use `DecodeToWidth` for previews, and decode off-thread. `PerchSession.cs:218`: cap attachment size (about 5MB) before base64.
- [ ] `GitTreeWindow.cs:1313-1355`: coalesce watcher events on the background thread before posting; filter `node_modules`, `bin/obj` and `.git` internals other than HEAD, index and refs.
- [ ] `CodeHighlight`: build keyword sets once, statically.
- [ ] `PerchSession.LoadHistoryAsync`: use a ring buffer instead of reading the whole transcript into a list and then trimming to 4000 lines.

---

## Correctness

<a id="cp25"></a>
### CP25 — Watcher race, PID reuse, Process disposal · ⚪ P3 · S · ⬜

- [ ] `SessionMonitor.cs:1264-1277`: `OnWatcherError` mutates `_watchers` on a thread-pool thread while the UI thread uses it. Post the teardown to the owning thread, or lock. After CP20 this becomes "the scan worker's thread".
- [ ] `IProcessProbe.cs:23-33`: liveness should compare the process start time against the session file's `startedAt`, as `SessionTerminator` already does, so a reused PID doesn't keep a dead session alive. Dispose `Process` objects.
- [ ] `DaemonRosterReader`: one worker with a mistyped field shouldn't drop the whole roster.

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
