# Perch Social — backend (Supabase)

The database half of the Social feed: schema, the row-level-security policies that **are** the
authorization boundary, and pgTAP tests that prove them. See `docs/social-feed-plan.md` (why
Supabase) and `docs/social-feed-implementation.md` (the design).

## Layout

```
backend/supabase/
  migrations/
    0001_init.sql   # tables, enums, indexes, are_friends() + find_profile()
    0002_rls.sql    # RLS on every table + all policies + RPC grants
  tests/
    rls_test.sql    # pgTAP: non-friends can't read posts, etc.
```

## One-time setup (needs your account)

1. Create a **Supabase project** (free tier). Note the **project URL** and the **anon** public key
   (Project Settings → API). The anon key is non-secret and ships in the app; the `service_role`
   key must **never** leave the server.
2. Enable **GitHub** as an auth provider (Authentication → Providers → GitHub) and register a GitHub
   OAuth app, putting its client id/secret into Supabase. Add the desktop loopback redirect
   (`http://127.0.0.1:<port>/callback`) to the allowed redirect URLs — the exact port is finalised
   in M2.

## Point the app at your project

Get the **Project URL** and the **anon / publishable** key from the dashboard: Project Settings -> API
Keys (older UIs: Settings -> API). Never use the `service_role` / `secret` key in the app or repo.

`SupabaseConfig.Resolve()` reads them from, in order:

1. **Env vars** `PERCH_SUPABASE_URL` / `PERCH_SUPABASE_ANON_KEY` - the dev override:
   ```powershell
   $env:PERCH_SUPABASE_URL = "https://YOUR-REF.supabase.co"
   $env:PERCH_SUPABASE_ANON_KEY = "eyJ..."
   dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0
   ```
2. **Local file** `%AppData%\Perch\supabase.local.json` (or `Perch (Dev)`) - outside the repo, so it can't
   be committed. Copy `supabase.local.example.json` and fill it in.
3. **Compiled-in defaults** (`SupabaseDefaults.cs`, empty in git) - the only path that ships to end users.

**Release builds** get the key baked in by the `release.yml` "Inject Supabase config" step from two repo
secrets - add these under Settings -> Secrets and variables -> Actions:

| Secret | Value |
|--------|-------|
| `PERCH_SUPABASE_URL` | `https://YOUR-REF.supabase.co` |
| `PERCH_SUPABASE_ANON_KEY` | the anon / publishable key (`eyJ...` or `sb_publishable_...`) |

The public repo carries neither; empty secrets are a no-op (Social just stays inert), so a fork still builds.

## Apply the migrations

**Hosted (SQL editor):** paste `0001_init.sql` then `0002_rls.sql` and run, in order.

**Local (Supabase CLI + Docker):**
```bash
supabase start                     # spins up local Postgres + auth
supabase db reset                  # applies everything in migrations/ in order
supabase test db                   # runs tests/ (pgTAP)
```

## Verify security

`supabase test db` runs `rls_test.sql`, which asserts (as role `authenticated` with a simulated
JWT `sub`, so `auth.uid()` resolves like a real signed-in user):

- a **pending** friend cannot read the other's posts;
- an **accepted** friend can, and you can always read your own;
- a **stranger** cannot read your posts;
- `find_profile` matches an **exact** handle only (no partial/enumeration);
- a **third party** cannot see two other people's friendship rows.

> Status: authored in M0, **not yet run against a live project** — that waits on the Supabase
> project above. The `auth.users` insert in the test assumes the standard Supabase local stack;
> if a required column has no default in your version, add it to the fixture insert.

## Security checklist (kept in sync as milestones land — see implementation §7)

- [ ] RLS enabled on **every** table (0002), with a passing test per policy.
- [ ] `service_role` key is server-side only; the app ships the anon key only.
- [ ] Body length + handle format enforced by DB `CHECK` (0001), not just the client.
- [ ] Friend discovery is exact-handle only; friendship rows invisible to third parties.
- [ ] Refresh token stored via `ISecretStore` (DPAPI / Keychain), never plaintext.
- [ ] Per-user post rate limit; block + server-side delete available. *(M6)*
