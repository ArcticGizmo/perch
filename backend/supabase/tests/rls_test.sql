-- Perch Social — RLS tests (pgTAP)
-- Proves the authorization boundary end-to-end against a real Postgres: a non-friend cannot read
-- posts, a pending (un-accepted) friend cannot, an accepted friend can, friendship rows are private
-- to their two parties, and find_profile is exact-handle only.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- Superuser bypasses RLS, so each check runs as role `authenticated` with a simulated JWT sub —
-- exactly how auth.uid() resolves a signed-in user in production.

begin;
select plan(25);

-- ── fixtures (as the privileged migration role, before dropping to `authenticated`) ─────────────
-- Three users: alice, bob (will befriend alice), carol (a stranger).
insert into auth.users (id, email) values
  ('11111111-1111-1111-1111-111111111111', 'alice@example.com'),
  ('22222222-2222-2222-2222-222222222222', 'bob@example.com'),
  ('33333333-3333-3333-3333-333333333333', 'carol@example.com');

insert into public.profiles (id, handle) values
  ('11111111-1111-1111-1111-111111111111', 'alice'),
  ('22222222-2222-2222-2222-222222222222', 'bob'),
  ('33333333-3333-3333-3333-333333333333', 'carol');

-- alice and bob have a PENDING request (bob -> alice); carol is unrelated.
insert into public.friendships (requester, addressee, status) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'pending');

-- Each user posts once.
insert into public.posts (author, body) values
  ('11111111-1111-1111-1111-111111111111', 'alice here'),
  ('22222222-2222-2222-2222-222222222222', 'bob here'),
  ('33333333-3333-3333-3333-333333333333', 'carol here');

-- Helper: act as a given user id under RLS. Sets the role and the JWT sub auth.uid() reads.
create or replace function pg_temp.act_as(uid uuid) returns void
language plpgsql as $$
begin
  perform set_config('role', 'authenticated', true);
  perform set_config('request.jwt.claims', json_build_object('sub', uid, 'role', 'authenticated')::text, true);
end $$;

-- 1) While the request is only PENDING, alice cannot see bob's post.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  0, 'pending friend: alice cannot see bob''s post');
reset role;

-- Accept the request (bob and alice are now accepted friends).
update public.friendships set status = 'accepted'
  where requester = '22222222-2222-2222-2222-222222222222'
    and addressee = '11111111-1111-1111-1111-111111111111';

-- 2) Now alice CAN see bob's post.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  1, 'accepted friend: alice can see bob''s post');

-- 3) alice sees her own post too.
select is(
  (select count(*)::int from public.posts where author = '11111111-1111-1111-1111-111111111111'),
  1, 'alice can see her own post');

-- 4) alice (a stranger to carol) cannot see carol's post.
select is(
  (select count(*)::int from public.posts where author = '33333333-3333-3333-3333-333333333333'),
  0, 'stranger: alice cannot see carol''s post');

-- 5) find_profile returns an exact handle match…
select is(
  (select count(*)::int from public.find_profile('bob')),
  1, 'find_profile: exact handle resolves');

-- 6) …but not a partial/prefix (no enumeration).
select is(
  (select count(*)::int from public.find_profile('bo')),
  0, 'find_profile: partial handle does not resolve');
reset role;

-- 7) carol (a third party) cannot see the alice/bob friendship row.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select is(
  (select count(*)::int from public.friendships),
  0, 'third party cannot see others'' friendship rows');
reset role;

-- ── M6: block hides posts both directions ───────────────────────────────────────
-- alice (an accepted friend of bob) blocks bob.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
insert into public.blocks (blocker, blocked)
  values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');

-- 8) alice can no longer see bob's post (her block).
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  0, 'block: alice cannot see the blocked bob''s post');
reset role;

-- 9) bob can no longer see alice's post either (blocking is bidirectional).
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select is(
  (select count(*)::int from public.posts where author = '11111111-1111-1111-1111-111111111111'),
  0, 'block: bob cannot see alice''s post either');
reset role;

-- 10) alice unblocks bob → visibility restored (still accepted friends).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
delete from public.blocks
  where blocker = '11111111-1111-1111-1111-111111111111'
    and blocked = '22222222-2222-2222-2222-222222222222';
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  1, 'unblock: alice can see bob''s post again');

-- ── reactions: one per user per post ──────────────────────────────────────────────
-- alice (an accepted friend) reacts to bob's post, then a second reaction with a different emoji must be
-- rejected by the primary key — a person can hold at most one reaction on a post.
insert into public.reactions (post_id, reactor, emoji)
  select id, '11111111-1111-1111-1111-111111111111', '🔥'
  from public.posts where author = '22222222-2222-2222-2222-222222222222' limit 1;

-- 14) a second, different-emoji reaction on the same post from the same user violates the PK.
select throws_ok(
  $$insert into public.reactions (post_id, reactor, emoji)
      select id, '11111111-1111-1111-1111-111111111111', '👍'
      from public.posts where author = '22222222-2222-2222-2222-222222222222' limit 1$$,
  '23505', NULL,   -- match on SQLSTATE (unique_violation on post_id,reactor), not the message text
  'reactions: one reaction per user per post is enforced');
reset role;

-- ── post model: min-interval flood guard + one current status per author ──────────
-- carol posted 'carol here' in the fixtures, so the keep-latest trigger stamped her last_posted_at = now().
-- now() is frozen for this transaction, so a second post lands 0s later — inside the 5s interval.
-- 11) a post within the interval of the author's previous one is rejected by the flood guard.
select throws_ok(
  $$insert into public.posts (author, body)
      values ('33333333-3333-3333-3333-333333333333', 'too soon')$$,
  '23514', NULL,   -- check_violation raised by enforce_post_rate_limit(); match on SQLSTATE, not message
  'flood guard: a second post within the interval is rejected');

-- Push carol's last_posted_at into the past so a fresh post is allowed, then post again. The AFTER INSERT
-- keep-latest trigger must drop her previous status, leaving exactly one row — her newest.
update public.profiles set last_posted_at = now() - interval '1 minute'
  where id = '33333333-3333-3333-3333-333333333333';
insert into public.posts (author, body)
  values ('33333333-3333-3333-3333-333333333333', 'carol newest');

-- 12) exactly one post remains for the author (the superseded one was pruned)…
select is(
  (select count(*)::int from public.posts where author = '33333333-3333-3333-3333-333333333333'),
  1, 'keep-latest: only the current status is retained per author');
-- 13) …and it is the newest.
select is(
  (select body from public.posts where author = '33333333-3333-3333-3333-333333333333'),
  'carol newest', 'keep-latest: the retained post is the newest');

-- ── M6: moderation kill-switch ───────────────────────────────────────────────────
-- Suspend bob (as the owner — the moderation table has no policies, so only service_role touches it).
insert into public.moderation (profile) values ('22222222-2222-2222-2222-222222222222');

select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
-- 12) a suspended author's posts are hidden even from an accepted friend.
select is(
  (select count(*)::int from public.posts where author = '22222222-2222-2222-2222-222222222222'),
  0, 'suspended author: posts hidden from a friend');
-- 13) a suspended handle can no longer be found.
select is(
  (select count(*)::int from public.find_profile('bob')),
  0, 'suspended author: not discoverable via find_profile');
reset role;

-- ── friendship dedupe: unordered uniqueness ──────────────────────────────────────
-- (bob -> alice) still exists (accepted). alice adding the reverse (alice -> bob) must be rejected by the
-- unordered unique index — a pair holds only one row, in either direction.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$insert into public.friendships (requester, addressee, status)
      values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'pending')$$,
  '23505', NULL,   -- unique_violation on friendships_unordered; match on SQLSTATE, not message
  'friendship: a reverse-direction duplicate is rejected by the unordered unique index');
reset role;

-- ── account deletion / GDPR erasure: the cascade ─────────────────────────────────
-- The delete-account Edge Function removes a user with auth.admin.deleteUser(), i.e. it deletes the
-- auth.users row. profiles.id references auth.users ON DELETE CASCADE, and every social table references
-- profiles(id) ON DELETE CASCADE — except reports.reporter, which is ON DELETE SET NULL so a report the
-- user FILED survives, anonymised. This proves that whole chain in one delete. See
-- docs/social-account-deletion-plan.md §3 and PRIVACY.md §4. (Asserted as the owner — we want the ground
-- truth of which rows physically remain, not an RLS-filtered view.)

-- Extra fixtures so there is one of every dependent row to erase. alice and bob are accepted friends; alice
-- reacted to bob's post (both from earlier). Add a game + move between them, and two reports.
insert into public.games (id, player_red, player_yellow)
  values ('aaaaaaaa-0000-0000-0000-000000000001',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
insert into public.moves (game_id, mover, ply, col)
  values ('aaaaaaaa-0000-0000-0000-000000000001', '11111111-1111-1111-1111-111111111111', 0, 3);

-- A report alice FILED about carol (reporter = alice) — must be RETAINED with reporter nulled on erasure.
insert into public.reports (id, reporter, reported, reason) values
  ('bbbbbbbb-0000-0000-0000-000000000001',
   '11111111-1111-1111-1111-111111111111', '33333333-3333-3333-3333-333333333333', 'filed by alice');
-- A report carol filed ABOUT alice (reported = alice) — must CASCADE away when alice is erased.
insert into public.reports (id, reporter, reported, reason) values
  ('cccccccc-0000-0000-0000-000000000001',
   '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'about alice');

-- Erase alice exactly as the function does: delete the auth.users row and let the cascade run.
delete from auth.users where id = '11111111-1111-1111-1111-111111111111';

-- 16) the profile row is gone (auth.users -> profiles cascade).
select is(
  (select count(*)::int from public.profiles where id = '11111111-1111-1111-1111-111111111111'),
  0, 'erasure: profile row cascades from auth.users');

-- 17) alice's posts are gone.
select is(
  (select count(*)::int from public.posts where author = '11111111-1111-1111-1111-111111111111'),
  0, 'erasure: the user''s posts cascade');

-- 18) alice's reactions are gone (her 🔥 on bob's post).
select is(
  (select count(*)::int from public.reactions where reactor = '11111111-1111-1111-1111-111111111111'),
  0, 'erasure: the user''s reactions cascade');

-- 19) friendship edges touching alice are gone.
select is(
  (select count(*)::int from public.friendships
     where requester = '11111111-1111-1111-1111-111111111111'
        or addressee = '11111111-1111-1111-1111-111111111111'),
  0, 'erasure: friendship edges cascade');

-- 20) games alice was in are gone.
select is(
  (select count(*)::int from public.games
     where player_red = '11111111-1111-1111-1111-111111111111'
        or player_yellow = '11111111-1111-1111-1111-111111111111'),
  0, 'erasure: games cascade');

-- 21) and their moves with them.
select is(
  (select count(*)::int from public.moves where game_id = 'aaaaaaaa-0000-0000-0000-000000000001'),
  0, 'erasure: moves cascade with the game');

-- 22) a report ABOUT alice cascades away (reported = alice).
select is(
  (select count(*)::int from public.reports where id = 'cccccccc-0000-0000-0000-000000000001'),
  0, 'erasure: a report about the deleted user cascades away');

-- 23) a report the deleted user FILED is retained, with reporter anonymised to null (ON DELETE SET NULL).
select is(
  (select count(*)::int from public.reports
     where id = 'bbbbbbbb-0000-0000-0000-000000000001' and reporter is null),
  1, 'erasure: a report the user filed is retained but reporter is nulled');

select * from finish();
rollback;
