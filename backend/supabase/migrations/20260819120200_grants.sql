-- Perch Social — table privileges (grants)
-- RLS decides WHICH rows a request may touch, but PostgreSQL still needs a table-level GRANT before the
-- `authenticated` role can touch the table at all. Supabase usually sets these via default privileges, but
-- granting them explicitly makes the schema self-contained — and fixes the 403 "permission denied for
-- table" you hit claiming a handle when it doesn't. `anon` gets nothing: Social requires a signed-in user.

grant usage on schema public to anon, authenticated;

grant select, insert, update, delete on public.profiles    to authenticated;
grant select, insert, update, delete on public.friendships to authenticated;
grant select, insert, update, delete on public.posts       to authenticated;
