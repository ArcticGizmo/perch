-- Perch Social — RLS tests (pgTAP)
-- Proves the authorization boundary end-to-end against a real Postgres: a non-friend cannot read
-- posts, a pending (un-accepted) friend cannot, an accepted friend can, friendship rows are private
-- to their two parties, and find_profile is exact-handle only.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- Superuser bypasses RLS, so each check runs as role `authenticated` with a simulated JWT sub —
-- exactly how auth.uid() resolves a signed-in user in production.

begin;
select plan(7);

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

select * from finish();
rollback;
