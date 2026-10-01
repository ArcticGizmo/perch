-- Perch Social -- block + suspension coverage, find_profile throttle (review fixes CP6, docs/review-fixes-plan.md)
--
-- The problem:
--   - Blocks only gated posts (through are_friends). shares_edge ignored them, so a blocked user kept reading
--     the blocker's profile (handle, name, mood) through any friendship edge, even a fresh pending request.
--     The friendship row itself stayed visible to both, so the blocked side would see the blocker as a friend.
--     Shared games carried on, and the game RPCs never looked at blocks.
--   - Suspension only stopped posting and find_profile. A suspended account could still react, edit its
--     profile, send friend requests and game invites, and play.
--   - find_profile had no throttle, so a signed-in account could enumerate handles as fast as it liked.
--
-- The fix:
--   1. Blocks hide the whole relationship, from both sides, for as long as the block stands: shares_edge and the
--      friendships SELECT policy ignore a blocked pair, so to the blocked person a block looks like an unfriend.
--      Nothing refuses visibly -- a blocked user's new friend request still "succeeds" (CP2), it just shows
--      nothing to either side. Unblocking restores the edge as it was.
--   2. Blocking ends shared play: an AFTER INSERT trigger on blocks abandons the pair's in-progress Connect 4 and
--      Draw games and deletes their pending invites. Rows that predate this migration are brought into line
--      below.
--   3. One guard trigger on games and draw_games refuses any write that leaves a game in progress between a
--      blocked pair, or that a suspended account makes. Every RPC that creates or advances a game (accept_*,
--      drop_disc, submit_draw_round, submit_draw_guess, give_up_draw_round) writes that row, so the guard covers
--      them all -- and any future RPC -- without re-creating each one. Resigning (which ends the game) and the
--      block trigger's own abandonment pass through.
--   4. Suspension also stops reacting, editing your profile, and sending friend requests and game invites.
--      Blocking and reporting stay available: they're safety tools.
--   5. find_profile is throttled per caller (20 lookups per 10 minutes). It keeps returning the id: shipped
--      clients need it to send a friend request, and since CP3/CP4 an id unlocks nothing on its own. It becomes
--      VOLATILE plpgsql, since it now writes (PostgREST runs STABLE functions in a read-only transaction).

-- == 1. blocks hide the relationship ====================================================================
create or replace function private.shares_edge(a uuid, b uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.friendships
    where (requester = a and addressee = b)
       or (requester = b and addressee = a)
  )
  and not private.is_blocked(a, b);
$$;

alter policy friendships_party_select on public.friendships
  using ((requester = auth.uid() or addressee = auth.uid()) and not private.is_blocked(requester, addressee));

-- == 2. blocking ends shared play =======================================================================
create or replace function private.end_play_on_block()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
  update public.games set status = 'abandoned'
   where status = 'in_progress'
     and ((player_red = new.blocker and player_yellow = new.blocked)
       or (player_red = new.blocked and player_yellow = new.blocker));
  update public.draw_games set status = 'abandoned'
   where status = 'in_progress'
     and ((player_a = new.blocker and player_b = new.blocked)
       or (player_a = new.blocked and player_b = new.blocker));
  delete from public.game_requests
   where (requester = new.blocker and addressee = new.blocked)
      or (requester = new.blocked and addressee = new.blocker);
  delete from public.draw_requests
   where (requester = new.blocker and addressee = new.blocked)
      or (requester = new.blocked and addressee = new.blocker);
  return null;   -- AFTER trigger: return value is ignored
end;
$$;

drop trigger if exists blocks_end_play on public.blocks;
create trigger blocks_end_play
  after insert on public.blocks
  for each row execute function private.end_play_on_block();

-- Blocks that already exist: end their play the same way.
update public.games g set status = 'abandoned'
 where g.status = 'in_progress' and private.is_blocked(g.player_red, g.player_yellow);
update public.draw_games g set status = 'abandoned'
 where g.status = 'in_progress' and private.is_blocked(g.player_a, g.player_b);
delete from public.game_requests r where private.is_blocked(r.requester, r.addressee);
delete from public.draw_requests r where private.is_blocked(r.requester, r.addressee);

-- == 3. the game-write guard ============================================================================
-- Fires on every insert/update of a games or draw_games row that is (still) in progress. The block check keeps
-- a generic "game is over" message: by the time it can fire, the block trigger has already ended the game.
create or replace function private.guard_game_write()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
  a uuid;
  b uuid;
begin
  if new.status::text <> 'in_progress' then return new; end if;

  if tg_table_name = 'games' then
    a := new.player_red;  b := new.player_yellow;
  else
    a := new.player_a;    b := new.player_b;
  end if;

  if private.is_suspended(auth.uid()) then
    raise exception 'This account is suspended.' using errcode = 'insufficient_privilege';
  end if;
  if private.is_blocked(a, b) then
    raise exception 'game is over' using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

drop trigger if exists guard_game_write on public.games;
create trigger guard_game_write
  before insert or update on public.games
  for each row execute function private.guard_game_write();

drop trigger if exists guard_game_write on public.draw_games;
create trigger guard_game_write
  before insert or update on public.draw_games
  for each row execute function private.guard_game_write();

-- Functions in `private` still get Postgres' PUBLIC EXECUTE default; a trigger fires without it.
revoke all on function private.end_play_on_block(), private.guard_game_write()
  from public, anon, authenticated, service_role;

-- == 4. suspension covers every write a user makes ======================================================
-- ALTER POLICY keeps each policy's TO authenticated. Only WITH CHECK changes, so a refused write is an explicit
-- 42501 rather than a silent no-op.
alter policy reactions_write on public.reactions
  with check (reactor = auth.uid() and private.can_see_post(post_id) and not private.is_suspended(auth.uid()));

alter policy profiles_self_update on public.profiles
  with check (id = auth.uid() and not private.is_suspended(auth.uid()));

alter policy friendships_request on public.friendships
  with check (requester = auth.uid() and status = 'pending' and not private.is_suspended(auth.uid()));

alter policy game_requests_create on public.game_requests
  with check (requester = auth.uid() and addressee <> auth.uid()
              and private.are_friends(auth.uid(), addressee) and not private.is_suspended(auth.uid()));

alter policy draw_requests_create on public.draw_requests
  with check (requester = auth.uid() and addressee <> auth.uid()
              and private.are_friends(auth.uid(), addressee) and not private.is_suspended(auth.uid()));

-- == 5. find_profile throttle ===========================================================================
-- Keyed on the auth user, not the profile: a signed-in account may look someone up before claiming a handle.
create table if not exists private.find_profile_throttle (
  profile      uuid primary key references auth.users(id) on delete cascade,
  window_start timestamptz not null,
  calls        int not null
);
alter table private.find_profile_throttle enable row level security;   -- no policies: defence in depth behind no grants
revoke all on table private.find_profile_throttle from public, anon, authenticated, service_role;

-- A refused call rolls its own increment back, so the count rests at the limit until the window expires.
create or replace function public.find_profile(q text)
returns table (id uuid, handle citext, display_name text, mood_emoji text)
language plpgsql
volatile
security definer
set search_path = public
as $$
#variable_conflict use_column
declare
  me uuid := auth.uid();
  n  int;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

  insert into private.find_profile_throttle as t (profile, window_start, calls)
    values (me, now(), 1)
  on conflict (profile) do update
    set window_start = case when t.window_start <= now() - interval '10 minutes' then now() else t.window_start end,
        calls        = case when t.window_start <= now() - interval '10 minutes' then 1     else t.calls + 1    end
  returning t.calls into n;

  if n > 20 then
    raise exception 'Rate limit: too many lookups. Try again in a few minutes.' using errcode = 'check_violation';
  end if;

  return query
    select p.id, p.handle, p.display_name, p.mood_emoji
    from public.profiles p
    where p.handle = lower(q)
      and not private.is_suspended(p.id)
    limit 1;
end;
$$;
-- create or replace keeps the ACL (authenticated only, CP1/CP3); restate it so this file stands on its own.
revoke all on function public.find_profile(text) from public, anon, service_role;
grant execute on function public.find_profile(text) to authenticated;
