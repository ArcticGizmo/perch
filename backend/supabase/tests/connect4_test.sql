-- Perch — Connect 4 tests (pgTAP)
-- Proves the networked-game authorization boundary and the server-side move rules against a real Postgres:
-- only an accepted friend can start a game, a third party can't see the game or its moves, drop_disc enforces
-- whose turn it is and stops after a win, connect4_has_win detects a four-in-a-row, resign hands the win over,
-- and the per-player move rate limit fires.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- Superuser bypasses RLS, so RLS checks run as role `authenticated` with a simulated JWT sub — exactly how
-- auth.uid() resolves a signed-in user in production.

begin;
select plan(21);

-- ── fixtures ────────────────────────────────────────────────────────────────────
-- alice & bob are accepted friends; carol is a stranger.
insert into auth.users (id, email) values
  ('11111111-1111-1111-1111-111111111111', 'alice@example.com'),
  ('22222222-2222-2222-2222-222222222222', 'bob@example.com'),
  ('33333333-3333-3333-3333-333333333333', 'carol@example.com');

insert into public.profiles (id, handle) values
  ('11111111-1111-1111-1111-111111111111', 'alice'),
  ('22222222-2222-2222-2222-222222222222', 'bob'),
  ('33333333-3333-3333-3333-333333333333', 'carol');

insert into public.friendships (requester, addressee, status) values
  ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'accepted');

create or replace function pg_temp.act_as(uid uuid) returns void
language plpgsql as $$
begin
  perform set_config('role', 'authenticated', true);
  perform set_config('request.jwt.claims', json_build_object('sub', uid, 'role', 'authenticated')::text, true);
end $$;

-- 1) An accepted friend can start a game (as red).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
insert into public.games (id, player_red, player_yellow)
  values ('a0000000-0000-0000-0000-000000000001',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
select is(
  (select count(*)::int from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  1, 'games_create: an accepted friend can start a game');
reset role;

-- 2) You cannot start a game with a non-friend (carol is a stranger to alice) — RLS WITH CHECK rejects it.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select throws_ok(
  $$insert into public.games (player_red, player_yellow)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111')$$,
  '42501', 'games_create: cannot start a game with a non-friend');
reset role;

-- 3) A third party cannot even see the game.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select is(
  (select count(*)::int from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  0, 'games RLS: a third party cannot see the game');
reset role;

-- 4) drop_disc rejects a move out of turn (red/alice moves first, so bob cannot).
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.drop_disc('a0000000-0000-0000-0000-000000000001', 3)$$,
  '23514', 'drop_disc: a move out of turn is rejected');
reset role;

-- 5,6) A legal move is recorded and passes the turn to the opponent.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select public.drop_disc('a0000000-0000-0000-0000-000000000001', 0);
reset role;
select is(
  (select move_count from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  1, 'drop_disc: records the move');
select is(
  (select turn::text from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  'yellow', 'drop_disc: passes the turn to the opponent');

-- Build a vertical four for red (alice) in column 0; bob parks harmlessly in column 1.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 1);
select pg_temp.act_as('11111111-1111-1111-1111-111111111111'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 0);
select pg_temp.act_as('22222222-2222-2222-2222-222222222222'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 1);
select pg_temp.act_as('11111111-1111-1111-1111-111111111111'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 0);
select pg_temp.act_as('22222222-2222-2222-2222-222222222222'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 1);
select pg_temp.act_as('11111111-1111-1111-1111-111111111111'); select public.drop_disc('a0000000-0000-0000-0000-000000000001', 0);
reset role;

-- 7,8) The game is won by red, and the winner is recorded.
select is(
  (select status::text from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  'red_won', 'connect4_has_win: a vertical four wins for red');
select is(
  (select winner::text from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  'red', 'drop_disc: the winning colour is recorded');

-- 9) No moves are accepted once the game is over.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.drop_disc('a0000000-0000-0000-0000-000000000001', 2)$$,
  '23514', 'drop_disc: no moves after the game is over');
reset role;

-- 10) A third party cannot see the moves either.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select is(
  (select count(*)::int from public.moves where game_id = 'a0000000-0000-0000-0000-000000000001'),
  0, 'moves RLS: a third party cannot see the moves');
reset role;

-- 11) Resign hands the win to the opponent (bob resigns → red/alice wins).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
insert into public.games (id, player_red, player_yellow)
  values ('a0000000-0000-0000-0000-000000000002',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
reset role;
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select public.resign_game('a0000000-0000-0000-0000-000000000002');
reset role;
select is(
  (select status::text from public.games where id = 'a0000000-0000-0000-0000-000000000002'),
  'red_won', 'resign_game: hands the win to the opponent');

-- 12) The per-player move rate limit fires. Insert 30 moves for one player within the window (as the owner,
-- so RLS is bypassed but the BEFORE INSERT trigger still fires), then the 31st is rejected. Uses carol as the
-- mover so the count is independent of the drop_disc moves above.
insert into public.games (id, player_red, player_yellow)
  values ('a0000000-0000-0000-0000-000000000003',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
insert into public.moves (game_id, mover, ply, col)
  select 'a0000000-0000-0000-0000-000000000003', '33333333-3333-3333-3333-333333333333', g, g % 7
  from generate_series(0, 29) g;
select throws_ok(
  $$insert into public.moves (game_id, mover, ply, col)
      values ('a0000000-0000-0000-0000-000000000003', '33333333-3333-3333-3333-333333333333', 30, 3)$$,
  '23514', 'moves rate limit: too many moves in a short window is rejected');

-- 13) A third party's delete removes nothing — RLS filters the rows out, so the game survives.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
delete from public.games where id = 'a0000000-0000-0000-0000-000000000001';
reset role;
select is(
  (select count(*)::int from public.games where id = 'a0000000-0000-0000-0000-000000000001'),
  1, 'games_delete: a third party cannot delete someone else''s game');

-- 14) A player can delete their own game, and its moves cascade away.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
delete from public.games where id = 'a0000000-0000-0000-0000-000000000001';
reset role;
select is(
  (select count(*)::int from public.games where id = 'a0000000-0000-0000-0000-000000000001')
    + (select count(*)::int from public.moves where game_id = 'a0000000-0000-0000-0000-000000000001'),
  0, 'games_delete: a player deletes their own game and its moves cascade');

-- ── game invites (request / accept) ──────────────────────────────
-- 15) An accepted friend can send an invite.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice invites bob
insert into public.game_requests (id, requester, addressee, first_col)
  values ('c0000000-0000-0000-0000-000000000001',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 4);
select is(
  (select count(*)::int from public.game_requests where id = 'c0000000-0000-0000-0000-000000000001'),
  1, 'game invite: an accepted friend can invite');
reset role;

-- 16) A stranger cannot invite (RLS WITH CHECK).
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select throws_ok(
  $$insert into public.game_requests (requester, addressee)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111')$$,
  '42501', 'game invite: a stranger cannot invite');

-- 17) A third party cannot see the invite.
select is(
  (select count(*)::int from public.game_requests where id = 'c0000000-0000-0000-0000-000000000001'),
  0, 'game invite: a third party cannot see it');
reset role;

-- 18) Only the invitee can accept (alice is the requester here, not the addressee).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.accept_game_request('c0000000-0000-0000-0000-000000000001')$$,
  '42501', 'accept: only the invitee can accept');
reset role;

-- 19) The invitee accepts → a game is created and the request is gone.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');   -- bob accepts
select public.accept_game_request('c0000000-0000-0000-0000-000000000001');
reset role;
select is(
  (select count(*)::int from public.game_requests where id = 'c0000000-0000-0000-0000-000000000001')
    + (select case when exists (
        select 1 from public.games
        where player_red = '11111111-1111-1111-1111-111111111111'
          and player_yellow = '22222222-2222-2222-2222-222222222222'
          and status = 'in_progress') then 0 else 1 end),
  0, 'accept: the request is gone and an in-progress game now exists');

-- 20) The accepted game is seeded with the inviter's opening move (red, col 4) and it's the invitee's turn.
select is(
  (select move_count::int || ':' || turn::text
     from public.games
    where player_red = '11111111-1111-1111-1111-111111111111'
      and player_yellow = '22222222-2222-2222-2222-222222222222'
      and status = 'in_progress'
    order by created_at desc limit 1),
  '1:yellow', 'accept: the invite''s opening move is seeded and it is the invitee''s turn');

-- 21) That opening move is red's disc in column 4 at ply 0.
select is(
  (select mover::text || ':' || col::int
     from public.moves
    where game_id = (select id from public.games
                       where player_red = '11111111-1111-1111-1111-111111111111'
                         and player_yellow = '22222222-2222-2222-2222-222222222222'
                       order by created_at desc limit 1)
      and ply = 0),
  '11111111-1111-1111-1111-111111111111:4', 'accept: move 0 is the inviter''s disc in the chosen column');

select * from finish();
rollback;
