-- Perch -- Draw with Perch tests (pgTAP)
-- Proves the authorization boundary and the server-side game rules against a real Postgres: only an accepted
-- friend can challenge, a third party can't see the game/rounds, accept seeds round 1 as the invitee's turn to
-- guess, submit_draw_guess enforces whose round it is and scores a solve (mirroring DrawScoring), roles swap each
-- round, submit_draw_round enforces the draw phase, give_up/resign behave, and the per-player rate limit fires.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- Superuser bypasses RLS, so RLS checks run as role `authenticated` with a simulated JWT sub.

begin;
select plan(20);

-- == fixtures ======================================================================
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

-- 1) An accepted friend can send a challenge.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice challenges bob
insert into public.draw_requests (id, requester, addressee, difficulty, word, letter_hint, strokes)
  values ('c0000000-0000-0000-0000-000000000001',
          '11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
          'hard', 'gravity', '7', 'x');
select is(
  (select count(*)::int from public.draw_requests where id = 'c0000000-0000-0000-0000-000000000001'),
  1, 'draw_requests_create: an accepted friend can challenge');
reset role;

-- 2) A stranger cannot challenge (RLS WITH CHECK).
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select throws_ok(
  $$insert into public.draw_requests (requester, addressee, difficulty, word)
      values ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'easy', 'cat')$$,
  '42501', NULL, 'draw_requests_create: a stranger cannot challenge');

-- 3) A third party cannot see the challenge.
select is(
  (select count(*)::int from public.draw_requests where id = 'c0000000-0000-0000-0000-000000000001'),
  0, 'draw_requests RLS: a third party cannot see it');
reset role;

-- 4) Only the invitee can accept (alice is the requester, not the addressee).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.accept_draw_request('c0000000-0000-0000-0000-000000000001')$$,
  '42501', NULL, 'accept_draw_request: only the invitee can accept');
reset role;

-- 5) The invitee accepts -> a game is created and the request is gone.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');   -- bob accepts
select public.accept_draw_request('c0000000-0000-0000-0000-000000000001');
reset role;
-- Pin the created game (unique alice/bob draw game) for the rest of the test.
create temp table gid as
  select id from public.draw_games
  where player_a = '11111111-1111-1111-1111-111111111111'
    and player_b = '22222222-2222-2222-2222-222222222222';
create temp table rid1 as
  select id from public.draw_rounds where game_id = (select id from gid) and round_no = 1;

select is(
  (select count(*)::int from public.draw_requests where id = 'c0000000-0000-0000-0000-000000000001')
    + (select count(*)::int from gid),
  1, 'accept: the request is gone and exactly one in-progress game now exists');

-- 6) The game is seeded to round 1, the invitee's turn to guess.
select is(
  (select whose_turn::text || ':' || phase::text || ':' || round_no::text from public.draw_games where id = (select id from gid)),
  '22222222-2222-2222-2222-222222222222:guess:1',
  'accept: whose_turn = invitee, phase = guess, round 1');

-- 7) Round 1: the challenger drew it for the invitee, carrying the word.
select is(
  (select drawer::text || ':' || guesser::text || ':' || word from public.draw_rounds where id = (select id from rid1)),
  '11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:gravity',
  'accept: round 1 drawer/guesser/word are seeded from the invite');

-- 8) The drawer cannot guess their own round.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.submit_draw_guess((select id from rid1), 'gravity')$$,
  '42501', NULL, 'submit_draw_guess: the drawer cannot guess their own round');
reset role;

-- 9) A wrong guess is recorded and the round stays open.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select public.submit_draw_guess((select id from rid1), 'electricity');
reset role;
select is(
  (select status::text || ':' || jsonb_array_length(guesses)::text from public.draw_rounds where id = (select id from rid1)),
  'guessing:1', 'submit_draw_guess: a wrong guess is recorded, round stays open');

-- 10,11) A correct guess (2nd attempt) solves it and scores both players (Hard base 30: guesser 30-2=28, drawer 20).
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select public.submit_draw_guess((select id from rid1), 'Gravity!');
reset role;
select is(
  (select status::text from public.draw_rounds where id = (select id from rid1)),
  'solved', 'submit_draw_guess: a correct guess solves the round');
select is(
  (select score_a::text || ':' || score_b::text from public.draw_games where id = (select id from gid)),
  '20:28', 'submit_draw_guess: drawer (alice) +20, guesser (bob) +28 mirror DrawScoring');

-- 12) After a solve the roles swap: the guesser (bob) now draws next.
select is(
  (select whose_turn::text || ':' || phase::text from public.draw_games where id = (select id from gid)),
  '22222222-2222-2222-2222-222222222222:draw', 'solve: the guesser is next to draw');

-- 13) Bob draws round 2 (for alice to guess).
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select public.submit_draw_round((select id from gid), 'medium', 'rocket', '6', 'y');
reset role;
create temp table rid2 as
  select id from public.draw_rounds where game_id = (select id from gid) and round_no = 2;
select is(
  (select drawer::text || ':' || guesser::text || ':' || whose_turn::text
     from public.draw_rounds r join public.draw_games g on g.id = r.game_id
    where r.id = (select id from rid2)),
  '22222222-2222-2222-2222-222222222222:11111111-1111-1111-1111-111111111111:11111111-1111-1111-1111-111111111111',
  'submit_draw_round: roles swap and the opponent is next to guess');

-- 14) You cannot submit a drawing during the guess phase (alice's turn is to guess round 2).
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.submit_draw_round((select id from gid), 'easy', 'cat', '3', 'z')$$,
  '23514', NULL, 'submit_draw_round: rejected outside the draw phase');
reset role;

-- 15) The guesser (alice) gives up round 2 -> revealed, no score change, alice draws next.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select public.give_up_draw_round((select id from rid2));
reset role;
select is(
  (select r.status::text || ':' || g.phase::text || ':' || g.score_a::text
     from public.draw_rounds r join public.draw_games g on g.id = r.game_id
    where r.id = (select id from rid2)),
  'gave_up:draw:20', 'give_up_draw_round: round revealed, no points, drawer phase');

-- 16) A player resigns -> the game is abandoned.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select public.resign_draw_game((select id from gid));
reset role;
select is(
  (select status::text from public.draw_games where id = (select id from gid)),
  'abandoned', 'resign_draw_game: the game is abandoned');

-- 17) The per-player round rate limit fires. Insert 20 rounds for one drawer within the window (as the owner, so
-- RLS is bypassed but the BEFORE INSERT trigger still fires), then the 21st is rejected. Uses a separate game so
-- the count is independent of the rounds above.
insert into public.draw_games (id, player_a, player_b, whose_turn, phase)
  values ('d0000000-0000-0000-0000-000000000009',
          '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111',
          '33333333-3333-3333-3333-333333333333', 'draw');
insert into public.draw_rounds (game_id, round_no, drawer, guesser, difficulty, word)
  select 'd0000000-0000-0000-0000-000000000009', g,
         '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'easy', 'cat'
  from generate_series(1, 20) g;
select throws_ok(
  $$insert into public.draw_rounds (game_id, round_no, drawer, guesser, difficulty, word)
      values ('d0000000-0000-0000-0000-000000000009', 21,
              '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'easy', 'cat')$$,
  '23514', NULL, 'draw_rounds rate limit: too many drawings in a short window is rejected');

-- 18) A third party cannot see the game.
select pg_temp.act_as('33333333-3333-3333-3333-333333333333');
select is(
  (select count(*)::int from public.draw_games where id = (select id from gid)),
  0, 'draw_games RLS: a third party cannot see the game');

-- 19) A third party's delete removes nothing.
delete from public.draw_games where id = (select id from gid);
reset role;
select is(
  (select count(*)::int from public.draw_games where id = (select id from gid)),
  1, 'draw_games_delete: a third party cannot delete someone else''s game');

-- 20) A player deletes their own game and its rounds cascade away.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
delete from public.draw_games where id = (select id from gid);
reset role;
select is(
  (select count(*)::int from public.draw_games where id = (select id from gid))
    + (select count(*)::int from public.draw_rounds where game_id = (select id from gid)),
  0, 'draw_games_delete: a player deletes their own game and its rounds cascade');

select * from finish();
rollback;
