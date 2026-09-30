-- Perch -- Draw with Perch: RPC state checks + size limits (review fixes CP5, docs/review-fixes-plan.md)
--
-- The problem:
--   - give_up_draw_round and submit_draw_guess only checked that the caller was the round's guesser. Neither
--     looked at the game or asked whether the round was the CURRENT one. So replaying give_up on any old round
--     you once guessed set whose_turn = you, phase = 'draw' -- you took the drawing turn whenever you liked,
--     and the round your opponent had just drawn for you was orphaned (never guessed, never resolved). Guessing
--     also kept working on an abandoned game.
--   - No length limits anywhere: strokes, word and letter_hint (on rounds and on invites) and each guess could be
--     megabytes. That fills storage, and every client downloads and parses the payload on every poll.
--   - draw_requests had no rate limit. The unique (requester, addressee) index caps OUTSTANDING invites, but
--     insert -> delete -> insert loops freely, and every insert is a realtime push to the addressee.
--
-- The fix:
--   1. Rows already over the new limits are trimmed, then CHECK constraints bound strokes (bytes), word and
--      letter_hint (characters) on both tables. The strokes cap is sized from DrawStrokeCodec's own caps: the
--      largest drawing the client can encode is about 138 KB, so the cap is 160 KiB (not the 64 KB first
--      planned, which would have refused a legitimate busy drawing). DrawStrokeCodecTests pins that bound.
--   2. submit_draw_guess and give_up_draw_round lock the game, then the round, and require: the game is in
--      progress, in the guess phase, on the caller's turn, the round is the game's current round, and it is
--      still being guessed. A guess is at most 64 characters.
--   3. draw_requests inserts are rate limited per requester (10 per 10 minutes) through a counter in `private`,
--      since deleted invites leave nothing behind to count.

-- == 1. size limits =====================================================================================
-- Bring any existing row inside the limits first, so the constraints validate. Nothing the client writes is
-- anywhere near them; a row over them was written by hand.
update public.draw_rounds   set strokes = ''                        where octet_length(strokes) > 163840;
update public.draw_rounds   set word = left(word, 40)               where char_length(word) > 40;
update public.draw_rounds   set letter_hint = left(letter_hint, 40) where char_length(letter_hint) > 40;
update public.draw_requests set strokes = ''                        where octet_length(strokes) > 163840;
update public.draw_requests set word = left(word, 40)               where char_length(word) > 40;
update public.draw_requests set letter_hint = left(letter_hint, 40) where char_length(letter_hint) > 40;

alter table public.draw_rounds
  drop constraint if exists draw_rounds_strokes_size,
  drop constraint if exists draw_rounds_word_length,
  drop constraint if exists draw_rounds_hint_length;
alter table public.draw_rounds
  add constraint draw_rounds_strokes_size check (octet_length(strokes) <= 163840),
  add constraint draw_rounds_word_length  check (char_length(word) <= 40),
  add constraint draw_rounds_hint_length  check (char_length(letter_hint) <= 40);

alter table public.draw_requests
  drop constraint if exists draw_requests_strokes_size,
  drop constraint if exists draw_requests_word_length,
  drop constraint if exists draw_requests_hint_length;
alter table public.draw_requests
  add constraint draw_requests_strokes_size check (octet_length(strokes) <= 163840),
  add constraint draw_requests_word_length  check (char_length(word) <= 40),
  add constraint draw_requests_hint_length  check (char_length(letter_hint) <= 40);

-- == 2. RPC state checks ================================================================================
-- Both lock the game before the round, the same order as submit_draw_round and a game delete's cascade, so a
-- guess racing a delete can't deadlock. create or replace keeps the existing (authenticated-only) ACL.

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
    base  := case r.difficulty when 'easy' then 10 when 'medium' then 20 else 30 end;
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

-- Giving up is no longer idempotent: it resolves the current round exactly once. (Replaying it on an old round
-- was the turn-stealing hole.)
create or replace function public.give_up_draw_round(p_round uuid)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  r   public.draw_rounds;
  g   public.draw_games;
  me  uuid := auth.uid();
  gid uuid;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

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

  update public.draw_rounds set status = 'gave_up', updated_at = now() where id = r.id;

  update public.draw_games
     set whose_turn = r.guesser, phase = 'draw', updated_at = now()
   where id = g.id
   returning * into g;
  return g;
end;
$$;

-- == 3. draw_requests rate limit ========================================================================
-- A fixed-window counter per requester. It lives in `private` with no grants (like post_throttle): nobody can
-- read or reset it, and the SECURITY DEFINER trigger is its only writer. A refused insert (this limit, RLS, the
-- unique index) rolls the count back with it, so only challenges that were actually sent are counted.
create table if not exists private.draw_request_throttle (
  profile      uuid primary key references public.profiles(id) on delete cascade,
  window_start timestamptz not null,
  sent         int not null
);
alter table private.draw_request_throttle enable row level security;   -- no policies: defence in depth behind no grants
revoke all on table private.draw_request_throttle from public, anon, authenticated, service_role;

create or replace function private.enforce_draw_request_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
  n int;
begin
  insert into private.draw_request_throttle as t (profile, window_start, sent)
    values (new.requester, now(), 1)
  on conflict (profile) do update
    set window_start = case when t.window_start <= now() - interval '10 minutes' then now() else t.window_start end,
        sent         = case when t.window_start <= now() - interval '10 minutes' then 1     else t.sent + 1    end
  returning sent into n;

  if n > 10 then
    raise exception 'Rate limit: too many challenges. Try again in a few minutes.' using errcode = 'check_violation';
  end if;
  return new;
end;
$$;
-- Functions in `private` still get Postgres' PUBLIC EXECUTE default; a trigger fires without it.
revoke all on function private.enforce_draw_request_rate_limit() from public, anon, authenticated, service_role;

drop trigger if exists draw_requests_rate_limit on public.draw_requests;
create trigger draw_requests_rate_limit
  before insert on public.draw_requests
  for each row execute function private.enforce_draw_request_rate_limit();
