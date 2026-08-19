-- Perch Social — moderation kill-switch (M6)
-- A lever moderation (service_role) can pull to pull a handle's content and stop it posting, without deleting
-- the account. It lives in its own table with NO policies, so the `authenticated` role can neither read nor
-- write it — a user can't discover or lift their own suspension. The SECURITY DEFINER helpers below read it as
-- the table owner (RLS-independent), and the posts policies consult them. Set a suspension from the SQL editor:
--   insert into moderation (profile, note) values ('<uuid>', 'reason')
--     on conflict (profile) do update set suspended = true, note = excluded.note, updated_at = now();
-- Lift it with:  update moderation set suspended = false, updated_at = now() where profile = '<uuid>';
-- See docs/social-feed-implementation.md §7.

create table if not exists public.moderation (
  profile    uuid primary key references public.profiles(id) on delete cascade,
  suspended  boolean not null default true,
  note       text,
  updated_at timestamptz not null default now()
);

-- RLS on, but deliberately NO policies and NO grants to authenticated: only service_role (which bypasses RLS)
-- can touch this table. is_suspended() reads it via SECURITY DEFINER for the policies.
alter table public.moderation enable row level security;

create or replace function public.is_suspended(u uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (select 1 from public.moderation where profile = u and suspended);
$$;

grant execute on function public.is_suspended(uuid) to authenticated;

-- ── enforce the switch ──────────────────────────────────────────────────────────

-- 1) A suspended author's posts vanish for everyone (including themselves).
drop policy if exists posts_read on public.posts;
create policy posts_read on public.posts
  for select using (
    (author = auth.uid() or public.are_friends(auth.uid(), author))
    and not public.is_suspended(author)
  );

-- 2) A suspended author can't create new posts. Reuse the rate-limit trigger point with a second BEFORE INSERT
--    trigger so the two concerns stay independent.
create or replace function public.block_suspended_posts()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  if public.is_suspended(new.author) then
    raise exception 'This account is suspended.' using errcode = 'insufficient_privilege';
  end if;
  return new;
end;
$$;

drop trigger if exists posts_suspended on public.posts;
create trigger posts_suspended
  before insert on public.posts
  for each row execute function public.block_suspended_posts();

-- 3) A suspended handle can't be found or added.
create or replace function public.find_profile(q text)
returns table (id uuid, handle citext, display_name text, mood_emoji text)
language sql
stable
security definer
set search_path = public
as $$
  select p.id, p.handle, p.display_name, p.mood_emoji
  from public.profiles p
  where p.handle = lower(q)
    and not public.is_suspended(p.id)
  limit 1;
$$;
