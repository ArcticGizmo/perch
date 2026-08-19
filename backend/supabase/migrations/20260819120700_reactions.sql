-- Perch Social — reactions (UI feature)
-- A friend can react to a status you can see, with an emoji. Reactions ride the same friends-only visibility
-- as the posts they hang off: you can read a reaction only if you can read its post, and you can only react to
-- a post you can see, as yourself. See docs/social-feed-implementation.md.

create table if not exists public.reactions (
  post_id    uuid not null references public.posts(id) on delete cascade,
  reactor    uuid not null references public.profiles(id) on delete cascade,
  emoji      text not null check (char_length(emoji) between 1 and 16),
  created_at timestamptz not null default now(),
  primary key (post_id, reactor, emoji)   -- one of each emoji per person per post
);
create index if not exists reactions_post on public.reactions (post_id);

-- Can the caller see this post? Mirrors posts_read (own or accepted-friend's, block/suspension aware) via the
-- helpers those policies use. SECURITY DEFINER so the reactions policies can consult it without re-tripping
-- posts' own RLS in a correlated subquery.
create or replace function public.can_see_post(pid uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.posts p
    where p.id = pid
      and (p.author = auth.uid() or public.are_friends(auth.uid(), p.author))
      and not public.is_suspended(p.author)
  );
$$;

grant execute on function public.can_see_post(uuid) to authenticated;

alter table public.reactions enable row level security;

-- Read a reaction if you can see its post (your own reactions are on posts you can see too).
drop policy if exists reactions_read on public.reactions;
create policy reactions_read on public.reactions
  for select using (public.can_see_post(post_id));

-- React only AS yourself, and only to a post you can see.
drop policy if exists reactions_write on public.reactions;
create policy reactions_write on public.reactions
  for insert with check (reactor = auth.uid() and public.can_see_post(post_id));

-- Remove only your own reaction.
drop policy if exists reactions_delete on public.reactions;
create policy reactions_delete on public.reactions
  for delete using (reactor = auth.uid());

grant select, insert, delete on public.reactions to authenticated;
