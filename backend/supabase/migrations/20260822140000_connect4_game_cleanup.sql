-- Perch — Connect 4 game cleanup / retention
-- Each match is its own row (a rematch is a new game, not a reuse), so finished games would otherwise pile up.
-- This lets a player delete a game they're in (its moves cascade via the FK) and adds a retention sweep that
-- drops decided games after a couple of days, scheduled with pg_cron when the extension is available.

-- A player may delete a game they're in. moves rows are removed automatically by moves.game_id's ON DELETE
-- CASCADE (the cascade runs as the table owner, so no moves delete grant is needed).
drop policy if exists games_delete on public.games;
create policy games_delete on public.games
  for delete using (player_red = auth.uid() or player_yellow = auth.uid());

grant delete on public.games to authenticated;

-- Retention sweep: drop decided games (won / draw / abandoned) older than the cutoff. SECURITY DEFINER so a
-- scheduled job can run it; deliberately NOT granted to `authenticated` — it's a global sweep, not a per-user
-- action (users remove their own games through the RLS delete policy above). Returns how many it removed.
create or replace function public.cleanup_old_games(older_than interval default interval '2 days')
returns integer
language plpgsql
security definer
set search_path = public
as $$
declare
  removed integer;
begin
  delete from public.games
  where status <> 'in_progress'
    and updated_at < now() - older_than;
  get diagnostics removed = row_count;
  return removed;
end;
$$;

-- Schedule the daily sweep when pg_cron is installed (enable the extension in the Supabase dashboard).
-- Best-effort and guarded: the migration still applies where pg_cron isn't present, and cron.schedule's named
-- form replaces the job rather than duplicating it on re-run. If anything about the call differs, the sweep can
-- still be run manually (`select public.cleanup_old_games();`).
do $$
begin
  if exists (select 1 from pg_extension where extname = 'pg_cron') then
    perform cron.schedule('perch-connect4-cleanup', '0 4 * * *', 'select public.cleanup_old_games()');
  end if;
exception when others then
  null;
end $$;
