-- Perch — Connect 4 game requests (invite / accept)
-- A game must be agreed by BOTH players before it exists, so an invite lives in its own table until accepted:
-- accepting creates the games row and removes the request; declining or cancelling just deletes the request.
-- Nothing lands in the games table until there's a real, mutually-agreed game.

create table if not exists public.game_requests (
  id         uuid primary key default gen_random_uuid(),
  requester  uuid not null references public.profiles(id) on delete cascade,  -- would play red (moves first)
  addressee  uuid not null references public.profiles(id) on delete cascade,  -- must accept to start
  created_at timestamptz not null default now(),
  check (requester <> addressee),
  unique (requester, addressee)    -- at most one outstanding invite per direction
);
create index if not exists game_requests_addressee on public.game_requests (addressee, created_at desc);

alter table public.game_requests enable row level security;

-- Visible to the two parties only — a third party can't see that an invite exists.
drop policy if exists game_requests_party_select on public.game_requests;
create policy game_requests_party_select on public.game_requests
  for select using (requester = auth.uid() or addressee = auth.uid());

-- You may only invite AS yourself, and only an accepted friend (are_friends folds in the block check).
drop policy if exists game_requests_create on public.game_requests;
create policy game_requests_create on public.game_requests
  for insert with check (
    requester = auth.uid()
    and addressee <> auth.uid()
    and public.are_friends(auth.uid(), addressee)
  );

-- Either party may remove it: the requester cancels, the addressee declines.
drop policy if exists game_requests_delete on public.game_requests;
create policy game_requests_delete on public.game_requests
  for delete using (requester = auth.uid() or addressee = auth.uid());

grant select, insert, delete on public.game_requests to authenticated;

-- accept_game_request: the invitee accepts, which creates the game (requester = red and moves first) and
-- deletes the request. SECURITY DEFINER so it can write games past RLS, but it re-checks that auth.uid() is
-- the addressee — only the person invited can accept.
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

  delete from public.game_requests where id = p_request;
  return g;
end;
$$;

grant execute on function public.accept_game_request(uuid) to authenticated;

-- Realtime: the invitee sees a new request land (and both sides see it clear on accept/decline).
do $$
begin
  if exists (select 1 from pg_publication where pubname = 'supabase_realtime') then
    begin alter publication supabase_realtime add table public.game_requests; exception when duplicate_object then null; end;
  end if;
end $$;
