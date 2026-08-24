-- Perch — Connect 4 challenge carries the opening move
-- Feedback: when you invite someone, you make the FIRST MOVE first, then the invite is sent — so the moment
-- the friend accepts it's already their turn and they're never left waiting. The invite therefore carries the
-- inviter's opening column, and accept_game_request seeds it as move 0 (red) when creating the game.
--
-- The snappy invite/accept/nudge/rematch delivery rides Supabase Realtime BROADCAST (transient, no DB rows) on
-- a per-user inbox topic ("perch:inbox:<uid>"); it is a best-effort accelerator only — these persistent rows and
-- the client's poll remain the source of truth, so a missed broadcast just means "a little slower". Broadcast
-- needs no table. (If this project later turns on Realtime Authorization / private channels, add a
-- realtime.messages policy for the inbox topics; the public publishable-key + JWT path used here does not.)

-- The opening move the inviter played, carried on the invite. Nullable so any pre-existing invite still accepts
-- (falls back to no seeded move); every invite the client sends now includes it.
alter table public.game_requests
  add column if not exists first_col int check (first_col between 0 and 6);

-- Recreate accept_game_request so it seeds the inviter's opening move (move 0 = red) and hands the turn to the
-- invitee. Still SECURITY DEFINER (writes games/moves past RLS) but re-checks auth.uid() is the addressee.
create or replace function public.accept_game_request(p_request uuid)
returns public.games
language plpgsql
security definer
set search_path = public
as $$
declare
  r  public.game_requests;
  g  public.games;
  me uuid := auth.uid();
begin
  if me is null then raise exception 'not authenticated' using errcode = '28000'; end if;

  select * into r from public.game_requests where id = p_request for update;
  if not found then raise exception 'no such request' using errcode = 'no_data_found'; end if;
  if r.addressee <> me then raise exception 'only the invitee can accept' using errcode = '42501'; end if;

  insert into public.games (player_red, player_yellow)
    values (r.requester, r.addressee)
    returning * into g;

  -- Seed the inviter's opening move (if the invite carried one), so the accepter is immediately on their turn.
  if r.first_col is not null then
    insert into public.moves (game_id, mover, ply, col) values (g.id, r.requester, 0, r.first_col);
    update public.games
       set move_count = 1,
           turn       = 'yellow'::public.game_turn,   -- red moved (move 0); it's yellow's (the accepter's) turn
           updated_at = now()
     where id = g.id
     returning * into g;
  end if;

  delete from public.game_requests where id = p_request;
  return g;
end;
$$;

grant execute on function public.accept_game_request(uuid) to authenticated;
