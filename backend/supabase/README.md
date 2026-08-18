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
