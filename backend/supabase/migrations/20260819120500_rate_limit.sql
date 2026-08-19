-- Perch Social — per-user post rate limit (M6)
-- The body length is already a table CHECK; this caps *frequency*, so a tampered client can't flood the feed.
-- A BEFORE INSERT trigger counts the author's recent posts and rejects once over the ceiling. SECURITY DEFINER
-- so the count sees all of the author's rows regardless of the caller's RLS. Enforced in the DB, not the UI —
-- the compose window's guard is only a courtesy. See docs/social-feed-implementation.md §7.

-- Ceiling: at most 10 posts in any rolling 60 seconds per author. Generous for a human, ruinous for a flood.
create or replace function public.enforce_post_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  recent integer;
begin
  select count(*) into recent
  from public.posts
  where author = new.author
    and created_at > now() - interval '1 minute';

  if recent >= 10 then
    raise exception 'Rate limit: too many posts in a short time. Please wait a moment.'
      using errcode = 'check_violation';
  end if;

  return new;
end;
$$;

drop trigger if exists posts_rate_limit on public.posts;
create trigger posts_rate_limit
  before insert on public.posts
  for each row execute function public.enforce_post_rate_limit();
