-- Perch Social -- security hardening (review fixes CP1 + CP2, docs/review-fixes-plan.md)
--
-- CP1: internal SECURITY DEFINER helpers were callable by any client.
--   Postgres grants EXECUTE on every new function to PUBLIC, and Supabase's default privileges add anon +
--   authenticated, so every function in `public` was reachable as POST /rest/v1/rpc/<name>. Nothing ever
--   revoked that, which exposed:
--     - cleanup_old_games / cleanup_old_draw_games: a global retention sweep. `older_than => '0 seconds'`
--       deleted every finished game for every user.
--     - are_friends(a,b) / shares_edge(a,b): who is connected to whom, for ANY two users.
--     - is_blocked(me,x): whether x blocked you.  is_suspended(u): moderation state.
--   The policy helpers can't simply be revoked -- RLS policies call them AS the querying role, so revoking
--   EXECUTE would break every read. Instead they move to a `private` schema that PostgREST doesn't expose
--   (the Data API serves `public` + `graphql_public` only). Policies reference functions by OID, so
--   ALTER FUNCTION ... SET SCHEMA carries every policy along untouched. SQL-language bodies are resolved by
--   NAME at run time, so the functions that call a moved helper are re-created below with the new names.
--   The sweeps and connect4_has_win are only ever called by their owner (pg_cron / the drop_disc SECURITY
--   DEFINER RPC), so they are revoked outright.
--
-- CP2: friendship consent.
--   friendships_request only checked requester = auth.uid(), and friendships_respond had USING but no WITH
--   CHECK, with a full-table UPDATE grant. A requester could therefore INSERT an edge as 'accepted', or PATCH
--   their own pending request to 'accepted', and become the victim's friend without consent (reading their
--   posts/profile and challenging them to games). Now:
--     - insert: only as the requester, only 'pending'.
--     - update: only the ADDRESSEE, only pending -> accepted.
--     - a BEFORE UPDATE trigger makes the parties and created_at immutable, so the addressee can't re-point
--       an edge at someone else while accepting.
--   The UPDATE grant is kept (not narrowed to a column grant) so already-shipped clients, whose friend
--   request is a PostgREST upsert needing UPDATE privilege, keep working.
--   NOTE: edges forged before this migration are indistinguishable from real ones (there is no accepted_at /
--   accepted_by audit column), so this migration cannot repair them.

-- == CP1: private schema for policy helpers ===========================================================
create schema if not exists private;
revoke all on schema private from public;
grant usage on schema private to authenticated;

-- Move the helpers (guarded so a re-run is a no-op). OIDs are preserved, so the policies that use them
-- (posts_read, profiles_friends_select, reactions_*, games_create, game_requests, draw_requests_create)
-- keep working unchanged.
do $$
declare f text;
begin
  foreach f in array array[
    'are_friends(uuid, uuid)',
    'is_blocked(uuid, uuid)',
    'shares_edge(uuid, uuid)',
    'is_suspended(uuid)',
    'can_see_post(uuid)'
  ] loop
    if to_regprocedure('public.' || f) is not null then
      execute format('alter function public.%s set schema private', f);
    end if;
  end loop;
end $$;

-- Re-point the bodies that called a moved helper by name (create or replace keeps each OID).
create or replace function private.are_friends(a uuid, b uuid)
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
  and not private.is_blocked(a, b);
$$;

create or replace function private.can_see_post(pid uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.posts p
    where p.id = pid
      and (p.author = auth.uid() or private.are_friends(auth.uid(), p.author))
      and not private.is_suspended(p.author)
  );
$$;

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
    and not private.is_suspended(p.id)
  limit 1;
$$;

create or replace function public.block_suspended_posts()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  if private.is_suspended(new.author) then
    raise exception 'This account is suspended.' using errcode = 'insufficient_privilege';
  end if;
  return new;
end;
$$;

-- Policies evaluate as the signed-in user, so `authenticated` keeps EXECUTE; nobody else needs it.
revoke execute on function
  private.are_friends(uuid, uuid),
  private.is_blocked(uuid, uuid),
  private.shares_edge(uuid, uuid),
  private.is_suspended(uuid),
  private.can_see_post(uuid)
from public, anon;
grant execute on function
  private.are_friends(uuid, uuid),
  private.is_blocked(uuid, uuid),
  private.shares_edge(uuid, uuid),
  private.is_suspended(uuid),
  private.can_see_post(uuid)
to authenticated;

-- == CP1: owner-only functions -- revoke from every client role ========================================
-- cleanup_* run from pg_cron as the owner; connect4_has_win runs inside drop_disc (SECURITY DEFINER, so as
-- the owner). The owner always retains EXECUTE; service_role keeps it for manual runs from the dashboard.
revoke execute on function
  public.cleanup_old_games(interval),
  public.cleanup_old_draw_games(interval),
  public.connect4_has_win(uuid)
from public, anon, authenticated;
grant execute on function
  public.cleanup_old_games(interval),
  public.cleanup_old_draw_games(interval),
  public.connect4_has_win(uuid)
to service_role;

-- == CP1: client RPCs are for signed-in users only ======================================================
-- Social requires sign-in, but the PUBLIC default let the bare publishable (anon) key call these too --
-- e.g. find_profile let anyone enumerate handles without an account. Each already has an explicit grant to
-- `authenticated` (rls.sql, block_report.sql, connect4*.sql, draw_with_perch.sql), so dropping PUBLIC/anon
-- changes nothing for the app. (Extension functions in `public`, e.g. citext's, are deliberately untouched.)
revoke execute on function
  public.find_profile(text),
  public.list_blocked(),
  public.drop_disc(uuid, int),
  public.resign_game(uuid),
  public.accept_game_request(uuid),
  public.accept_draw_request(uuid),
  public.submit_draw_round(uuid, public.draw_difficulty, text, text, text),
  public.submit_draw_guess(uuid, text),
  public.give_up_draw_round(uuid),
  public.resign_draw_game(uuid)
from public, anon;

-- == CP2: friendship consent ===========================================================================
-- Request: only as yourself, only pending.
drop policy if exists friendships_request on public.friendships;
create policy friendships_request on public.friendships
  for insert with check (requester = auth.uid() and status = 'pending');

-- Respond: only the addressee, only pending -> accepted. (Declining is a DELETE, via friendships_delete.)
drop policy if exists friendships_respond on public.friendships;
create policy friendships_respond on public.friendships
  for update
  using      (addressee = auth.uid() and status = 'pending')
  with check (addressee = auth.uid() and status = 'accepted');

-- The parties of an edge never change: without this the addressee could accept while re-pointing the
-- requester at a third party, forging an accepted edge with someone who never asked.
create or replace function private.friendships_guard()
returns trigger
language plpgsql
set search_path = public
as $$
begin
  if new.requester  is distinct from old.requester
  or new.addressee  is distinct from old.addressee
  or new.created_at is distinct from old.created_at then
    raise exception 'A friendship''s parties cannot be changed.' using errcode = 'insufficient_privilege';
  end if;
  return new;
end;
$$;

drop trigger if exists friendships_guard on public.friendships;
create trigger friendships_guard
  before update on public.friendships
  for each row execute function private.friendships_guard();
