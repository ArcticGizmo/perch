-- Perch Social — one current status per user (data minimisation)
-- The product only ever surfaces a user's LATEST status (the overlay roster; there is no history/feed view),
-- so retaining every past post and its reactions was data we use nowhere. A "status" becomes a single
-- current row per author: posting replaces the previous one, and reactions on the superseded status cascade
-- away with it. Flood protection moves off the old "count posts in a rolling minute" (which relied on the
-- history we're removing) to a minimum interval between posts, tracked by profiles.last_posted_at.
-- See docs/social-account-deletion-plan.md and the repo-root PRIVACY.md.

-- ── last_posted_at (server-managed) ──────────────────────────────────────────────
alter table public.profiles add column if not exists last_posted_at timestamptz;

-- Clients must not write it — otherwise a tampered client could reset its own flood guard. Narrow the
-- authenticated UPDATE grant on profiles to the genuinely user-editable columns; the post trigger below
-- writes last_posted_at as the (SECURITY DEFINER) table owner, which the grant doesn't constrain. The
-- handle upsert only ever writes handle/display_name/mood_emoji, so it is unaffected.
revoke update on public.profiles from authenticated;
grant update (handle, display_name, mood_emoji) on public.profiles to authenticated;

-- ── collapse any existing history to the current status per author ───────────────
-- Keep only the newest post per author (id as a deterministic tiebreak); reactions on the rest cascade.
delete from public.posts p
using (
  select id, row_number() over (partition by author order by created_at desc, id desc) as rn
  from public.posts
) ranked
where p.id = ranked.id and ranked.rn > 1;

-- Seed last_posted_at from each author's surviving (latest) post.
update public.profiles pr
set last_posted_at = p.created_at
from public.posts p
where p.author = pr.id;

-- ── flood protection: a minimum interval between posts ───────────────────────────
-- Replaces the old rolling-minute counter (which counted rows we no longer keep). Rejects a post that lands
-- within the interval of the author's previous one. now() is the transaction time, so in production (one
-- post per transaction) it advances by real seconds between posts; a burst inside a single transaction is
-- (correctly) rejected. SECURITY DEFINER so it reads profiles regardless of the caller's RLS.
create or replace function public.enforce_post_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  last_at timestamptz;
begin
  select last_posted_at into last_at from public.profiles where id = new.author;
  if last_at is not null and now() - last_at < interval '5 seconds' then
    raise exception 'Rate limit: you are posting too quickly. Please wait a few seconds.'
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;
-- (the existing BEFORE INSERT trigger posts_rate_limit already calls this function)

-- ── keep only the latest post per author, and stamp last_posted_at ───────────────
-- AFTER INSERT: delete the author's other posts (reactions on them cascade) and record the post time as the
-- flood-guard timestamp. Deleting posts here can't recurse — this is an INSERT trigger, not a DELETE one.
create or replace function public.posts_keep_latest()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  delete from public.posts where author = new.author and id <> new.id;
  update public.profiles set last_posted_at = new.created_at where id = new.author;
  return null;   -- AFTER trigger: return value is ignored
end;
$$;

drop trigger if exists posts_keep_latest on public.posts;
create trigger posts_keep_latest
  after insert on public.posts
  for each row execute function public.posts_keep_latest();
