-- Perch -- Draw with Perch: an "Extra hard" word tier (base 40 points).
--
-- The client now offers a fourth word per round (DrawDifficulty.ExtraHard, wire value 'extra_hard'). Two things
-- change server-side:
--   1. draw_difficulty gains 'extra_hard', so draw_requests / draw_rounds / submit_draw_round accept it.
--   2. submit_draw_guess scores it. The old `else 30` would have paid an extra-hard solve as a hard one, so the
--      base is now explicit per tier and mirrors DrawScoring.Base again: easy 10, medium 20, hard 30, extra 40.
--
-- ADD VALUE inside the migration's transaction is fine on PG 12+ as long as nothing in the same transaction
-- USES the new value. The function below only names it in a plpgsql body, which isn't evaluated until called.
-- Older clients that read an 'extra_hard' round fall back to Medium for display only; scoring is server-side.

alter type public.draw_difficulty add value if not exists 'extra_hard';

-- Unchanged from 20260930120000_draw_state_checks.sql except the `base` line.
create or replace function public.submit_draw_guess(p_round uuid, p_guess text)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  r        public.draw_rounds;
  g        public.draw_games;
  me       uuid := auth.uid();
  gid      uuid;
  attempts int;
  correct  boolean;
  base     int;
  g_pts    int;
  d_pts    int;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;
  if char_length(coalesce(p_guess, '')) > 64 then
    raise exception 'guess is too long' using errcode = 'check_violation';
  end if;

  select game_id into gid from public.draw_rounds where id = p_round;
  if not found then raise exception 'no such round' using errcode = 'no_data_found'; end if;
  select * into g from public.draw_games  where id = gid     for update;
  select * into r from public.draw_rounds where id = p_round for update;
  if not found then raise exception 'no such round' using errcode = 'no_data_found'; end if;   -- deleted meanwhile

  if me <> r.guesser then raise exception 'not your round to guess' using errcode = '42501'; end if;
  if g.status <> 'in_progress' then raise exception 'game is over' using errcode = 'check_violation'; end if;
  if r.status <> 'guessing' then raise exception 'round is over' using errcode = 'check_violation'; end if;
  if r.round_no <> g.round_no then raise exception 'not the current round' using errcode = 'check_violation'; end if;
  if g.phase <> 'guess' or g.whose_turn <> me then
    raise exception 'not your turn to guess' using errcode = 'check_violation';
  end if;

  r.guesses := r.guesses || to_jsonb(coalesce(p_guess, ''));
  attempts  := jsonb_array_length(r.guesses);
  if attempts > 200 then raise exception 'too many guesses' using errcode = 'check_violation'; end if;

  correct := length(public.draw_normalize(r.word)) > 0
             and public.draw_normalize(p_guess) = public.draw_normalize(r.word);

  if correct then
    base  := case r.difficulty::text when 'easy' then 10 when 'medium' then 20 when 'hard' then 30 else 40 end;
    g_pts := greatest((base + 1) / 2, base - (attempts - 1) * 2);   -- floor at ceil(base/2)
    d_pts := round(base * 2.0 / 3.0)::int;

    update public.draw_rounds
       set guesses = r.guesses, status = 'solved', points_guesser = g_pts, points_drawer = d_pts, updated_at = now()
     where id = r.id;

    update public.draw_games
       set score_a    = score_a + (case when player_a = r.guesser then g_pts when player_a = r.drawer then d_pts else 0 end),
           score_b    = score_b + (case when player_b = r.guesser then g_pts when player_b = r.drawer then d_pts else 0 end),
           whose_turn = r.guesser,   -- the guesser draws the next round
           phase      = 'draw',
           updated_at = now()
     where id = g.id
     returning * into g;
  else
    update public.draw_rounds set guesses = r.guesses, updated_at = now() where id = r.id;
    update public.draw_games   set updated_at = now() where id = g.id returning * into g;
  end if;

  return g;
end;
$$;
