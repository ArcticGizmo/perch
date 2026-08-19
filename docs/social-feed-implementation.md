# Perch Social Feed — implementation sketch (Supabase path)

> Companion to `social-feed-plan.md` (the options assessment). This is the concrete build:
> data model, RLS, auth flow, the `Perch.Core` interfaces, UI wiring, and phasing. Chosen stack:
> **Supabase** (Postgres + Auth + Realtime + RLS), per the assessment's recommendation for a
> private, threat-aware, .NET, hobby-effort feature.

## Guiding constraints (from the project + the threat model)

- **Every OS/network capability behind a `Perch.Core` interface**, resolved via `PlatformServices`
  (`ISocialClient`, `ISecretStore`) — the macOS head shares them.
- **Never trust the client.** The publishable key is public *by design*; **RLS is the security boundary.**
  The `service_role` key never ships in the app — anything privileged runs in an Edge Function.
- **Minimal PII.** Handle + optional display name only. **Never ingest anything from `~/.claude`**
  (paths, project names, transcripts) into a post. Posting is always explicit and manual.
- **Owner-drawn UI through `OverlayDraw`** (text height from line-height, never a magic number),
  IO off the UI thread, single reused windows via `WindowHost.ShowOrFocus`.

## 1. Data model (Postgres)

```sql
-- A public-facing identity. One row per auth user.
create table profiles (
  id           uuid primary key references auth.users on delete cascade,
  handle       citext unique not null check (handle ~ '^[a-z0-9_]{3,20}$'),
  display_name text check (char_length(display_name) <= 40),
  mood_emoji   text,                       -- current status glyph
  created_at   timestamptz not null default now()
);

-- Friend edges. status: pending -> accepted, or blocked. One row per ordered pair.
create type friend_status as enum ('pending', 'accepted', 'blocked');
create table friendships (
  requester  uuid not null references profiles(id) on delete cascade,
  addressee  uuid not null references profiles(id) on delete cascade,
  status     friend_status not null default 'pending',
  created_at timestamptz not null default now(),
  primary key (requester, addressee),
  check (requester <> addressee)
);

-- Status posts. Body hard-capped; no rich content in phase 1.
create table posts (
  id         uuid primary key default gen_random_uuid(),
  author     uuid not null references profiles(id) on delete cascade,
  body       text not null check (char_length(body) between 1 and 280),
  mood_emoji text,
  created_at timestamptz not null default now()
);
create index posts_author_created on posts (author, created_at desc);
```

## 2. Authorization — RLS is the whole ballgame

```sql
alter table profiles    enable row level security;
alter table friendships enable row level security;
alter table posts       enable row level security;

-- Helper: are A and B accepted friends? (edge stored either direction)
create function are_friends(a uuid, b uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from friendships
    where status = 'accepted'
      and ((requester = a and addressee = b) or (requester = b and addressee = a))
  );
$$;

-- POSTS: read your own or an accepted friend's; write only as yourself.
create policy posts_read on posts for select using (
  author = auth.uid() or are_friends(auth.uid(), author)
);
create policy posts_write on posts for insert with check (author = auth.uid());
create policy posts_delete on posts for delete using (author = auth.uid());

-- FRIENDSHIPS: visible only to the two parties.
create policy fr_read on friendships for select using (
  requester = auth.uid() or addressee = auth.uid()
);
-- You may only create a request AS the requester.
create policy fr_request on friendships for insert with check (requester = auth.uid());
-- Only the addressee flips pending -> accepted; either party may block.
create policy fr_respond on friendships for update using (
  addressee = auth.uid() or requester = auth.uid()
);

-- PROFILES: your own row is fully readable; others only via the search RPC below,
-- so the table itself is NOT open to blanket SELECT (prevents user enumeration).
create policy profiles_self on profiles for select using (id = auth.uid());
create policy profiles_upsert on profiles for insert with check (id = auth.uid());
create policy profiles_update on profiles for update using (id = auth.uid());
```

**Finding friends without exposing the whole table** — a rate-limited RPC returns only a handle
match, not a browsable list:

```sql
create function find_profile(q text) returns table (id uuid, handle citext, display_name text)
language sql stable security definer set search_path = public as $$
  select id, handle, display_name from profiles
  where handle = lower(q)          -- exact handle only: you must know it to add someone
  limit 1;
$$;
```

> Threat notes baked in: exact-handle lookup (no `LIKE '%q%'` enumeration); posts unreadable
> unless friendship is *accepted*; friendship rows invisible to third parties; body length is a
> DB `CHECK`, not client-side. Add per-user insert rate-limiting in an Edge Function or via a
> `posts` trigger counting recent rows if abuse appears.

## 3. Auth flow (desktop OAuth, no password)

GitHub OAuth via the system browser + loopback redirect (PKCE):

1. App starts a one-shot `HttpListener` on `http://127.0.0.1:<port>/callback`.
2. Opens the Supabase GitHub authorize URL in the default browser.
3. User approves in the browser; Supabase redirects to the loopback with the code.
4. App exchanges the code (PKCE) for an access + refresh token.
5. **Refresh token stored via `ISecretStore`** (Windows: DPAPI / Credential Manager; macOS:
   Keychain) — never in `AppSettings`/plaintext.
6. First run: prompt to **claim a handle** (writes the `profiles` row).

## 4. `Perch.Core` interfaces (new)

```csharp
namespace Perch.Social;

public interface ISocialClient
{
    Task<AuthState>      SignInAsync(CancellationToken ct);   // launches OAuth, stores tokens
    Task                 SignOutAsync();
    Task<Profile?>       GetMeAsync(CancellationToken ct);
    Task                 ClaimHandleAsync(string handle, string? displayName, CancellationToken ct);

    Task<Profile?>       FindByHandleAsync(string handle, CancellationToken ct);
    Task                 SendRequestAsync(Guid addressee, CancellationToken ct);
    Task                 RespondAsync(Guid requester, bool accept, CancellationToken ct);
    Task<IReadOnlyList<Friend>> GetFriendsAsync(CancellationToken ct);

    Task<PostId>         PostAsync(string body, string? mood, CancellationToken ct);
    Task<IReadOnlyList<FeedItem>> GetFeedAsync(int limit, CancellationToken ct);

    IDisposable          SubscribeFeed(Action<FeedItem> onPost);   // Realtime; no-op-safe
}

// Secret storage abstraction (also useful beyond social)
public interface ISecretStore
{
    void   Set(string key, string value);
    string? Get(string key);
    void   Delete(string key);
}
```

- Implementation `SupabaseSocialClient` lives in **`Perch.Core`** (no UI, no OS) — a thin
  `HttpClient` PostgREST client + a WebSocket for Realtime. Prefer raw REST over the community
  `supabase-csharp` to keep dependencies lean; RLS means the client only ever sees permitted rows.
- `ISecretStore` gets a `Perch.Platform.Windows` impl (DPAPI/`CredWrite`) and a `Perch.Platform.Mac`
  Keychain stub, wired in `PlatformServices`.
- All calls run **off the UI thread** (`Task.Run` → `Dispatcher.UIThread.Post`), guarded against
  a window closed mid-flight, exceptions swallowed — the established `*MonitorHost` pattern.

## 5. UI wiring

- **`FeedStrip`** — a new owner-drawn control under the overlay: friend avatar/emoji + handle +
  status + relative time, newest first, a few rows. Sizes every text rect from
  `OverlayDraw.Text(...).Height` (the stat-card clipping gotcha). Gated on/off through
  `OverlaySettingsGates.Apply`.
- **Compose** — a small window/popup (mood emoji + 280-char box + Post button), opened from the
  overlay header menu. Explicit action only.
- **Friends window** — search by handle, pending requests (accept/decline), friends list, block —
  reused via `WindowHost.ShowOrFocus`, closed in `App.CloseAuxWindows`.
- **Notifications** — reuse `INotifier` for "alice just posted" (respecting existing quiet/DND
  settings).
- **Settings** — add `AppSettings` props (`SocialEnabled`, cached `Handle`, `FeedPlacement`,
  `NotifyOnFriendPost`) **each with a `SettingsRegistry` descriptor** (the coverage test enforces
  it); the feed strip needs a `PreviewTarget` + an `OverlaySettingsGates` line.

## 6. Phasing

| Phase | Scope | Ships |
|-------|-------|-------|
| **0 · Foundation** | Supabase project, schema + RLS, GitHub OAuth in-app, `ISecretStore`, handle claim | You can sign in and exist |
| **1 · Feed (polling)** | Friends (find/request/accept), post, feed via 60s poll, Friends window | The whole feature, minus liveness |
| **2 · Live + overlay** | Realtime subscription w/ fallback, `FeedStrip` under overlay, "X posted" notifications | It feels live |
| **3 · Safety + polish** | Block/report, per-user rate-limit Edge Function, moods/reactions, moderation kill-switch | Hardened |

Ship **Phase 0–1 first** to prove the auth/authz model before any real-time or UI polish. Every
step stays behind `ISocialClient`, so a macOS head implements the same contract.

## 7. Security checklist (pre-launch)

- [ ] RLS enabled on **every** table; a test per policy (a non-friend cannot read posts).
- [ ] `service_role` key exists only server-side (Edge Functions); app ships the publishable key only.
- [ ] Body length enforced by DB `CHECK`; handle format enforced by `CHECK`.
- [ ] Friend discovery is exact-handle only (no enumeration); friendship rows invisible to third parties.
- [ ] Refresh token in `ISecretStore`, never plaintext.
- [ ] Per-user post rate limit; block + server-side delete available.
- [ ] Nothing from `~/.claude` is ever auto-posted; compose is manual and explicit.
- [ ] TLS/HTTPS only; transport degrades WS → SSE → long-poll for firewalled users.
