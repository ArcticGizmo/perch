-- Perch Social — read friends' profiles
-- The friends list, incoming requests, and feed authors all need to show a handle/name, which means reading
-- OTHER people's profile rows — but profiles_self_select only exposes your own. This adds a second
-- (permissive, so OR-combined) SELECT policy letting you read the profile of anyone you share a friendship
-- edge with (accepted OR pending, either direction). Strangers stay unreachable except via find_profile
-- (exact handle). Uses a SECURITY DEFINER helper to keep the policy simple and avoid correlated-subquery
-- ambiguity.

create or replace function public.shares_edge(a uuid, b uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.friendships
    where (requester = a and addressee = b)
       or (requester = b and addressee = a)
  );
$$;

grant execute on function public.shares_edge(uuid, uuid) to authenticated;

drop policy if exists profiles_friends_select on public.profiles;
create policy profiles_friends_select on public.profiles
  for select using (id = auth.uid() or public.shares_edge(auth.uid(), id));
