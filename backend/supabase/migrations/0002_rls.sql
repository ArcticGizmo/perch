-- Perch Social — Row-Level Security (M0)
-- RLS is THE security boundary: the anon key is public by design, so every table must have RLS
-- on and correct policies. A table with RLS off would be wide open to anyone holding that key.
-- See docs/social-feed-implementation.md §7.

alter table public.profiles    enable row level security;
alter table public.friendships enable row level security;
alter table public.posts       enable row level security;

-- ── profiles ────────────────────────────────────────────────────────────────
-- Your own row is fully readable; other profiles are reachable ONLY through find_profile()
-- (exact handle), so the base table is not open to a blanket SELECT — no user enumeration.
drop policy if exists profiles_self_select on public.profiles;
create policy profiles_self_select on public.profiles
  for select using (id = auth.uid());

drop policy if exists profiles_self_insert on public.profiles;
create policy profiles_self_insert on public.profiles
  for insert with check (id = auth.uid());

drop policy if exists profiles_self_update on public.profiles;
create policy profiles_self_update on public.profiles
  for update using (id = auth.uid()) with check (id = auth.uid());

-- ── friendships ───────────────────────────────────────────────────────────────
-- A friendship row is visible only to its two parties — a third party can't even see that
-- two people are connected.
drop policy if exists friendships_party_select on public.friendships;
create policy friendships_party_select on public.friendships
  for select using (requester = auth.uid() or addressee = auth.uid());

-- You may only create a request AS the requester (never forge one from someone else).
drop policy if exists friendships_request on public.friendships;
create policy friendships_request on public.friendships
  for insert with check (requester = auth.uid());

-- Either party may update their edge (addressee accepts/declines; either can block).
drop policy if exists friendships_respond on public.friendships;
create policy friendships_respond on public.friendships
  for update using (requester = auth.uid() or addressee = auth.uid());

-- Either party may remove the edge (decline / unfriend).
drop policy if exists friendships_delete on public.friendships;
create policy friendships_delete on public.friendships
  for delete using (requester = auth.uid() or addressee = auth.uid());

-- ── posts ────────────────────────────────────────────────────────────────────
-- The heart of it: you can read a post only if you wrote it OR you're an accepted friend of
-- the author. This is the rule FakeSocialClient mirrors in the app.
drop policy if exists posts_read on public.posts;
create policy posts_read on public.posts
  for select using (author = auth.uid() or public.are_friends(auth.uid(), author));

-- You can only post AS yourself. (Length is enforced by the table CHECK, not here.)
drop policy if exists posts_write on public.posts;
create policy posts_write on public.posts
  for insert with check (author = auth.uid());

-- You can only delete your own posts.
drop policy if exists posts_delete on public.posts;
create policy posts_delete on public.posts
  for delete using (author = auth.uid());

-- Let signed-in users call the helper RPCs.
grant execute on function public.are_friends(uuid, uuid) to authenticated;
grant execute on function public.find_profile(text)      to authenticated;
