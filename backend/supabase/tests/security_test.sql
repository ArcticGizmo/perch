-- Perch Social -- security hardening tests (pgTAP), review fixes CP1-CP4 (docs/review-fixes-plan.md)
-- Each check replays an attack that worked before 20260929120000_security_hardening.sql /
-- 20260929130000_least_privilege.sql, as role `authenticated` (or `anon`) with a simulated JWT sub
-- (superuser bypasses RLS and owns every function, so it would prove nothing).
--
-- The CP3 block also pins the whole privilege matrix: every table's grants per role, every writable column,
-- every executable function. A new table, column grant or function that isn't deliberately added to the
-- expected lists fails here -- that is the point.
--
-- Run with the Supabase CLI:  supabase test db --workdir backend   (after `supabase start`).

begin;
select plan(51);

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

-- == CP3: the privilege matrix (tripwire) ==============================================================
-- Table-level privileges per role, as "table:rawd" (r=SELECT a=INSERT w=UPDATE d=DELETE).
create function pg_temp.table_privs(r text) returns text
language sql as $$
  select string_agg(c.relname || ':' || concat_ws('',
           case when has_table_privilege(r, c.oid, 'SELECT') then 'r' end,
           case when has_table_privilege(r, c.oid, 'INSERT') then 'a' end,
           case when has_table_privilege(r, c.oid, 'UPDATE') then 'w' end,
           case when has_table_privilege(r, c.oid, 'DELETE') then 'd' end), ' ' order by c.relname)
  from pg_class c
  where c.relnamespace = 'public'::regnamespace and c.relkind in ('r', 'p', 'v', 'm', 'f')
$$;

-- 19) anon holds nothing on any table, view or sequence -- no privilege of any kind, at table or column level.
select is(
  (select count(*)::int
     from pg_class c
    where c.relnamespace = 'public'::regnamespace and c.relkind in ('r', 'p', 'v', 'm', 'f', 'S')
      and (   has_table_privilege('anon', c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN')
           or (c.relkind <> 'S' and has_any_column_privilege('anon', c.oid, 'SELECT, INSERT, UPDATE, REFERENCES')))),
  0, 'CP3: anon holds no privilege on any table, view or sequence');

-- 20) anon can't even resolve names in `public` (the bare publishable key has no business in the Data API).
select ok(
  not has_schema_privilege('anon', 'public', 'USAGE')
    and not has_schema_privilege('authenticated', 'public', 'CREATE'),
  'CP3: anon has no USAGE on public, and no client role can CREATE there');

-- 21) authenticated's table-level grants are exactly what the client uses. INSERT/UPDATE only ever come as
-- column grants (test 22), so no table shows 'a' or 'w' here; moderation/reports/profiles show no table grant.
select is(
  pg_temp.table_privs('authenticated'),
  'blocks:rd draw_games:rd draw_requests:rd draw_rounds:r friendships:rd game_requests:rd games:rd '
  'moderation: moves:r posts:r profiles:r reactions:rd reports:',
  'CP3: authenticated table grants are exactly the expected set');

-- 22) ...and the only client-writable columns (a=INSERT, w=UPDATE) are the ones the client sends. No generated
-- id, no timestamp, no game state. (profiles.id is updatable only because PostgREST's upsert SETs it; the
-- policy pins it to auth.uid() -- test 38.)
select is(
  (select string_agg(c.relname || '.' || a.attname || ':' || concat_ws('',
            case when has_column_privilege('authenticated', c.oid, a.attnum, 'INSERT') then 'a' end,
            case when has_column_privilege('authenticated', c.oid, a.attnum, 'UPDATE') then 'w' end),
          ' ' order by c.relname, a.attname)
     from pg_class c join pg_attribute a on a.attrelid = c.oid
    where c.relnamespace = 'public'::regnamespace and c.relkind = 'r' and a.attnum > 0 and not a.attisdropped
      and (   has_column_privilege('authenticated', c.oid, a.attnum, 'INSERT')
           or has_column_privilege('authenticated', c.oid, a.attnum, 'UPDATE'))),
  'blocks.blocked:a blocks.blocker:a '
  'draw_requests.addressee:a draw_requests.difficulty:a draw_requests.letter_hint:a draw_requests.requester:a '
  'draw_requests.strokes:a draw_requests.word:a '
  'friendships.addressee:aw friendships.requester:aw friendships.status:aw '
  'game_requests.addressee:a game_requests.first_col:a game_requests.requester:a '
  'posts.author:a posts.body:a posts.mood_emoji:a '
  'profiles.display_name:aw profiles.handle:aw profiles.id:aw profiles.mood_emoji:aw '
  'reactions.emoji:a reactions.post_id:a reactions.reactor:a '
  'reports.reason:a reports.reported:a reports.reporter:a',
  'CP3: authenticated can write only the columns the client sends');

-- 23) service_role (the secret key, which bypasses RLS) gets only the moderation surface.
select is(
  pg_temp.table_privs('service_role'),
  'blocks: draw_games: draw_requests: draw_rounds: friendships: game_requests: games: '
  'moderation:rawd moves: posts: profiles: reactions: reports:rd',
  'CP3: service_role table grants are only moderation + the report queue');

-- 24) no client role holds TRUNCATE / REFERENCES / TRIGGER / MAINTAIN anywhere (none of these respect RLS).
select is(
  (select count(*)::int
     from pg_class c cross join unnest(array['anon', 'authenticated', 'service_role']) r
    where c.relnamespace = 'public'::regnamespace and c.relkind in ('r', 'p')
      and has_table_privilege(r, c.oid, 'TRUNCATE, REFERENCES, TRIGGER, MAINTAIN')),
  0, 'CP3: no client role holds TRUNCATE/REFERENCES/TRIGGER/MAINTAIN on any table');

-- 24b) nothing in `private` (the flood-guard stamps) is reachable by any client role, at any level.
select is(
  (select count(*)::int
     from pg_class c cross join unnest(array['anon', 'authenticated', 'service_role']) r
    where c.relnamespace = 'private'::regnamespace and c.relkind in ('r', 'p', 'v', 'm', 'f')
      and (   has_table_privilege(r, c.oid, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN')
           or has_any_column_privilege(r, c.oid, 'SELECT, INSERT, UPDATE, REFERENCES'))),
  0, 'CP3: no client role holds any privilege on a private table');

-- 25) every table has RLS on.
select is(
  (select count(*)::int from pg_class
    where relnamespace = 'public'::regnamespace and relkind in ('r', 'p') and not relrowsecurity),
  0, 'CP3: every table has row-level security enabled');

-- 26) every policy is scoped to authenticated (none left on PUBLIC).
select is(
  (select count(*)::int from pg_policies where schemaname = 'public' and roles <> '{authenticated}'),
  0, 'CP3: every policy applies to authenticated only');

-- 27) no function of ours is executable by PUBLIC or anon.
select is(
  (select count(*)::int
     from pg_proc p
    where p.pronamespace in ('public'::regnamespace, 'private'::regnamespace)
      and not exists (select 1 from pg_depend d
                       where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e')
      and has_function_privilege('anon', p.oid, 'EXECUTE')),
  0, 'CP3: no function is executable by PUBLIC/anon');

-- 28) authenticated can execute exactly the client RPCs + the RLS helpers -- no trigger function, no sweep.
select is(
  (select string_agg(p.pronamespace::regnamespace::text || '.' || p.proname, ' '
                     order by p.pronamespace::regnamespace::text, p.proname)
     from pg_proc p
    where p.pronamespace in ('public'::regnamespace, 'private'::regnamespace)
      and not exists (select 1 from pg_depend d
                       where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e')
      and has_function_privilege('authenticated', p.oid, 'EXECUTE')),
  'private.are_friends private.can_see_post private.inbox_owner private.is_blocked private.is_suspended '
  'private.shares_edge '
  'public.accept_draw_request public.accept_game_request public.drop_disc public.find_profile '
  'public.give_up_draw_round public.list_blocked public.resign_draw_game public.resign_game '
  'public.submit_draw_guess public.submit_draw_round',
  'CP3: authenticated executes exactly the client RPCs and the RLS helpers');

-- 29) a table created later by the migration role starts closed (the Supabase default privileges are gone).
create table public.cp3_default_probe (x int);
select ok(
  not has_table_privilege('anon', 'public.cp3_default_probe', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE')
    and not has_table_privilege('authenticated', 'public.cp3_default_probe', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE')
    and not has_table_privilege('service_role', 'public.cp3_default_probe', 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE'),
  'CP3: a new table grants nothing to anon/authenticated/service_role by default');
drop table public.cp3_default_probe;

-- 30) ...and so does a new function (beyond the global PUBLIC default, which our sweep + test 27 police).
create function public.cp3_default_probe() returns int language sql as 'select 1';
select ok(
  not exists (select 1 from pg_proc p, aclexplode(p.proacl) a
               where p.oid = 'public.cp3_default_probe()'::regprocedure
                 and a.grantee in ('anon'::regrole, 'authenticated'::regrole, 'service_role'::regrole)),
  'CP3: a new function grants nothing to anon/authenticated/service_role by default');
drop function public.cp3_default_probe();

-- == CP3: the attacks ==================================================================================
-- 31) the bare anon key reads nothing (it used to be refused only by RLS; now it can't reach the schema).
set local role anon;
select throws_ok(
  $$select count(*) from public.posts$$,
  '42501', NULL, 'CP3: anon cannot read posts at all');
reset role;

select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice (bob's accepted friend)

-- 32) no direct game creation, even with a friend and the policy's old conditions met.
select throws_ok(
  $$insert into public.games (player_red, player_yellow, status, winner, updated_at)
      values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
              'red_won', 'red', '2999-01-01')$$,
  '42501', NULL, 'CP3: a forged game cannot be inserted');

-- 33) game state can't be edited in place either.
select throws_ok(
  $$update public.games set status = 'red_won', winner = 'red'$$,
  '42501', NULL, 'CP3: games cannot be updated by a client');

-- 34) nor can moves be written directly.
select throws_ok(
  $$insert into public.moves (game_id, mover, ply, col)
      values (gen_random_uuid(), '11111111-1111-1111-1111-111111111111', 0, 3)$$,
  '42501', NULL, 'CP3: moves cannot be inserted by a client');

-- 35) a backdated post (the old flood-guard bypass) is refused outright: created_at isn't client-writable.
select throws_ok(
  $$insert into public.posts (author, body, created_at)
      values ('11111111-1111-1111-1111-111111111111', 'from the past', '1970-01-01')$$,
  '42501', NULL, 'CP3: a client cannot choose a post''s created_at');

-- 36) the flood-guard stamp is out of reach (neither readable nor resettable).
select throws_ok(
  $$update private.post_throttle set last_posted_at = '1970-01-01'
      where profile = '11111111-1111-1111-1111-111111111111'$$,
  '42501', NULL, 'CP3: a client cannot reset its own flood guard');

-- 37) the claim-handle upsert, in exactly the shape PostgREST compiles a merge-duplicates upsert to (every sent
-- column in the SET, id included; RETURNING *), works within the grants.
select lives_ok(
  $$insert into public.profiles (id, handle, display_name, mood_emoji)
      values ('11111111-1111-1111-1111-111111111111', 'alice', 'Alice', null)
      on conflict (id) do update
        set id = excluded.id, handle = excluded.handle, display_name = excluded.display_name,
            mood_emoji = excluded.mood_emoji
      returning *$$,
  'CP3: claiming / editing your handle still works');

-- 38) ...but the id grant can't be used to take over another profile id.
select throws_ok(
  $$update public.profiles set id = '44444444-4444-4444-4444-444444444444'
      where id = '11111111-1111-1111-1111-111111111111'$$,
  '42501', NULL, 'CP3: a client cannot change its profile id');

-- 38b) the report queue is write-only.
select throws_ok(
  $$select count(*) from public.reports$$,
  '42501', NULL, 'CP3: a client cannot read the report queue');
reset role;

-- 39) a timestamp supplied on a server-side path is overridden with now() (the BEFORE INSERT stamp). Carol has
-- no status yet, so the flood guard doesn't interfere.
insert into public.posts (author, body, created_at)
  values ('33333333-3333-3333-3333-333333333333', 'backdated by the owner', '1970-01-01');
select is(
  (select created_at from public.posts where author = '33333333-3333-3333-3333-333333333333'),
  now(), 'CP3: created_at is stamped by the server even when supplied');

-- 40) an ordinary client post works (insert grant + triggers fire without EXECUTE on the trigger functions)...
select pg_temp.act_as('44444444-4444-4444-4444-444444444444');   -- dave, no status yet
select lives_ok(
  $$insert into public.posts (author, body) values ('44444444-4444-4444-4444-444444444444', 'dave here')$$,
  'CP3: a client can post a status');
reset role;

-- 41) ...and stamps the flood guard with the server clock.
select is(
  (select last_posted_at from private.post_throttle where profile = '44444444-4444-4444-4444-444444444444'),
  now(), 'CP3: the flood-guard stamp is the server clock');

-- 42) a second post inside the interval trips the flood guard (now() is frozen in this transaction, so 0s).
select pg_temp.act_as('44444444-4444-4444-4444-444444444444');
select throws_ok(
  $$insert into public.posts (author, body) values ('44444444-4444-4444-4444-444444444444', 'too soon')$$,
  '23514', NULL, 'CP3: two posts inside the interval trip the flood guard');
reset role;

-- == CP4: Realtime inbox authorisation =================================================================
-- Realtime authorises a private channel by running SELECT (join) / INSERT (broadcast) on realtime.messages as
-- the caller's role, with realtime.topic() set to the channel topic -- replayed here the same way. By now
-- alice's friends are bob (fixture) and carol (accepted in test 18); dave is a stranger.
create function pg_temp.topic(t text) returns void
language sql as $$ select set_config('realtime.topic', t, true) $$;

-- 45) the topic parser only accepts a canonical inbox topic (anything else fails the policies).
select ok(
  private.inbox_owner('perch:inbox:11111111-1111-1111-1111-111111111111') = '11111111-1111-1111-1111-111111111111'
    and private.inbox_owner('perch:inbox:11111111-1111-1111-1111-111111111111:x') is null
    and private.inbox_owner('perch:inbox:not-a-uuid') is null
    and private.inbox_owner('perch:inbox:') is null
    and private.inbox_owner('other:11111111-1111-1111-1111-111111111111') is null
    and private.inbox_owner(null) is null,
  'CP4: inbox_owner parses only canonical perch:inbox:<uuid> topics');

-- A message sitting in alice's inbox, written as the owner (the way Realtime stores one).
insert into realtime.messages (id, topic, extension, private, event, payload)
  values ('dddddddd-0000-0000-0000-000000000001',
          'perch:inbox:11111111-1111-1111-1111-111111111111', 'broadcast', true, 'inbox', '{}');

-- 46) the owner can join (read) her own inbox...
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select pg_temp.topic('perch:inbox:11111111-1111-1111-1111-111111111111');
select is(
  (select count(*)::int from realtime.messages where id = 'dddddddd-0000-0000-0000-000000000001'),
  1, 'CP4: the owner can read her own inbox');
reset role;

-- 47) ...but nobody else can, not even a friend.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');   -- bob, alice's friend
select pg_temp.topic('perch:inbox:11111111-1111-1111-1111-111111111111');
select is(
  (select count(*)::int from realtime.messages where id = 'dddddddd-0000-0000-0000-000000000001'),
  0, 'CP4: nobody else can read an inbox, not even a friend');

-- 48) a friend can broadcast into it.
select lives_ok(
  $$insert into realtime.messages (topic, extension, private, event, payload)
      values ('perch:inbox:11111111-1111-1111-1111-111111111111', 'broadcast', true, 'inbox', '{}')$$,
  'CP4: a friend can broadcast into an inbox');
reset role;

-- 49) a stranger can't.
select pg_temp.act_as('44444444-4444-4444-4444-444444444444');   -- dave
select pg_temp.topic('perch:inbox:11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$insert into realtime.messages (topic, extension, private, event, payload)
      values ('perch:inbox:11111111-1111-1111-1111-111111111111', 'broadcast', true, 'inbox', '{}')$$,
  '42501', NULL, 'CP4: a stranger cannot broadcast into an inbox');
reset role;

-- 50) nor a friend alice has blocked.
insert into public.blocks (blocker, blocked)
  values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select pg_temp.topic('perch:inbox:11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$insert into realtime.messages (topic, extension, private, event, payload)
      values ('perch:inbox:11111111-1111-1111-1111-111111111111', 'broadcast', true, 'inbox', '{}')$$,
  '42501', NULL, 'CP4: a blocked friend cannot broadcast into an inbox');
reset role;

-- 51) and the bare anon key gets nothing (no policy is scoped to anon).
set local role anon;
select pg_temp.topic('perch:inbox:11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$insert into realtime.messages (topic, extension, private, event, payload)
      values ('perch:inbox:11111111-1111-1111-1111-111111111111', 'broadcast', true, 'inbox', '{}')$$,
  '42501', NULL, 'CP4: anon cannot broadcast into an inbox');
reset role;

select * from finish();
rollback;
