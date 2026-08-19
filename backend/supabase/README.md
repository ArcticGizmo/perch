# Perch Social — backend (Supabase)

The database half of the Social feed: schema, the row-level-security policies that **are** the
authorization boundary, and pgTAP tests that prove them. See `docs/social-feed-plan.md` (why
Supabase) and `docs/social-feed-implementation.md` (the design).

## Layout

```
backend/supabase/
  config.toml                    # Supabase CLI config (run CLI with --workdir backend)
  migrations/
    <ts>_init.sql               # tables, enums, indexes, are_friends() + find_profile()
    <ts>_rls.sql                # RLS on every table + all policies + RPC grants
    <ts>_grants.sql             # table-level GRANTs for the authenticated role
    <ts>_profile_visibility.sql # read a friend's profile (shares_edge)
    <ts>_block_report.sql       # blocks + reports tables; are_friends folds in blocking (M6)
    <ts>_rate_limit.sql         # per-author post rate-limit trigger (M6)
    <ts>_moderation.sql         # suspension kill-switch; posts + find_profile respect it (M6)
    <ts>_reactions.sql          # emoji reactions on posts (friends-only via can_see_post)
    <ts>_reactions_one_per_user.sql # PK (post_id, reactor): one reaction per user per post
  tests/
    rls_test.sql    # pgTAP: non-friends can't read posts, blocking, rate limit, suspension, etc.
```

## One-time setup (needs your account)

1. Create a **Supabase project** (free tier). Note the **project URL** and the **publishable** key
   (Project Settings → API Keys). The publishable key is non-secret and ships in the app; the `service_role`
   key must **never** leave the server.
2. Enable **GitHub** as an auth provider (Authentication → Providers → GitHub) and register a GitHub
   OAuth app, putting its client id/secret into Supabase. Add the desktop loopback redirect
   (`http://127.0.0.1:<port>/callback`) to the allowed redirect URLs — the exact port is finalised
   in M2.

## Point the app at your project

Get the **Project URL** and the **publishable** key from the dashboard: Project Settings -> API Keys
(older UIs: Settings -> API, labelled `anon` / `public` - it works in the same slot). Never use the
`service_role` / `secret` key in the app or repo.

`SupabaseConfig.Resolve()` reads them from, in order (first fully-populated wins):

1. **Env vars** `PERCH_SUPABASE_URL` / `PERCH_SUPABASE_PUBLISHABLE_KEY` - the highest override:
   ```powershell
   $env:PERCH_SUPABASE_URL = "https://YOUR-REF.supabase.co"
   $env:PERCH_SUPABASE_PUBLISHABLE_KEY = "sb_publishable_..."
   dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
   ```
2. **`.env.local` at the repo root** (dev builds) - the easiest spot in a checkout. Copy the root
   `.env.local.example` to `.env.local` (it sits next to `perch.slnx`) and fill in the two `KEY=VALUE`
   lines. It's gitignored, and dev builds read it automatically. Just `dotnet run` - no env vars to set.
3. **Compiled-in defaults** (`SupabaseDefaults.cs`, empty in git) - the only path that ships to end users.

**Release builds** get the key baked in by the `release.yml` "Inject Supabase config" step from two repo
secrets - add these under Settings -> Secrets and variables -> Actions:

| Secret | Value |
|--------|-------|
| `PERCH_SUPABASE_URL` | `https://YOUR-REF.supabase.co` |
| `PERCH_SUPABASE_PUBLISHABLE_KEY` | the publishable key (`sb_publishable_...`, or a legacy `eyJ...` anon key) |

The public repo carries neither; empty secrets are a no-op (Social just stays inert), so a fork still builds.

## Apply the migrations

All CLI commands take `--workdir backend` so the CLI finds `backend/supabase/` (Perch keeps its Supabase
files under `backend/`, not the default repo-root `./supabase`). Migrations are idempotent
(`create ... if not exists`, `create or replace`, `drop policy if exists`), so re-applying is always safe.

**Quick / one-off (SQL editor):** paste the `*_init.sql` then the `*_rls.sql` migration into the dashboard
SQL editor and run, in order. Fine for a first bring-up; the automated paths below are better once iterating.

**Automated - push to the hosted project (recommended, no Docker):**
```bash
supabase login                                             # once, opens a browser (or set SUPABASE_ACCESS_TOKEN)
supabase link --project-ref ecrehwttqpgdpzroazwp --workdir backend   # once, prompts for the DB password
supabase db push --workdir backend                         # applies any not-yet-applied migrations
```
To add a migration later: `supabase migration new <name> --workdir backend`, edit the generated file, then
`db push` again. (If you already applied the migrations by hand, `db push` will re-apply them harmlessly -
they're idempotent - or run `supabase migration repair --status applied <version> --workdir backend` for
each, where `<version>` is the file's 14-digit timestamp prefix, to mark them applied instead.)

**Automated - CI (Actions):** `.github/workflows/db-migrate.yml` runs `db push` when a migration changes on
`main` (and on demand via *Run workflow*). It has two gates so an expired token/password only fails a run
when there's genuinely something to apply: the `paths` filter (only starts on `migrations/` changes) and a
tokenless git check that skips the link/push steps unless the push actually **added** a migration file
(edits, renames, deletions or doc churn add no new version, so the token is never touched and the run passes
green). Add three repo secrets under **Settings -> Secrets and variables -> Actions**:

| Secret | Where to get it |
|--------|-----------------|
| `SUPABASE_ACCESS_TOKEN` | supabase.com/dashboard/account/tokens |
| `SUPABASE_DB_PASSWORD` | Project Settings -> Database (reset it there if unknown) |
| `SUPABASE_PROJECT_REF` | `ecrehwttqpgdpzroazwp` (not sensitive - it's in the project URL) |

**Local full stack (offline, needs Docker):**
```bash
supabase start --workdir backend       # local Postgres + auth in Docker
supabase db reset --workdir backend    # applies everything in migrations/ in order
supabase test db --workdir backend     # runs tests/ (pgTAP)
```

## Testing from one machine (puppet account)

GitHub sign-in gives you one identity, so the friends/posts/reactions loop is impossible to exercise
solo. The app ships a hidden **developer testing tool** that drives a second "puppet" account:

1. **Create the puppet user.** Supabase dashboard → **Authentication → Users → Add user**. Give it an
   email + password and tick **Auto Confirm User** (so no email round-trip). This is an ordinary email/
   password user — nothing special server-side.
2. **Enable the tool.** Set `PERCH_SOCIAL_DEBUG=1` (a real env var, or add it to the repo `.env.local` —
   see `.env.local.example`). It's off unless set, so it never appears in a normal install.
3. **Open it.** Sign in with your real GitHub account, then **Settings → Social → "Testing tool
   (puppet account)…"**.
4. **Drive the loop.** In the tool: *Sign in as puppet* (the email/password above) → *Claim handle* →
   *Send me a friend request*. Accept it in your real Friends window (the **+** in the overlay's Friends
   region). Then *Post as puppet* and *React to my latest post* — each action re-polls your overlay so it
   updates immediately. "Accept my requests" handles the reverse direction (a request you sent the puppet).

The puppet keeps its session in memory only, so it never disturbs your real signed-in session. It's a
normal authenticated user, so every RLS rule (friends-only posts, reactions, blocking) applies to it
exactly as to a real friend — which is what makes it a faithful test.

## Branching (deferred)

Supabase **Branching** gives each Git branch/PR its own ephemeral Supabase environment (own DB + auth) with
your migrations auto-applied - ideal for PR previews. It needs the Supabase GitHub integration **and a paid
(Pro) plan**: each preview branch is billable compute. For a solo, free-tier hobby project the `db push` CI
above covers the same need at no cost, so branching is intentionally not set up. If you later go Pro, connect
the repo in the dashboard (Branches) and point its "Supabase directory" at `backend/supabase`.

## Verify security

`supabase test db` runs `rls_test.sql`, which asserts (as role `authenticated` with a simulated
JWT `sub`, so `auth.uid()` resolves like a real signed-in user):

- a **pending** friend cannot read the other's posts;
- an **accepted** friend can, and you can always read your own;
- a **stranger** cannot read your posts;
- `find_profile` matches an **exact** handle only (no partial/enumeration);
- a **third party** cannot see two other people's friendship rows;
- **(M6)** a **block** hides posts in both directions, and unblocking restores them;
- **(M6)** the per-author **rate limit** rejects the 11th post inside a minute;
- **(M6)** a **suspended** author's posts are hidden from a friend and the handle stops resolving.

> Status: authored in M0, extended with the M6 safety checks, **not yet run against a live project** —
> that waits on the Supabase project above. The `auth.users` insert in the test assumes the standard
> Supabase local stack; if a required column has no default in your version, add it to the fixture insert.

## Safety model (M6)

- **Block** — a private, one-sided row in `blocks` (blocker → blocked). `are_friends()` consults
  `is_blocked()`, so a block kills post visibility in **both** directions regardless of friendship. It's a
  separate table, not the `friendships` enum, precisely because `friendships_respond` lets either party
  update an edge — a blocked user must never be able to lift their own block. `list_blocked()` (SECURITY
  DEFINER) returns only the caller's blocked profiles for the "unblock" UI.
- **Report** — `reports` is insert-only for `authenticated` (no select policy), so it's a write-only queue
  only `service_role` (you, in the dashboard) can read. The app's "Report" also blocks, since a lone report
  leaves the content in your feed.
- **Rate limit** — a `BEFORE INSERT` trigger on `posts` rejects an author's 11th post inside a rolling
  minute. Enforced in the DB, so a tampered client can't flood; the compose window's guard is courtesy only.
- **Moderation kill-switch** — the `moderation` table (RLS on, **no policies/grants**, so only
  `service_role` touches it) suspends a handle: `posts_read` hides a suspended author's posts, a trigger
  blocks them from posting, and `find_profile` stops resolving them — all via the `is_suspended()` SECURITY
  DEFINER helper. Suspend/lift from the SQL editor (see the header of `*_moderation.sql`).

## Security checklist — sign-off (see implementation §7)

- [x] RLS enabled on **every** table (`profiles`, `friendships`, `posts`, `blocks`, `reports`,
  `moderation`), with a pgTAP test per policy in `rls_test.sql`.
- [x] `service_role` key is server-side only; the app ships the publishable key only (see config above).
- [x] Body length + handle format enforced by DB `CHECK` (schema migration), not just the client.
- [x] Friend discovery is exact-handle only; friendship rows invisible to third parties.
- [x] Refresh token stored via `ISecretStore` (DPAPI / Keychain), never plaintext (`SupabaseSocialClient`).
- [x] Per-user post rate limit (trigger); block + report available; suspended-author content pulled.
- [x] Nothing from `~/.claude` is ever auto-posted — composing is manual and explicit (no ingest path exists).
- [x] TLS/HTTPS only (`https://`/`wss://`); transport degrades WebSocket → polling for firewalled users.

> One item remains **manual, not code**: running `supabase test db` (or the SQL-editor checks) against the
> live project to confirm the boxes above hold on real Postgres. The tests are written and pass locally in
> intent; they haven't been executed against the hosted DB yet.
