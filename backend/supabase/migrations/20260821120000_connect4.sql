-- Perch — networked Connect 4 ("play a friend")
-- Two tables (games, moves), their RLS + grants, a validated drop_disc() RPC (the trust boundary), a resign
-- RPC, and the realtime publication for live turns. Every rule mirrors the pure Perch.Games.Connect4Game
-- engine; the DATABASE is authoritative so a tampered client can't cheat. See docs/connect4-plan.md.

-- ── enums ───────────────────────────────────────────────────────────────────────
do $$ begin
  create type public.game_status as enum ('in_progress','red_won','yellow_won','draw','abandoned');
exception when duplicate_object then null; end $$;

do $$ begin
  create type public.game_turn as enum ('red','yellow');
exception when duplicate_object then null; end $$;

-- ── games ─────────────────────────────────────────────────────────────────────
-- One row per match. The creator is red and moves first. status/turn/winner are owned by the server (the
-- RPCs below) — there is deliberately no client UPDATE grant, so the only way state advances is a validated
-- move.
create table if not exists public.games (
  id            uuid primary key default gen_random_uuid(),
  player_red    uuid not null references public.profiles(id) on delete cascade,
  player_yellow uuid not null references public.profiles(id) on delete cascade,
  status        public.game_status not null default 'in_progress',
  turn          public.game_turn   not null default 'red',
  move_count    int  not null default 0,
  winner        public.game_turn,                      -- set on a win, else null
  created_at    timestamptz not null default now(),
  updated_at    timestamptz not null default now(),
  check (player_red <> player_yellow)
);
create index if not exists games_red    on public.games (player_red,    updated_at desc);
create index if not exists games_yellow on public.games (player_yellow, updated_at desc);

-- ── moves ─────────────────────────────────────────────────────────────────────
-- Append-only, one row per disc dropped. (game_id, ply) is unique, so a double-submit or a replay can't add
-- a duplicate. Clients never INSERT here directly (no insert policy/grant) — every move goes through
-- drop_disc(), which validates first.
create table if not exists public.moves (
  id         uuid primary key default gen_random_uuid(),
  game_id    uuid not null references public.games(id) on delete cascade,
  mover      uuid not null references public.profiles(id) on delete cascade,
  ply        int  not null check (ply >= 0),          -- 0-based; move 0 is red
  col        int  not null check (col between 0 and 6),
  created_at timestamptz not null default now(),
  unique (game_id, ply)
);
create index if not exists moves_game on public.moves (game_id, ply);

-- ── RLS ─────────────────────────────────────────────────────────────────────────
alter table public.games enable row level security;
alter table public.moves enable row level security;

-- A game and its moves are visible only to the two players — a third party can't even see the game exists.
drop policy if exists games_party_select on public.games;
create policy games_party_select on public.games
  for select using (player_red = auth.uid() or player_yellow = auth.uid());

-- You may create a game only as red (the creator, who moves first), against an accepted friend. are_friends()
-- folds in the block check, so you can't invite someone who has blocked you (or whom you've blocked).
drop policy if exists games_create on public.games;
create policy games_create on public.games
  for insert with check (
    player_red = auth.uid()
    and player_yellow <> auth.uid()
    and public.are_friends(auth.uid(), player_yellow)
  );

-- No client UPDATE/DELETE policy on games: status transitions are the server's job (drop_disc / resign_game).

-- Moves are readable by the game's two players. There is NO insert policy, so a raw insert is denied by RLS —
-- moves may only be written by the SECURITY DEFINER RPC, which validates the move first.
drop policy if exists moves_party_select on public.moves;
create policy moves_party_select on public.moves
  for select using (exists (
    select 1 from public.games g
    where g.id = moves.game_id
      and (g.player_red = auth.uid() or g.player_yellow = auth.uid())
  ));

-- ── grants ────────────────────────────────────────────────────────────────────
-- select+insert on games (insert gated by games_create); select only on moves (no direct writes).
grant select, insert on public.games to authenticated;
grant select         on public.moves to authenticated;

-- ── win detection ───────────────────────────────────────────────────────────────
-- Reconstructs the board from a game's moves (colour by ply parity, stack per column) and reports whether any
-- four-in-a-row exists. Mirrors Connect4Game's four-axis scan. Called by drop_disc as the definer, so no grant
-- is needed. 'n' marks an empty cell (array_fill can't seed nulls portably).
create or replace function public.connect4_has_win(p_game uuid)
returns boolean
language plpgsql
stable
security definer
set search_path = public
as $$
declare
  board   text[]  := array_fill('n', array[7, 6]);     -- [col+1][row+1], 'r' | 'y' | 'n'
  heights int[]   := array[0,0,0,0,0,0,0];
  m       record;
  colr    text;
  c int; r int; d int; dc int; dr int; cc int; rr int; k int; cnt int;
  dirs int[] := array[1,0, 0,1, 1,1, 1,-1];            -- four (dc,dr) axes, flattened
begin
  -- Replay moves in ply order; even ply = red, odd = yellow.
  for m in select col, ply from public.moves where game_id = p_game order by ply loop
    colr := case when m.ply % 2 = 0 then 'r' else 'y' end;
    c := m.col;
    r := heights[c + 1];
    board[c + 1][r + 1] := colr;
    heights[c + 1] := heights[c + 1] + 1;
  end loop;

  -- From every filled cell, count a run of the same colour along each of the four axes (positive direction
  -- only — every run is caught from its starting cell).
  for c in 0..6 loop
    for r in 0..5 loop
      if board[c + 1][r + 1] = 'n' then continue; end if;
      for d in 0..3 loop
        dc := dirs[d * 2 + 1];
        dr := dirs[d * 2 + 2];
        cnt := 1; k := 1;
        loop
          cc := c + dc * k; rr := r + dr * k;
          exit when cc < 0 or cc > 6 or rr < 0 or rr > 5;
          exit when board[cc + 1][rr + 1] is distinct from board[c + 1][r + 1];
          cnt := cnt + 1; k := k + 1;
        end loop;
        if cnt >= 4 then return true; end if;
      end loop;
    end loop;
  end loop;
  return false;
end;
$$;

-- NOTE: this version has a bug — array_fill('n', ...) passes an untyped literal to a polymorphic function and
-- fails at call time with "could not determine polymorphic type". It is left as-applied for an honest history;
-- the fix is migration 20260822120000_connect4_fix.sql, which replaces this function.

-- ── drop_disc: the validated move (trust boundary) ────────────────────────────────
-- Validates and applies one move as the calling player, then advances the game. SECURITY DEFINER so it can
-- write games/moves past RLS, but it re-checks auth.uid() itself — never trusts the client. Returns the
-- updated game row.
create or replace function public.drop_disc(p_game uuid, p_col int)
returns public.games
language plpgsql
security definer
set search_path = public
as $$
declare
  g       public.games;
  me      uuid := auth.uid();
  my_turn public.game_turn;
  filled  int;
  win     boolean;
  next    int;
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;
  if p_col < 0 or p_col > 6 then raise exception 'illegal column' using errcode = 'check_violation'; end if;

  -- Lock the game row so two racing drops serialise.
  select * into g from public.games where id = p_game for update;
  if not found then raise exception 'no such game' using errcode = 'no_data_found'; end if;
  if me <> g.player_red and me <> g.player_yellow then
    raise exception 'not a player in this game' using errcode = '42501';
  end if;
  if g.status <> 'in_progress' then raise exception 'game is over' using errcode = 'check_violation'; end if;

  my_turn := case when me = g.player_red then 'red' else 'yellow' end;
  if my_turn <> g.turn then raise exception 'not your turn' using errcode = 'check_violation'; end if;

  select count(*) into filled from public.moves where game_id = p_game and col = p_col;
  if filled >= 6 then raise exception 'column is full' using errcode = 'check_violation'; end if;

  insert into public.moves (game_id, mover, ply, col) values (p_game, me, g.move_count, p_col);

  win  := public.connect4_has_win(p_game);
  next := g.move_count + 1;

  update public.games
     set move_count = next,
         status = case
                    when win and my_turn = 'red'    then 'red_won'::public.game_status
                    when win and my_turn = 'yellow' then 'yellow_won'::public.game_status
                    when next >= 42                  then 'draw'::public.game_status
                    else 'in_progress'::public.game_status
                  end,
         winner = case when win then my_turn else null end,
         turn   = case
                    when win or next >= 42 then g.turn
                    when g.turn = 'red' then 'yellow'::public.game_turn
                    else 'red'::public.game_turn
                  end,
         updated_at = now()
   where id = p_game
   returning * into g;

  return g;
end;
$$;

-- ── resign_game: concede an in-progress game ──────────────────────────────────────
create or replace function public.resign_game(p_game uuid)
returns public.games
language plpgsql
security definer
set search_path = public
as $$
declare
  g  public.games;
  me uuid := auth.uid();
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;
  select * into g from public.games where id = p_game for update;
  if not found then raise exception 'no such game' using errcode = 'no_data_found'; end if;
  if me <> g.player_red and me <> g.player_yellow then
    raise exception 'not a player in this game' using errcode = '42501';
  end if;
  if g.status <> 'in_progress' then return g; end if;   -- already decided: no-op

  update public.games
     set status  = case when me = g.player_red then 'yellow_won' else 'red_won' end::public.game_status,
         winner  = case when me = g.player_red then 'yellow' else 'red' end::public.game_turn,
         updated_at = now()
   where id = p_game
   returning * into g;
  return g;
end;
$$;

grant execute on function public.drop_disc(uuid, int) to authenticated;
grant execute on function public.resign_game(uuid)    to authenticated;

-- ── realtime ──────────────────────────────────────────────────────────────────
-- Live turns: add moves + games to the realtime publication so Postgres Changes fires. No existing migration
-- touches this publication (even posts relies on it being enabled), so do it explicitly — and guard it so the
-- migration still applies on a project where the publication hasn't been created yet.
do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    begin alter publication supabase_realtime add table public.moves; exception when duplicate_object then null; end;
    begin alter publication supabase_realtime add table public.games; exception when duplicate_object then null; end;
  end if;
end $$;
