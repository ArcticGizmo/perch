-- Perch -- networked "Draw with Perch" (doodle & guess)
-- A slower, async draw-and-guess game between two accepted friends. Three tables (draw_games, draw_rounds,
-- draw_requests), their RLS + grants, the SECURITY DEFINER RPCs that own every state transition (accept a
-- challenge, submit a drawing, guess, give up, resign) and compute scores, a per-player rate limit, a retention
-- sweep, and the realtime publication. Every rule mirrors the pure Perch.Games engine (DrawGuessing / DrawScoring);
-- the DATABASE is authoritative so a tampered client can't cheat turns or scores. The word rides on the round
-- (secrecy is by convention only -- the game is for fun). See docs/draw-with-perch-plan.md.

-- == enums =========================================================================
do $$ begin create type public.draw_status       as enum ('in_progress','abandoned');           exception when duplicate_object then null; end $$;
do $$ begin create type public.draw_phase        as enum ('draw','guess');                        exception when duplicate_object then null; end $$;
do $$ begin create type public.draw_round_status as enum ('guessing','solved','gave_up');         exception when duplicate_object then null; end $$;
do $$ begin create type public.draw_difficulty   as enum ('easy','medium','hard');                exception when duplicate_object then null; end $$;

-- == draw_games ====================================================================
-- One row per match. player_a started it (drew round 1). status/whose_turn/phase/scores/round_no are owned by
-- the server (the RPCs below) -- there is deliberately no client INSERT/UPDATE grant, so state only advances
-- through a validated action. Games are born only by accepting a challenge (accept_draw_request).
create table if not exists public.draw_games (
  id          uuid primary key default gen_random_uuid(),
  player_a    uuid not null references public.profiles(id) on delete cascade,
  player_b    uuid not null references public.profiles(id) on delete cascade,
  status      public.draw_status not null default 'in_progress',
  whose_turn  uuid not null references public.profiles(id) on delete cascade,   -- who must act next
  phase       public.draw_phase  not null default 'guess',                      -- draw a new round, or guess the pending one
  score_a     int  not null default 0,
  score_b     int  not null default 0,
  round_no    int  not null default 0,
  created_at  timestamptz not null default now(),
  updated_at  timestamptz not null default now(),
  check (player_a <> player_b)
);
create index if not exists draw_games_a on public.draw_games (player_a, updated_at desc);
create index if not exists draw_games_b on public.draw_games (player_b, updated_at desc);

-- == draw_rounds ===================================================================
-- One row per round. Inserted (by the RPC) when a drawing is submitted, then updated as guesses land. Roles
-- swap each round: the guesser of one round draws the next. No client INSERT/UPDATE -- rounds move only via the
-- RPCs. The word is stored here readable by both players (secrecy is a client-side convention).
create table if not exists public.draw_rounds (
  id            uuid primary key default gen_random_uuid(),
  game_id       uuid not null references public.draw_games(id) on delete cascade,
  round_no      int  not null check (round_no >= 1),
  drawer        uuid not null references public.profiles(id) on delete cascade,
  guesser       uuid not null references public.profiles(id) on delete cascade,
  difficulty    public.draw_difficulty not null,
  word          text not null,
  letter_hint   text not null default '',
  strokes       text not null default '',                 -- DrawStrokeCodec-encoded drawing
  status        public.draw_round_status not null default 'guessing',
  guesses       jsonb not null default '[]'::jsonb,        -- array of guess strings, in order
  points_drawer int  not null default 0,
  points_guesser int not null default 0,
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  unique (game_id, round_no)
);
create index if not exists draw_rounds_game on public.draw_rounds (game_id, round_no);

-- == draw_requests =================================================================
-- A challenge carries the challenger's first drawing (drawn before sending) until accepted -- accepting creates
-- the game + round 1 and removes the request; declining/cancelling just deletes it.
create table if not exists public.draw_requests (
  id          uuid primary key default gen_random_uuid(),
  requester   uuid not null references public.profiles(id) on delete cascade,   -- drew round 1, becomes player_a
  addressee   uuid not null references public.profiles(id) on delete cascade,   -- guesses round 1, becomes player_b
  difficulty  public.draw_difficulty not null,
  word        text not null,
  letter_hint text not null default '',
  strokes     text not null default '',
  created_at  timestamptz not null default now(),
  check (requester <> addressee),
  unique (requester, addressee)
);
create index if not exists draw_requests_addressee on public.draw_requests (addressee, created_at desc);

-- == RLS ===========================================================================
alter table public.draw_games    enable row level security;
alter table public.draw_rounds   enable row level security;
alter table public.draw_requests enable row level security;

-- Games + rounds are visible only to the two players.
drop policy if exists draw_games_party_select on public.draw_games;
create policy draw_games_party_select on public.draw_games
  for select using (player_a = auth.uid() or player_b = auth.uid());

-- No client INSERT/UPDATE on games: they're created and advanced by the SECURITY DEFINER RPCs only.

drop policy if exists draw_rounds_party_select on public.draw_rounds;
create policy draw_rounds_party_select on public.draw_rounds
  for select using (exists (
    select 1 from public.draw_games g
    where g.id = draw_rounds.game_id
      and (g.player_a = auth.uid() or g.player_b = auth.uid())
  ));

-- Requests: both parties can see them; you may only challenge AS yourself, an accepted friend; either may remove.
drop policy if exists draw_requests_party_select on public.draw_requests;
create policy draw_requests_party_select on public.draw_requests
  for select using (requester = auth.uid() or addressee = auth.uid());

drop policy if exists draw_requests_create on public.draw_requests;
create policy draw_requests_create on public.draw_requests
  for insert with check (
    requester = auth.uid()
    and addressee <> auth.uid()
    and public.are_friends(auth.uid(), addressee)
  );

drop policy if exists draw_requests_delete on public.draw_requests;
create policy draw_requests_delete on public.draw_requests
  for delete using (requester = auth.uid() or addressee = auth.uid());

-- == grants ========================================================================
grant select                 on public.draw_games    to authenticated;   -- created/advanced via RPCs only
grant select                 on public.draw_rounds   to authenticated;
grant select, insert, delete on public.draw_requests to authenticated;

-- == helpers =======================================================================
-- Guess normalisation mirrors DrawGuessing.Normalize: lower-case, strip everything that isn't a letter/digit.
create or replace function public.draw_normalize(txt text)
returns text
language sql
immutable
as $$ select regexp_replace(lower(coalesce(txt, '')), '[^a-z0-9]', '', 'g') $$;

-- == accept_draw_request: create the game + round 1 (trust boundary) ================
create or replace function public.accept_draw_request(p_request uuid)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  r  public.draw_requests;
  g  public.draw_games;
  me uuid := auth.uid();
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

  select * into r from public.draw_requests where id = p_request for update;
  if not found then raise exception 'no such request' using errcode = 'no_data_found'; end if;
  if r.addressee <> me then raise exception 'only the invitee can accept' using errcode = '42501'; end if;

  insert into public.draw_games (player_a, player_b, whose_turn, phase, round_no)
    values (r.requester, r.addressee, r.addressee, 'guess', 1)
    returning * into g;

  insert into public.draw_rounds (game_id, round_no, drawer, guesser, difficulty, word, letter_hint, strokes)
    values (g.id, 1, r.requester, r.addressee, r.difficulty, r.word, r.letter_hint, r.strokes);

  delete from public.draw_requests where id = p_request;
  return g;
end;
$$;

-- == submit_draw_round: the current drawer submits a new round's drawing ============
create or replace function public.submit_draw_round(
  p_game uuid, p_difficulty public.draw_difficulty, p_word text, p_hint text, p_strokes text)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  g   public.draw_games;
  me  uuid := auth.uid();
  opp uuid;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;
  if p_word is null or length(btrim(p_word)) = 0 then raise exception 'empty word' using errcode = 'check_violation'; end if;

  select * into g from public.draw_games where id = p_game for update;
  if not found then raise exception 'no such game' using errcode = 'no_data_found'; end if;
  if me <> g.player_a and me <> g.player_b then raise exception 'not a player in this game' using errcode = '42501'; end if;
  if g.status <> 'in_progress' then raise exception 'game is over' using errcode = 'check_violation'; end if;
  if g.phase <> 'draw' then raise exception 'not the draw phase' using errcode = 'check_violation'; end if;
  if g.whose_turn <> me then raise exception 'not your turn' using errcode = 'check_violation'; end if;

  opp := case when me = g.player_a then g.player_b else g.player_a end;

  insert into public.draw_rounds (game_id, round_no, drawer, guesser, difficulty, word, letter_hint, strokes)
    values (g.id, g.round_no + 1, me, opp, p_difficulty, p_word, coalesce(p_hint, ''), coalesce(p_strokes, ''));

  update public.draw_games
     set round_no = g.round_no + 1, whose_turn = opp, phase = 'guess', updated_at = now()
   where id = g.id
   returning * into g;
  return g;
end;
$$;

-- == submit_draw_guess: the guesser guesses; scores mirror DrawScoring ==============
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
  attempts int;
  correct  boolean;
  base     int;
  g_pts    int;
  d_pts    int;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

  select * into r from public.draw_rounds where id = p_round for update;
  if not found then raise exception 'no such round' using errcode = 'no_data_found'; end if;
  select * into g from public.draw_games where id = r.game_id for update;

  if me <> r.guesser then raise exception 'not your round to guess' using errcode = '42501'; end if;
  if r.status <> 'guessing' then raise exception 'round is over' using errcode = 'check_violation'; end if;

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

-- == give_up_draw_round: reveal the word, no points, roles swap =====================
create or replace function public.give_up_draw_round(p_round uuid)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  r  public.draw_rounds;
  g  public.draw_games;
  me uuid := auth.uid();
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

  select * into r from public.draw_rounds where id = p_round for update;
  if not found then raise exception 'no such round' using errcode = 'no_data_found'; end if;
  select * into g from public.draw_games where id = r.game_id for update;

  if me <> r.guesser then raise exception 'not your round to guess' using errcode = '42501'; end if;

  if r.status = 'guessing' then
    update public.draw_rounds set status = 'gave_up', updated_at = now() where id = r.id;
  end if;

  update public.draw_games
     set whose_turn = r.guesser, phase = 'draw', updated_at = now()
   where id = g.id
   returning * into g;
  return g;
end;
$$;

-- == resign_draw_game: abandon an in-progress game =================================
create or replace function public.resign_draw_game(p_game uuid)
returns public.draw_games
language plpgsql
security definer
set search_path = public
as $$
declare
  g  public.draw_games;
  me uuid := auth.uid();
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;
  select * into g from public.draw_games where id = p_game for update;
  if not found then raise exception 'no such game' using errcode = 'no_data_found'; end if;
  if me <> g.player_a and me <> g.player_b then raise exception 'not a player in this game' using errcode = '42501'; end if;
  if g.status <> 'in_progress' then return g; end if;

  update public.draw_games set status = 'abandoned', updated_at = now() where id = g.id returning * into g;
  return g;
end;
$$;

grant execute on function public.accept_draw_request(uuid)                                                 to authenticated;
grant execute on function public.submit_draw_round(uuid, public.draw_difficulty, text, text, text)         to authenticated;
grant execute on function public.submit_draw_guess(uuid, text)                                             to authenticated;
grant execute on function public.give_up_draw_round(uuid)                                                  to authenticated;
grant execute on function public.resign_draw_game(uuid)                                                    to authenticated;

-- == per-player rate limit =========================================================
-- Caps how fast one player can submit rounds (drawings normally arrive via the RPC, but the trigger bounds a
-- direct flood too). Turn-based play is nowhere near 20 rounds / 10s.
create or replace function public.enforce_draw_round_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  recent integer;
begin
  select count(*) into recent
  from public.draw_rounds
  where drawer = new.drawer
    and created_at > now() - interval '10 seconds';

  if recent >= 20 then
    raise exception 'Rate limit: too many drawings too quickly. Slow down.' using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

drop trigger if exists draw_rounds_rate_limit on public.draw_rounds;
create trigger draw_rounds_rate_limit
  before insert on public.draw_rounds
  for each row execute function public.enforce_draw_round_rate_limit();

-- == cleanup / retention ===========================================================
-- A player may delete a game they're in (rounds cascade via the FK).
drop policy if exists draw_games_delete on public.draw_games;
create policy draw_games_delete on public.draw_games
  for delete using (player_a = auth.uid() or player_b = auth.uid());

grant delete on public.draw_games to authenticated;

-- Retention sweep: drop abandoned games and stale invites. SECURITY DEFINER, NOT granted to authenticated.
create or replace function public.cleanup_old_draw_games(older_than interval default interval '2 days')
returns integer
language plpgsql
security definer
set search_path = public
as $$
declare
  removed integer;
begin
  delete from public.draw_games
  where status <> 'in_progress'
    and updated_at < now() - older_than;
  get diagnostics removed = row_count;

  -- Sweep abandoned/never-accepted invites too (a fortnight is generous for an async game).
  delete from public.draw_requests where created_at < now() - interval '14 days';
  return removed;
end;
$$;

do $$
begin
  if exists (select 1 from pg_extension where extname = 'pg_cron') then
    perform cron.schedule('perch-draw-cleanup', '30 4 * * *', 'select public.cleanup_old_draw_games()');
  end if;
exception when others then
  null;
end $$;

-- == realtime ======================================================================
-- Live turns: add the tables to the realtime publication so Postgres Changes fires. Guarded so the migration
-- still applies where the publication hasn't been created (it must be enabled in the Supabase dashboard; verify
-- delivery live -- no migration creates the publication).
do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    begin alter publication supabase_realtime add table public.draw_rounds;   exception when duplicate_object then null; end;
    begin alter publication supabase_realtime add table public.draw_games;    exception when duplicate_object then null; end;
    begin alter publication supabase_realtime add table public.draw_requests; exception when duplicate_object then null; end;
  end if;
end $$;
