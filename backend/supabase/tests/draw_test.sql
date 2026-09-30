-- Perch -- Draw with Perch tests (pgTAP)
-- Proves the authorization boundary and the server-side game rules against a real Postgres: only an accepted
-- friend can challenge, a third party can't see the game/rounds, accept seeds round 1 as the invitee's turn to
-- guess, submit_draw_guess enforces whose round it is and scores a solve (mirroring DrawScoring), roles swap each
-- round, submit_draw_round enforces the draw phase, give_up/resign behave, and the per-player rate limit fires.
-- CP5 adds: guess/give-up only act on the game's current, open round of an in-progress game (no replaying an old
-- round to steal the drawing turn), size limits on words, hints, drawings and guesses, and an invite rate limit.
--
-- Run with the Supabase CLI:  supabase test db   (after `supabase start`).
-- Superuser bypasses RLS, so RLS checks run as role `authenticated` with a simulated JWT sub.

begin;
select plan(35);

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
-- The id is server-generated (clients can't choose it, CP3), so pin it as the owner afterwards.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');   -- alice challenges bob
insert into public.draw_requests (requester, addressee, difficulty, word, letter_hint, strokes)
  values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
          'hard', 'gravity', '7', 'x');
select is(
  (select count(*)::int from public.draw_requests
    where requester = '11111111-1111-1111-1111-111111111111' and addressee = '22222222-2222-2222-2222-222222222222'),
  1, 'draw_requests_create: an accepted friend can challenge');
reset role;
update public.draw_requests set id = 'c0000000-0000-0000-0000-000000000001'
  where requester = '11111111-1111-1111-1111-111111111111' and addressee = '22222222-2222-2222-2222-222222222222';

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
-- The pins are created by the superuser but read inside RPC arguments as `authenticated`; without this every
-- such read fails "permission denied for table" (which is 42501, so a throws_ok expecting 42501 would pass
-- for the wrong reason).
grant select on gid, rid1 to authenticated;

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
grant select on rid2 to authenticated;
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

-- == CP5: state checks + size limits ================================================
-- 15a) Giving up is not idempotent: replaying it on the round just given up is refused.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.give_up_draw_round((select id from rid2))$$,
  '23514', NULL, 'CP5 give_up_draw_round: a round already given up cannot be given up again');
reset role;

-- 15b) The turn-steal: bob (round 1's guesser) replays give_up on that old, solved round while it's alice's
-- turn to draw. Before CP5 this set whose_turn = bob, phase = draw.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.give_up_draw_round((select id from rid1))$$,
  '23514', NULL, 'CP5 give_up_draw_round: replaying an old round is refused');
reset role;
select is(
  (select whose_turn::text || ':' || phase::text from public.draw_games where id = (select id from gid)),
  '11111111-1111-1111-1111-111111111111:draw', 'CP5: the replay did not take the drawing turn');

-- 15c,d) Size limits on a submitted round: a 41-character word, then a drawing over 160 KiB.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$select public.submit_draw_round((select id from gid), 'easy', repeat('w', 41), '41', 'z')$$,
  '23514', NULL, 'CP5 draw_rounds: a word over 40 characters is rejected');
select throws_ok(
  $$select public.submit_draw_round((select id from gid), 'easy', 'kite', '4', repeat('x', 163841))$$,
  '23514', NULL, 'CP5 draw_rounds: strokes over 160 KiB are rejected');

-- Alice draws round 3 for bob (a drawing exactly at the cap is fine).
select public.submit_draw_round((select id from gid), 'easy', 'kite', '4', repeat('x', 163840));
reset role;
create temp table rid3 as
  select id from public.draw_rounds where game_id = (select id from gid) and round_no = 3;
grant select on rid3 to authenticated;

-- 15e) While round 3 is live, bob can't give up (or orphan it through) the old round 1...
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.give_up_draw_round((select id from rid1))$$,
  '23514', NULL, 'CP5 give_up_draw_round: an old round is refused while a newer one is live');
-- 15f) ...or guess it...
select throws_ok(
  $$select public.submit_draw_guess((select id from rid1), 'gravity')$$,
  '23514', NULL, 'CP5 submit_draw_guess: an old round is refused');
-- 15g) ...and a guess over 64 characters is refused.
select throws_ok(
  $$select public.submit_draw_guess((select id from rid3), repeat('g', 65))$$,
  '23514', NULL, 'CP5 submit_draw_guess: a guess over 64 characters is rejected');
reset role;
select is(
  (select r.status::text || ':' || jsonb_array_length(r.guesses)::text || ':' || g.whose_turn::text || ':' || g.phase::text
     from public.draw_rounds r join public.draw_games g on g.id = r.game_id
    where r.id = (select id from rid3)),
  'guessing:0:22222222-2222-2222-2222-222222222222:guess',
  'CP5: round 3 is untouched by the refused calls');

-- 16) A player resigns -> the game is abandoned.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select public.resign_draw_game((select id from gid));
reset role;
select is(
  (select status::text from public.draw_games where id = (select id from gid)),
  'abandoned', 'resign_draw_game: the game is abandoned');

-- 16a,b) Nothing moves on an abandoned game: no guess, no give-up, even on its live round.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select throws_ok(
  $$select public.submit_draw_guess((select id from rid3), 'kite')$$,
  '23514', NULL, 'CP5 submit_draw_guess: refused once the game is abandoned');
select throws_ok(
  $$select public.give_up_draw_round((select id from rid3))$$,
  '23514', NULL, 'CP5 give_up_draw_round: refused once the game is abandoned');
reset role;

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

-- == CP5: invite limits ==============================================================
-- 21) An invite carrying a drawing over 160 KiB is rejected.
select pg_temp.act_as('11111111-1111-1111-1111-111111111111');
select throws_ok(
  $$insert into public.draw_requests (requester, addressee, difficulty, word, letter_hint, strokes)
      values ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
              'easy', 'cat', '3', repeat('x', 163841))$$,
  '23514', NULL, 'CP5 draw_requests: strokes over 160 KiB are rejected');
reset role;

-- 22) Every size limit exists as a constraint on both tables (the ones not exercised above included).
select is(
  (select string_agg(conname, ' ' order by conname) from pg_constraint
    where conrelid in ('public.draw_rounds'::regclass, 'public.draw_requests'::regclass) and contype = 'c'
      and conname like any (array['%strokes_size', '%word_length', '%hint_length'])),
  'draw_requests_hint_length draw_requests_strokes_size draw_requests_word_length '
  'draw_rounds_hint_length draw_rounds_strokes_size draw_rounds_word_length',
  'CP5: word, hint and strokes limits exist on draw_rounds and draw_requests');

-- 23) Invites are rate limited even though each one is deleted again (the unique index can't see a
-- send/cancel loop): bob sends and cancels 10, and the 11th inside the window is refused.
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
do $$
begin
  for i in 1..10 loop
    insert into public.draw_requests (requester, addressee, difficulty, word)
      values ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'easy', 'cat');
    delete from public.draw_requests where requester = '22222222-2222-2222-2222-222222222222';
  end loop;
end $$;
select throws_ok(
  $$insert into public.draw_requests (requester, addressee, difficulty, word)
      values ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'easy', 'cat')$$,
  '23514', NULL, 'CP5 draw_requests: the 11th challenge inside 10 minutes is refused');
reset role;

-- 24) Once the window has passed, challenges flow again (age the window as the owner).
update private.draw_request_throttle set window_start = now() - interval '11 minutes'
  where profile = '22222222-2222-2222-2222-222222222222';
select pg_temp.act_as('22222222-2222-2222-2222-222222222222');
select lives_ok(
  $$insert into public.draw_requests (requester, addressee, difficulty, word)
      values ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'easy', 'cat')$$,
  'CP5 draw_requests: a new window allows challenges again');
reset role;

select * from finish();
rollback;
