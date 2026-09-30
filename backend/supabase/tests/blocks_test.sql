-- Perch -- block + suspension coverage and the find_profile throttle (pgTAP, review fixes CP6)
-- Proves, as the `authenticated` role with a simulated JWT sub:
--   - a block hides the whole relationship from both sides (profile and friendship row), ends shared play
--     (games abandoned, invites deleted), and nothing can restart it; a blocked user's friend request still
--     "succeeds" but shows nothing; unblocking restores the edge;
--   - a suspended account can't react, edit its profile, send friend requests or game invites, or play, but can
--     still block and report;
--   - find_profile is throttled per caller.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- NOTE: act_as sets the JWT claims transaction-locally, so they persist across `reset role`. Owner statements
-- that fire the game guard run while a non-suspended user's claims are current.

begin;
select plan(26);

-- == fixtures ======================================================================
-- alice-bob and alice-dave are accepted friends; carol and erin are strangers; dave gets suspended later.
insert into auth.users (id, email) values
  ('11111111-1111-1111-1111-111111111111', 'alice@example.com'),
  ('22222222-2222-2222-2222-222222222222', 'bob@example.com'),
  ('33333333-3333-3333-3333-333333333333', 'carol@example.com'),
  ('44444444-4444-4444-4444-444444444444', 'dave@example.com'),
  ('55555555-5555-5555-5555-555555555555', 'erin@example.com');

insert into public.profiles (id, handle) values
  ('11111111-1111-1111-1111-111111111111', 'alice'),
  ('22222222-2222-2222-2222-222222222222', 'bob'),
  ('33333333-3333-3333-3333-333333333333', 'carol'),
  ('44444444-4444-4444-4444-444444444444', 'dave'),
  ('55555555-5555-5555-5555-555555555555', 'erin');

insert into public.friendships (requester, addressee, status) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'accepted'),
  ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 'accepted');

create or replace function pg_temp.act_as(uid uuid) returns void
language plpgsql as $$
begin
  perform set_config('role', 'authenticated', true);
  perform set_config('request.jwt.claims', json_build_object('sub', uid, 'role', 'authenticated')::text, true);
end $$;

-- Shared play between alice and bob (created as the owner): a Connect 4 game, a Draw game, and one pending
-- invite of each kind from bob. Plus a Connect 4 game between alice and dave, on dave's (yellow's) turn.
insert into public.games (id, player_red, player_yellow, turn) values
  ('a0000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'yellow'),
  ('a0000000-0000-0000-0000-000000000002', '11111111-1111-1111-1111-111111111111', '44444444-4444-4444-4444-444444444444', 'yellow');
insert into public.draw_games (id, player_a, player_b, whose_turn, phase) values
  ('b0000000-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
   '11111111-1111-1111-1111-111111111111', 'draw');
insert into public.game_requests (requester, addressee) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111');
insert into public.draw_requests (requester, addressee, difficulty, word) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'easy', 'cat');

-- alice has a current status for dave to (try to) react to.
insert into public.posts (author, body) values ('11111111-1111-1111-1111-111111111111', 'hello');

-- 1) Before any block, bob reads alice's profile and their friendship row.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select is(
  (select count(*)::int from public.profiles where id = '11111111-1111-1111-1111-111111111111')
    + (select count(*)::int from public.friendships where requester = '22222222-2222-2222-2222-222222222222'),
  2, 'baseline: a friend reads the profile and the friendship row');
reset role;

-- == blocks ========================================================================
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice blocks bob
insert into public.blocks (blocker, blocked)
  values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');

-- 2) The blocker no longer sees the friendship row either (the relationship is hidden from both sides).
select is(
  (select count(*)::int from public.friendships where requester = '22222222-2222-2222-2222-222222222222'),
  0, 'CP6 block: the blocker no longer sees the friendship row');
reset role;

-- 3,4) The blocked user can't read the blocker's profile, nor see the friendship row (it looks like an unfriend).
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select is(
  (select count(*)::int from public.profiles where id = '11111111-1111-1111-1111-111111111111'),
  0, 'CP6 block: the blocked user cannot read the blocker''s profile');
select is(
  (select count(*)::int from public.friendships where addressee = '11111111-1111-1111-1111-111111111111'),
  0, 'CP6 block: the blocked user no longer sees the friendship row');
reset role;

-- 5,6) Shared play ended: both games abandoned, both pending invites gone.
select is(
  (select status::text from public.games where id = 'a0000000-0000-0000-0000-000000000001')
    || ':' || (select status::text from public.draw_games where id = 'b0000000-0000-0000-0000-000000000001'),
  'abandoned:abandoned', 'CP6 block: the pair''s Connect 4 and Draw games are abandoned');
select is(
  (select count(*)::int from public.game_requests where requester = '22222222-2222-2222-2222-222222222222')
    + (select count(*)::int from public.draw_requests where requester = '22222222-2222-2222-2222-222222222222'),
  0, 'CP6 block: the pair''s pending invites are deleted');

-- 7) The blocked user can't move in the old game.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.drop_disc('a0000000-0000-0000-0000-000000000001', 3)$$,
  '23514', NULL, 'CP6 block: the blocked user cannot move in the shared game');

-- 8) Their friend request still "succeeds" (no tell), exactly as the client sends it (ignore-duplicates)...
select lives_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'pending')
      on conflict do nothing$$,
  'CP6 block: a blocked user''s friend request does not fail visibly');
reset role;

-- 9-11) ...and so does a stranger's, but it opens nothing: alice blocks erin (no edge at all), erin sends a
-- request, and neither side can see it or the other's profile.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
insert into public.blocks (blocker, blocked)
  values ('11111111-1111-1111-1111-111111111111', '55555555-5555-5555-5555-555555555555');
reset role;
select pg_temp.act_as('55555555-5555-5555-5555-555555555555');
select lives_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('55555555-5555-5555-5555-555555555555', '11111111-1111-1111-1111-111111111111', 'pending')$$,
  'CP6 block: a blocked stranger''s request is accepted by the server');
select is(
  (select count(*)::int from public.profiles where id = '11111111-1111-1111-1111-111111111111'),
  0, 'CP6 block: the pending request does not expose the blocker''s profile');
reset role;
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select is(
  (select count(*)::int from public.friendships where requester = '55555555-5555-5555-5555-555555555555'),
  0, 'CP6 block: the blocker never sees the blocked user''s request');
reset role;

-- 12,13) An invite sneaked in past the policy (as the owner) still can't start a game: the guard refuses it.
insert into public.game_requests (id, requester, addressee) values
  ('c0000000-0000-0000-0000-000000000001', '22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111');
insert into public.draw_requests (id, requester, addressee, difficulty, word) values
  ('c0000000-0000-0000-0000-000000000002', '22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'easy', 'cat');
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.accept_game_request('c0000000-0000-0000-0000-000000000001')$$,
  '23514', NULL, 'CP6 block: accepting a Connect 4 invite from a blocked user is refused');
select throws_ok(
  $$select public.accept_draw_request('c0000000-0000-0000-0000-000000000002')$$,
  '23514', NULL, 'CP6 block: accepting a Draw invite from a blocked user is refused');
reset role;

-- 14) Not even the owner can put a blocked pair's game back in progress.
select throws_ok(
  $$update public.games set status = 'in_progress' where id = 'a0000000-0000-0000-0000-000000000001'$$,
  '23514', NULL, 'CP6 block: no game can be in progress between a blocked pair');

-- 15) Unblocking restores the edge as it was.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
delete from public.blocks where blocked = '22222222-2222-2222-2222-222222222222';
reset role;
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select is(
  (select count(*)::int from public.profiles where id = '11111111-1111-1111-1111-111111111111')
    || ':' || (select status::text from public.friendships where requester = '22222222-2222-2222-2222-222222222222'),
  '1:accepted', 'CP6 unblock: the profile and the accepted friendship are visible again');
reset role;

-- == suspension ====================================================================
insert into public.moderation (profile, note) values ('44444444-4444-4444-4444-444444444444', 'test');
select pg_temp.act_as('44444444-4444-4444-4444-444444444444');

-- 16) No reactions (dave can still see alice's post: she isn't the suspended one).
select throws_ok(
  $$insert into public.reactions (post_id, reactor, emoji)
      select id, '44444444-4444-4444-4444-444444444444', '🔥' from public.posts
       where author = '11111111-1111-1111-1111-111111111111'$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot react');

-- 17) No profile edits.
select throws_ok(
  $$update public.profiles set display_name = 'Dave!' where id = '44444444-4444-4444-4444-444444444444'$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot edit its profile');

-- 18) No friend requests.
select throws_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('44444444-4444-4444-4444-444444444444', '33333333-3333-3333-3333-333333333333', 'pending')$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot send a friend request');

-- 19,20) No game invites of either kind.
select throws_ok(
  $$insert into public.game_requests (requester, addressee, first_col)
      values ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 3)$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot send a Connect 4 invite');
select throws_ok(
  $$insert into public.draw_requests (requester, addressee, difficulty, word)
      values ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 'easy', 'cat')$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot send a Draw invite');

-- 21) No moves.
select throws_ok(
  $$select public.drop_disc('a0000000-0000-0000-0000-000000000002', 3)$$,
  '42501', NULL, 'CP6 suspension: a suspended account cannot play');

-- 22) Blocking and reporting still work: they're safety tools.
select lives_ok(
  $$insert into public.blocks (blocker, blocked)
      values ('44444444-4444-4444-4444-444444444444', '33333333-3333-3333-3333-333333333333');
    insert into public.reports (reporter, reported, reason)
      values ('44444444-4444-4444-4444-444444444444', '33333333-3333-3333-3333-333333333333', 'spam')$$,
  'CP6 suspension: a suspended account can still block and report');
reset role;

-- == find_profile throttle =========================================================
-- 23) 20 lookups inside the window are fine; the 21st is refused.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
do $$
begin
  for i in 1..20 loop
    perform public.find_profile('alice');
  end loop;
end $$;
select throws_ok(
  $$select public.find_profile('alice')$$,
  '23514', NULL, 'CP6 find_profile: the 21st lookup inside 10 minutes is refused');
reset role;

-- 24) Once the window has passed, lookups work again (age it as the owner).
update private.find_profile_throttle set window_start = now() - interval '11 minutes'
  where profile = '33333333-3333-3333-3333-333333333333';
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select is(
  (select handle::text from public.find_profile('alice')),
  'alice', 'CP6 find_profile: a new window allows lookups again');
reset role;

-- 25) It's VOLATILE: PostgREST runs STABLE functions read-only, where the counter write would fail.
select is(
  (select provolatile::text from pg_proc where oid = 'public.find_profile(text)'::regprocedure),
  'v', 'CP6 find_profile: volatile, so PostgREST gives it a writable transaction');

-- 26) The new trigger functions are owner-only.
select ok(
  not has_function_privilege('authenticated', 'private.guard_game_write()', 'EXECUTE')
    and not has_function_privilege('authenticated', 'private.end_play_on_block()', 'EXECUTE'),
  'CP6: the guard and block triggers'' functions are not callable by clients');

select * from finish();
rollback;
