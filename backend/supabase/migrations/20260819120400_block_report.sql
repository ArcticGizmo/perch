-- Perch Social — block + report (M6)
-- Blocking is the user-facing safety valve: a one-sided, private edge that kills post visibility in BOTH
-- directions regardless of any friendship. It lives in its own table (not the friendships enum) so the blocked
-- user can never undo it — friendships_respond lets either party update an edge, which would let a blocked
-- person un-block themselves. Reporting is write-only: it lands in a table only moderation (service_role) reads.
-- See docs/social-feed-implementation.md §7.

-- ── blocks ────────────────────────────────────────────────────────────────────
-- One row per (blocker, blocked). Only the blocker may see or manage their own rows — nobody can discover who
-- blocked them.
create table if not exists public.blocks (
  blocker    uuid not null references public.profiles(id) on delete cascade,
  blocked    uuid not null references public.profiles(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key (blocker, blocked),
  check (blocker <> blocked)
);

alter table public.blocks enable row level security;

drop policy if exists blocks_self_select on public.blocks;
create policy blocks_self_select on public.blocks
  for select using (blocker = auth.uid());

drop policy if exists blocks_self_insert on public.blocks;
create policy blocks_self_insert on public.blocks
  for insert with check (blocker = auth.uid());

drop policy if exists blocks_self_delete on public.blocks;
create policy blocks_self_delete on public.blocks
  for delete using (blocker = auth.uid());

grant select, insert, delete on public.blocks to authenticated;

-- ── reports ───────────────────────────────────────────────────────────────────
-- Write-only for users: you can file a report, but there is NO select policy, so only service_role (the
-- moderator, in the dashboard) can read the queue. reporter is nulled if the reporter deletes their account,
-- keeping the report for moderation.
create table if not exists public.reports (
  id         uuid primary key default gen_random_uuid(),
  reporter   uuid references public.profiles(id) on delete set null,
  reported   uuid not null references public.profiles(id) on delete cascade,
  reason     text check (reason is null or char_length(reason) <= 500),
  created_at timestamptz not null default now()
);
create index if not exists reports_reported on public.reports (reported);

alter table public.reports enable row level security;

drop policy if exists reports_insert on public.reports;
create policy reports_insert on public.reports
  for insert with check (reporter = auth.uid());

grant insert on public.reports to authenticated;

-- ── visibility: fold blocking into are_friends ──────────────────────────────────
-- are_friends() is what posts_read keys off, so consulting blocks here makes a block hide posts in both
-- directions with no policy change. SECURITY DEFINER, so it reads blocks as the owner (RLS-independent).
create or replace function public.is_blocked(a uuid, b uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.blocks
    where (blocker = a and blocked = b)
       or (blocker = b and blocked = a)
  );
$$;

grant execute on function public.is_blocked(uuid, uuid) to authenticated;

-- Accepted friends, UNLESS either side has blocked the other.
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
  )
  and not public.is_blocked(a, b);
$$;

-- ── the caller's own block list (with handles) ──────────────────────────────────
-- A blocked stranger shares no friendship edge, so profiles_friends_select wouldn't expose their handle for
-- the "unblock" UI. This RPC returns ONLY the caller's blocked profiles.
create or replace function public.list_blocked()
returns table (id uuid, handle citext, display_name text, mood_emoji text)
language sql
stable
security definer
set search_path = public
as $$
  select p.id, p.handle, p.display_name, p.mood_emoji
  from public.blocks b
  join public.profiles p on p.id = b.blocked
  where b.blocker = auth.uid()
  order by p.handle;
$$;

grant execute on function public.list_blocked() to authenticated;
