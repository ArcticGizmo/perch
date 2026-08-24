-- Perch — Connect 4 per-player move rate limit
-- Mirrors the posts rate limit: a BEFORE INSERT trigger caps how fast one player can add moves, so a tampered
-- or scripted client can't flood the moves table. Moves normally arrive through the SECURITY DEFINER
-- drop_disc() RPC, but the trigger fires for every inserted row regardless, so it bounds a direct flood too.
-- Turn-based play is nowhere near the ceiling (30 moves / 10s per player = 3/s).

create or replace function public.enforce_move_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  recent integer;
begin
  select count(*) into recent
  from public.moves
  where mover = new.mover
    and created_at > now() - interval '10 seconds';

  if recent >= 30 then
    raise exception 'Rate limit: too many moves too quickly. Slow down.'
      using errcode = 'check_violation';
  end if;

  return new;
end;
$$;

drop trigger if exists moves_rate_limit on public.moves;
create trigger moves_rate_limit
  before insert on public.moves
  for each row execute function public.enforce_move_rate_limit();
