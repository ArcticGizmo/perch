-- Perch Social -- security hardening tests (pgTAP), review fixes CP1 + CP2 (docs/review-fixes-plan.md)
-- Each check replays an attack that worked before 20260929120000_security_hardening.sql, as role
-- `authenticated` with a simulated JWT sub (superuser bypasses RLS and owns every function, so it would
-- prove nothing).
--
-- Run with the Supabase CLI:  supabase test db --workdir backend   (after `supabase start`).

begin;
select plan(18);

-- == fixtures ==========================================================================================
-- alice & bob are accepted friends (bob posted); carol and dave are strangers to alice.
insert into auth.users (id, email) values
  ('11111111-1111-1111-1111-111111111111', 'alice@example.com'),
  ('22222222-2222-2222-2222-222222222222', 'bob@example.com'),
  ('33333333-3333-3333-3333-333333333333', 'carol@example.com'),
  ('44444444-4444-4444-4444-444444444444', 'dave@example.com');

insert into public.profiles (id, handle) values
  ('11111111-1111-1111-1111-111111111111', 'alice'),
  ('22222222-2222-2222-2222-222222222222', 'bob'),
  ('33333333-3333-3333-3333-333333333333', 'carol'),
  ('44444444-4444-4444-4444-444444444444', 'dave');

insert into public.friendships (requester, addressee, status) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'accepted');

insert into public.posts (author, body) values
  ('22222222-2222-2222-2222-222222222222', 'bob here'),
  ('11111111-1111-1111-1111-111111111111', 'alice here');

create or replace function pg_temp.act_as(uid uuid) returns void
language plpgsql as $$
begin
  perform set_config('role', 'authenticated', true);
  perform set_config('request.jwt.claims', json_build_object('sub', uid, 'role', 'authenticated')::text, true);
end $$;

-- == CP1: helpers are out of the exposed schema ========================================================
-- 1) none of the policy helpers is left in `public` (so none is reachable as /rest/v1/rpc/<name>).
select is(
  (select count(*)::int from pg_proc
    where pronamespace = 'public'::regnamespace
      and proname in ('are_friends', 'is_blocked', 'shares_edge', 'is_suspended', 'can_see_post')),
  0, 'CP1: no policy helper remains in the exposed public schema');

-- 2) ...they all live in `private`.
select is(
  (select count(*)::int from pg_proc
    where pronamespace = 'private'::regnamespace
      and proname in ('are_friends', 'is_blocked', 'shares_edge', 'is_suspended', 'can_see_post')),
  5, 'CP1: the policy helpers live in the private schema');

-- 3) anon can't even resolve names in `private`.
select ok(
  not has_schema_privilege('anon', 'private', 'USAGE'),
  'CP1: anon has no USAGE on the private schema');

-- 4) anon holds no EXECUTE on the moved helpers.
select ok(
  not has_function_privilege('anon', 'private.is_blocked(uuid, uuid)', 'EXECUTE'),
  'CP1: anon cannot execute private.is_blocked');

-- 5) the anon key can't call client RPCs (find_profile let anyone enumerate handles without an account)...
select ok(
  not has_function_privilege('anon', 'public.find_profile(text)', 'EXECUTE'),
  'CP1: anon cannot execute find_profile');

-- 6) ...but signed-in users still can.
select ok(
  has_function_privilege('authenticated', 'public.find_profile(text)', 'EXECUTE')
    and has_function_privilege('authenticated', 'public.drop_disc(uuid, int)', 'EXECUTE')
    and has_function_privilege('authenticated', 'public.submit_draw_guess(uuid, text)', 'EXECUTE'),
  'CP1: authenticated keeps EXECUTE on the client RPCs');

-- == CP1: owner-only functions are closed to clients ===================================================
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');   -- carol, an ordinary signed-in user

-- 7) the global game sweep is no longer callable (it used to delete every finished game).
select throws_ok(
  $$select public.cleanup_old_games('0 seconds'::interval)$$,
  '42501', NULL, 'CP1: authenticated cannot run cleanup_old_games');

-- 8) nor the draw sweep.
select throws_ok(
  $$select public.cleanup_old_draw_games('0 seconds'::interval)$$,
  '42501', NULL, 'CP1: authenticated cannot run cleanup_old_draw_games');

-- 9) nor the internal win check.
select throws_ok(
  $$select public.connect4_has_win('00000000-0000-0000-0000-000000000000'::uuid)$$,
  '42501', NULL, 'CP1: authenticated cannot run connect4_has_win');
reset role;

-- == CP1: the policies still work after the move ======================================================
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice
-- 10) posts_read -> private.are_friends: an accepted friend still sees the post.
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  1, 'CP1: posts_read still resolves friendship through the moved helper');

-- 11) profiles_friends_select -> private.shares_edge: alice still reads her friend's profile.
select is(
  (select count(*)::int from public.profiles where id = '22222222-2222-2222-2222-222222222222'),
  1, 'CP1: profiles_friends_select still resolves through the moved helper');
reset role;

-- == CP2: friendship consent ==========================================================================
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');   -- carol, a stranger to alice

-- 12) carol cannot create an edge that is already accepted.
select throws_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'accepted')$$,
  '42501', NULL, 'CP2: a request cannot be inserted as accepted');

-- 13) nor in the legacy blocked state.
select throws_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'blocked')$$,
  '42501', NULL, 'CP2: a request cannot be inserted as blocked');

-- 14) an ordinary pending request still works.
select lives_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'pending')$$,
  'CP2: a pending request can be sent');

-- 15) re-sending (the client's ignore-duplicates upsert) is a no-op, not an error.
select lives_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'pending')
      on conflict (requester, addressee) do nothing$$,
  'CP2: re-sending a request is a no-op');

-- 16) the REQUESTER cannot accept their own request (the update matches no row under RLS).
update public.friendships set status = 'accepted'
  where requester = '33333333-3333-3333-3333-333333333333'
    and addressee = '11111111-1111-1111-1111-111111111111';
select is(
  (select status::text from public.friendships
    where requester = '33333333-3333-3333-3333-333333333333'
      and addressee = '11111111-1111-1111-1111-111111111111'),
  'pending', 'CP2: the requester cannot accept their own request');
reset role;

select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice, the addressee

-- 17) the addressee cannot re-point the edge at someone else while accepting (friendships_guard).
select throws_ok(
  $$update public.friendships
      set requester = '44444444-4444-4444-4444-444444444444', status = 'accepted'
      where requester = '33333333-3333-3333-3333-333333333333'
        and addressee = '11111111-1111-1111-1111-111111111111'$$,
  '42501', NULL, 'CP2: an edge''s parties cannot be changed');

-- 18) the addressee CAN accept.
update public.friendships set status = 'accepted'
  where requester = '33333333-3333-3333-3333-333333333333'
    and addressee = '11111111-1111-1111-1111-111111111111';
select is(
  (select status::text from public.friendships
    where requester = '33333333-3333-3333-3333-333333333333'
      and addressee = '11111111-1111-1111-1111-111111111111'),
  'accepted', 'CP2: the addressee can accept a pending request');
reset role;

select * from finish();
rollback;
