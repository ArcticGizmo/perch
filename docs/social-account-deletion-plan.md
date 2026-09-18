# Perch Social — account deletion / GDPR "forget me" (plan)

Status: **plan only** (2026-09-17). A self-service "delete my account and all my data" action for the
Social feature, satisfying the GDPR **right to erasure** (Art. 17). Sits alongside `social-feed-plan.md`
and `social-feed-implementation.md`; the backend half extends `backend/supabase/`.

## 1. Goal

One button — **Settings → Social → "Delete account and data"** — that, after an explicit irreversible
confirm, permanently removes:

- the user's **auth identity** (`auth.users` row, incl. the GitHub OAuth email — the only real PII the
  backend holds), and
- **every** social row that belongs to them (profile, friendships, posts, reactions, blocks, games/moves,
  moderation record), and
- the **local** session (refresh token in `ISecretStore`).

…and returns the app to a clean signed-out state.

## 2. Why an Edge Function (not the client, not a plain RPC)

The client only ever holds the **publishable** key + the user's access token, and RLS is the security
boundary. Two hard limits fall out of that:

1. **The client cannot delete its own profile row.** `profiles` has select/insert/update policies but
   **no `DELETE` policy** (`20260819120100_rls.sql`), so RLS default-denies the delete even though the
   table grant exists. A client `DELETE /rest/v1/profiles` is refused.
2. **The client cannot touch `auth.users` at all.** Deleting an auth user requires the Admin API
   (`auth.admin.deleteUser`) with the **`service_role`** key — which must never ship in the app.

A `SECURITY DEFINER` RPC (`delete_me()`) could delete the *profile* row (cascade would clear all app
data), but it would **leave the `auth.users` row** — i.e. leave the email behind. That's a data wipe, not
erasure. To remove the auth identity too we need code that runs with `service_role` **server-side**: an
**Edge Function** is exactly that, and it's the Supabase-sanctioned home for `service_role` work.

Cost is a non-issue: deletion is invoked ~once per user ever, far inside the free tier, and a deployed-
but-idle function costs nothing (billing is per-invocation).

## 3. What actually gets erased (the cascade)

Everything hangs off one root, so **a single `deleteUser(uid)` tears the whole tree down** — verified
against the migrations:

```
auth.users (id)                         ← deleteUser() removes this
  └─ profiles.id  ON DELETE CASCADE      (20260819120000_init.sql)
       ├─ friendships.requester/addressee  CASCADE
       ├─ posts.author                     CASCADE
       │    └─ reactions.post_id           CASCADE
       ├─ reactions.reactor                CASCADE
       ├─ blocks.blocker/blocked           CASCADE   (block_report)
       ├─ games.player_red/player_yellow   CASCADE   (connect4)
       │    └─ moves.game_id               CASCADE
       ├─ moves.mover                       CASCADE
       ├─ moderation.profile               CASCADE   (moderation)
       ├─ reports.reported                 CASCADE
       └─ reports.reporter        ON DELETE SET NULL  ← deliberate, see below
```

**Reports the user *filed*** (`reports.reporter`) are **anonymised, not deleted** — the FK is
`ON DELETE SET NULL` by design. The report about *someone else* stays in the moderation queue with a null
reporter. This is a defensible GDPR retention (moderation = legitimate interest / safety). Reports *about*
the deleted user cascade away with them.

The **exact retained shape** (this must be spelled out in the privacy document — see §9) is one row per
report the deleting user filed:

```
reports(
  id         uuid,        -- opaque
  reporter   = NULL,      -- ← was the deleting user; nulled on erasure, no longer identifies them
  reported   uuid,        -- the OTHER user they reported (still a live account)
  reason     text,        -- free text the deleting user typed when reporting (<=500 chars)
  created_at timestamptz  -- when the report was filed
)
```

Two honesty notes for the privacy doc: `reason` is **free text the deleting user authored**, so it could
in principle carry identifying content — we keep it because a reason-less report is near-useless to
moderation, but the privacy doc must say we retain it. And `reported` still points at the person they
reported (that account is untouched). Nothing else authored by, or identifying, the deleting user survives.

Nothing else persists their data: realtime broadcasts (invites/nudges) are transient, and friends' overlays
only *cache* the feed — once the rows are gone, the content disappears from a friend's overlay on its next
poll.

## 4. The Edge Function

Path: `backend/supabase/functions/delete-account/index.ts`.

```ts
// Perch Social — account deletion (GDPR erasure).
// Verifies the CALLER from their own access token (never a body-supplied id), then deletes that auth
// user with service_role. The FK cascade (see plan §3) removes every social row; reports the user filed
// are anonymised (reporter -> null) by their own ON DELETE SET NULL.
import { createClient } from "jsr:@supabase/supabase-js@2";

// SUPABASE_URL / SUPABASE_ANON_KEY / SUPABASE_SERVICE_ROLE_KEY are auto-injected into the function
// runtime by Supabase — nothing to wire up by hand, and the service key never leaves the server.
const URL = Deno.env.get("SUPABASE_URL")!;
const ANON = Deno.env.get("SUPABASE_ANON_KEY")!;
const SERVICE = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

Deno.serve(async (req) => {
  if (req.method !== "POST") return json({ error: "method_not_allowed" }, 405);

  const auth = req.headers.get("Authorization");
  if (!auth?.startsWith("Bearer ")) return json({ error: "missing_bearer" }, 401);

  // 1) Resolve WHO is calling from their own token. getUser() cryptographically validates the JWT and
  //    returns the user — we trust this, not anything in the request body.
  const asUser = createClient(URL, ANON, { global: { headers: { Authorization: auth } } });
  const { data: { user }, error: whoErr } = await asUser.auth.getUser();
  if (whoErr || !user) return json({ error: "invalid_token" }, 401);

  // 2) Delete that (and only that) auth user as service_role. Cascade does the rest.
  const admin = createClient(URL, SERVICE);
  const { error: delErr } = await admin.auth.admin.deleteUser(user.id);

  // Idempotent: a second call (user already gone) is a success from the client's point of view.
  if (delErr && !/not[_ ]?found/i.test(delErr.message)) {
    console.error("deleteUser failed", { uid: user.id, msg: delErr.message });
    return json({ error: "delete_failed" }, 500);
  }
  return json({ ok: true }, 200);
});
```

**Security properties**

- The uid comes from `getUser()` on the caller's verified token, so a user can **only ever delete
  themselves** — there is no id parameter to forge.
- `service_role` never leaves the function runtime; the client keeps holding only the publishable key.
- Idempotent: re-invoking after deletion returns `ok` (the "not found" branch), so a client retry after a
  dropped connection is safe.

**Config** — `backend/supabase/config.toml`, add:

```toml
[functions.delete-account]
verify_jwt = true   # gateway also rejects an unauthenticated call before our code runs (defence in depth)
```

`verify_jwt = true` (the default) means the platform validates the bearer JWT at the gateway too; we still
call `getUser()` to obtain the uid. The client already sends `apikey: <publishable>` + `Authorization:
Bearer <user access token>` on every call (its `Rest()` helper), so no new header work is needed.

## 5. Deploying it

Manual (one-off):

```bash
supabase functions deploy delete-account --workdir backend
```

No extra secrets to set — `SUPABASE_URL` / `SUPABASE_ANON_KEY` / `SUPABASE_SERVICE_ROLE_KEY` are injected
automatically.

CI (recommended, mirrors `db-migrate.yml`): add `.github/workflows/functions-deploy.yml` triggered on
`backend/supabase/functions/**` changes, running the same `functions deploy` with the existing
`SUPABASE_ACCESS_TOKEN` + `SUPABASE_PROJECT_REF` secrets. Same tokenless-skip guard idea as the DB workflow
so an unrelated push never touches the token.

## 6. Client wiring (`Perch.Core`)

**`ISocialClient`** — one new method:

```csharp
/// <summary>Permanently deletes the signed-in user's account and ALL their social data (server-side, via
/// the delete-account Edge Function), then clears the local session. Irreversible. After it returns the
/// client is signed out (<see cref="Current"/> == <see cref="AuthState.SignedOut"/>). Idempotent-ish: if
/// the server has already removed the user it still completes and signs out locally.</summary>
Task DeleteAccountAsync(CancellationToken ct = default);
```

**`SupabaseSocialClient`** — reuse the existing `Rest()` + `ValidAccessTokenAsync()`; factor the body of
`SignOutAsync` into a private `ClearSession()` and call it on success:

```csharp
public async Task DeleteAccountAsync(CancellationToken ct = default)
{
    var token = await ValidAccessTokenAsync(ct);
    using var req = Rest(HttpMethod.Post, "/functions/v1/delete-account", token);
    using var resp = await _http.SendAsync(req, ct);
    await EnsureOkAsync(resp, "delete account", ct);   // throws SocialException on non-2xx
    ClearSession();                                    // null token, _secrets.Delete(RefreshTokenKey), raise SignedOut
}
```

If the call fails (network / 500) we **do not** clear local state — the user is still signed in and can
retry; nothing was half-done because the server side is a single cascading delete.

**`FakeSocialClient`** — wipe the caller's in-memory rows (profile, posts, friendships, reactions, blocks,
games, requests) and drop to signed-out, so the render preview and tests exercise the same contract.

## 7. Local cleanup

The only local PII is the refresh token in `ISecretStore` (DPAPI / Keychain). `ClearSession()` already
`Delete`s it. `AppSettings` and overlay caches hold nothing beyond what the live feed returns, so there's
nothing else to scrub. (If we ever add a local export cache, delete it here too.)

## 8. UI (`Perch.App`)

- **Settings → Social**: a destructive-styled **"Delete account and data"** button (use
  `Palette.Fixed.Danger` — the fixed destructive red, never a theme role), shown only when signed in.
- **Confirm dialog** — irreversible, spelled out: what's removed (profile, posts, friends, reactions,
  games), what's **retained** (anonymised abuse reports you filed, for safety — the exact shape is in the
  privacy doc, §9), and that **your GitHub OAuth authorization of Perch is separate** and stays until you
  remove it. Show a **"Revoke Perch on GitHub"** link to the *exact* app's connection page —
  `https://github.com/settings/connections/applications/<CLIENT_ID>` (deep-links straight to Perch's
  "Revoke access" button, far better UX than the generic `/settings/applications` list). Require an explicit
  action (e.g. type the handle, or a two-step confirm) since it can't be undone.
- On success: toast "Your account and data were deleted", overlay Social region returns to the signed-out
  prompt (driven by the `AuthChanged → SignedOut` the client already raises).
- Runs off the UI thread then marshals back (`Task.Run` → `Dispatcher.UIThread.Post`), the standard pattern.

## 9. Privacy document — the exact data we keep

Perch has **no privacy document today** (nothing under `docs/`, no `PRIVACY.md`). A self-service erasure
feature needs one to point at, so this feature ships one — a short `PRIVACY.md` (or `docs/privacy.md`,
linked from the README and the Settings → Social page) covering the Social feature's data:

- **What we store** while you use Social: your handle, optional display name + mood, your posts and their
  reactions, your friend edges, blocks, Connect 4 games, and — held by the auth provider, not our tables —
  the **GitHub email** you signed in with. (Cross-link the existing `Profile` docstring: "no real name, no
  email in our tables".)
- **What "delete my account" removes**: everything above, via the cascade in §3, including the auth
  identity/email.
- **What we retain after deletion, and why**: the one anonymised-report shape from §3, reproduced exactly
  (`reporter = NULL`, plus `reported`, `reason`, `created_at`), with the justification (safety / abuse
  moderation is a legitimate interest, and a reason-less report is useless). This is the reference the user
  asked for — the privacy doc is the canonical statement of the retained shape; this plan and the confirm
  dialog both point at it rather than restating it.
- **Third-party**: the GitHub OAuth authorization is GitHub's; we attempt to revoke it and also link the
  user to revoke it themselves (§11).

Keep it plain-language and short; the authoritative field list lives in the migrations, so the privacy doc
describes rather than duplicates the schema. **Deliverable D4a** below.

## 10. Companion: data export (optional, Art. 20)

Not required for erasure, but cheap to add beside it and rounds out GDPR (data portability): a "Download my
data" that dumps the caller's profile + posts + reactions + friend handles to a JSON file. Pure client REST
reads (all RLS-permitted), no backend work. **Out of scope for this doc** — noted so we site the button next
to Delete when we do it.

## 11. Testing

- **`SupabaseSocialClientTests`** (loopback `HttpListener`): `DeleteAccountAsync` POSTs to
  `/functions/v1/delete-account` with the bearer; on `200` `Current` becomes `SignedOut` and the refresh
  token is gone; on `500` it throws `SocialException` and stays signed in.
- **`FakeSocialClientTests`**: after `DeleteAccountAsync`, feed/roster/friends are empty and `Current` is
  signed out.
- **Backend**: a pgTAP check (`rls_test.sql`) that deleting a `profiles` row cascades away that user's
  posts/reactions/friendships/games and null-sets `reports.reporter` — proving the cascade the function
  relies on. (The `auth.admin.deleteUser` path itself is a manual check against the live project, like the
  other auth-dependent checks already are.)

## 12. Caveats / decisions

- **The GitHub OAuth grant is the user's to remove — we just link them there.** We remove the email
  Supabase stored, but GitHub keeps its own account and Perch's OAuth *authorization*. We deliberately do
  **not** revoke that for them: GitHub's revoke API needs a live GitHub user token, which we'd have to hoard
  server-side to use — the opposite of what a privacy feature should do. Instead we show the exact-app
  deep-link `https://github.com/settings/connections/applications/<CLIENT_ID>`, which lands on Perch's
  "Revoke access" button. Building it needs the OAuth app's **`client_id`** in the app — it's **not secret**,
  so add it as a compiled-in/config value beside the Supabase URL + publishable key (`SupabaseConfig` /
  defaults). Revoking the grant only removes the authorization anyway, not the GitHub account.
- **In-progress games with a friend** cascade-delete (game + moves vanish for the other player too). Simpler
  and more complete than marking them abandoned; acceptable for a hobby feature. Revisit if it feels abrupt.
- **Retained anonymised reports** are a deliberate, disclosed exception (safety / legitimate interest).
- **No abuse vector**: the function only ever deletes the authenticated caller; there's no id to target
  someone else.

## 13. Milestones

- [x] **D0 (code)** — `functions/delete-account/index.ts` + `config.toml` entry. **Still owed (manual):**
  deploy to the live project and smoke-test with a puppet account (create, delete, confirm rows gone).
- [x] **D1** — pgTAP cascade test in `rls_test.sql` (tests 16–23); deletes `auth.users` to exercise the full
  chain. Whole suite runs 44/44 green (fixed pre-existing `throws_ok`/`now()` test bugs along the way).
- [x] **D2** — `ISocialClient.DeleteAccountAsync` + `SupabaseSocialClient` (POST + `ClearSession` refactor) +
  `FakeSocialClient`; unit tests. Full .NET suite 1173 green.
- [x] **D3** — Settings → Social "Danger zone" button + `DeleteAccountDialog` type-to-confirm + GitHub revoke
  deep-link (`AppInfo.SocialGitHubClientId`) + `AuthChanged` rebuild.
- [x] **D4** — `functions-deploy.yml` CI; "Account deletion" section in `backend/supabase/README.md` and a
  §7 checklist item in `social-feed-implementation.md`.
- [x] **D4a** — **`PRIVACY.md`** (§9): what we store, what deletion removes, and the exact retained
  anonymised-report shape; all placeholders filled.
- [x] **D5** — data export companion (§10): `ISocialClient.ExportMyDataAsync` (profile + own posts + own
  reactions + friend handles + blocked handles) in both clients, and a Settings → Social "Your data →
  Download my data (JSON)…" save-to-file. Tests added; full .NET suite 1176 green.

## 14. Follow-up: one current status per user (data minimisation)

Reviewing the export surfaced that we retained **all** past posts and reactions, though the product only
ever shows a user's **latest** status (the overlay roster; there is no history/feed view — `GetFeedAsync`
is internal to the roster computation and the debug tool only). Keeping the rest is data we use nowhere, so
a "status" is now a single current row per author.

- **Migration** `20260918120000_posts_current_only.sql`: an `AFTER INSERT` trigger (`posts_keep_latest`)
  deletes the author's other posts on each new one (reactions on the superseded status cascade away); a
  one-off backfill collapses existing history. Reactions on a replaced status disappearing is *correct* for
  a current-status model.
- **Flood guard** moves off the old rolling-minute row count (which relied on the history we removed) to a
  **minimum interval** (5s) between posts, tracked by a new **server-managed** `profiles.last_posted_at`
  (the authenticated UPDATE grant is narrowed to handle/display_name/mood so a client can't reset its own
  guard). `now()` is the transaction time, so real posts (one per transaction) space out by wall-seconds.
- **Fake** mirrors keep-latest (posting replaces the author's prior status); it does **not** simulate the
  interval (rate limiting was always server-only). **pgTAP** exercises both the interval reject and the
  keep-latest prune (46/46). **PRIVACY.md** §3.1 updated to say only the current status is kept.
