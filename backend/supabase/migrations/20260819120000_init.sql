-- Perch Social — schema (M0)
-- Tables, enums, indexes and the helper functions the RLS policies (next migration) build on.
-- Applies to a fresh Supabase project. See docs/social-feed-implementation.md.

-- citext gives case-insensitive unique handles (@Ada == @ada) without lower() gymnastics.
create extension if not exists citext;

-- ── profiles ────────────────────────────────────────────────────────────────
-- One row per auth user. Deliberately minimal: a handle, an optional display name and mood.
-- No real name, no email (that stays with the auth provider).
create table if not exists public.profiles (
  id           uuid primary key references auth.users on delete cascade,
  handle       citext unique not null check (handle ~ '^[a-z0-9_]{3,20}$'),
  display_name text check (display_name is null or char_length(display_name) <= 40),
  mood_emoji   text check (mood_emoji is null or char_length(mood_emoji) <= 16),
  created_at   timestamptz not null default now()
);

-- ── friendships ─────────────────────────────────────────────────────────────
-- One row per ordered (requester, addressee) pair. status walks pending -> accepted,
-- or blocked. Visibility of posts keys off an *accepted* edge in either direction.
do $$ begin
  create type public.friend_status as enum ('pending', 'accepted', 'blocked');
exception when duplicate_object then null; end $$;

create table if not exists public.friendships (
  requester  uuid not null references public.profiles(id) on delete cascade,
  addressee  uuid not null references public.profiles(id) on delete cascade,
  status     public.friend_status not null default 'pending',
  created_at timestamptz not null default now(),
  primary key (requester, addressee),
  check (requester <> addressee)
);
create index if not exists friendships_addressee on public.friendships (addressee);

-- ── posts ───────────────────────────────────────────────────────────────────
-- Status posts. Body hard-capped in the DB so a tampered client can't over-post.
create table if not exists public.posts (
  id         uuid primary key default gen_random_uuid(),
  author     uuid not null references public.profiles(id) on delete cascade,
  body       text not null check (char_length(body) between 1 and 280),
  mood_emoji text check (mood_emoji is null or char_length(mood_emoji) <= 16),
  created_at timestamptz not null default now()
);
create index if not exists posts_author_created on public.posts (author, created_at desc);

-- ── helper functions ──────────────────────────────────────────────────────────

-- Are A and B accepted friends? Edge may be stored in either direction. SECURITY DEFINER so
-- the check itself isn't re-filtered by the caller's RLS (it reads friendships as the owner).
create or replace function public.are_friends(a uuid, b uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.friendships
    where status = 'accepted'
      and ((requester = a and addressee = b)
        or (requester = b and addressee = a))
  );
$$;

-- Exact-handle lookup only — you must know a handle to add someone; there is no browsable
-- directory to enumerate. Returns the minimal fields the "add friend" flow needs.
create or replace function public.find_profile(q text)
returns table (id uuid, handle citext, display_name text, mood_emoji text)
language sql
stable
security definer
set search_path = public
as $$
  select id, handle, display_name, mood_emoji
  from public.profiles
  where handle = lower(q)
  limit 1;
$$;
