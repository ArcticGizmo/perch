-- Perch Social -- least-privilege grants + server-owned fields (review fixes CP3, docs/review-fixes-plan.md)
--
-- The problem: the schema never owned its own privileges.
--   Supabase's default privileges grant ALL (arwdDxtm: SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES,
--   TRIGGER, MAINTAIN) on every new table in `public` to anon, authenticated AND service_role. The earlier
--   `grant ...` migrations only ever ADDED privileges on top of that, so every table was fully open at the
--   privilege layer and RLS was the only thing standing in the way -- and RLS doesn't cover TRUNCATE,
--   REFERENCES, TRIGGER or MAINTAIN. Comments like "select only on moves (no direct writes)" or "NO grants to
--   authenticated" (moderation) were never true. Every policy was also written without a TO clause, so it
--   applied to PUBLIC (anon included).
--
--   On top of that (the CP3 findings):
--   - Forged games. games_create still allowed a direct INSERT into games, where the client chose status,
--     winner, turn, move_count and updated_at -- forged results that sort first forever and escape cleanup.
--   - Backdated rows. posts.created_at was client-writable and the flood guard stamped
--     last_posted_at = new.created_at, so `created_at: "1970-01-01"` switched the rate limit off. The same
--     client-chosen timestamps existed on every table.
--   - Found while verifying: 20260918's `grant update (handle, display_name, mood_emoji)` on profiles broke the
--     client's claim-handle upsert outright (PostgREST's ON CONFLICT DO UPDATE also sets id). Fixed below.
--
-- The fix: deny by default, then grant back exactly what the app uses.
--   1. Schemas: nobody but the owner creates in `public`; anon loses USAGE on `public` altogether (Social
--      requires sign-in, so the bare publishable key has no business in the Data API). Server-owned state
--      (the flood-guard stamp) moves out of `profiles` into an unreachable private table.
--   2. Tables: REVOKE ALL from PUBLIC/anon/authenticated/service_role on every table, then GRANT back per table
--      the operations the client performs -- and for INSERT/UPDATE, only the columns it sends. Generated ids,
--      timestamps and game state are never client-writable. anon gets nothing. service_role gets only the
--      moderation surface.
--   3. Policies: every policy is re-scoped from PUBLIC to `authenticated`.
--   4. Functions: REVOKE ALL on every (non-extension) function in public/private, then GRANT EXECUTE back on
--      the explicit allowlist: the client RPCs + RLS helpers to authenticated, the sweeps to service_role.
--      Trigger functions keep no grants at all (a trigger doesn't need EXECUTE to fire).
--   5. Default privileges: new tables/sequences/functions created by `postgres` in `public` no longer grant
--      anything to anon/authenticated/service_role. A new table is closed until a migration grants it.
--   6. CP3: games are born only in accept_game_request (games_create dropped, no INSERT grant); BEFORE triggers
--      stamp created_at/updated_at with now() on every table, and the flood guard stamps last_posted_at = now().
--
-- `security_test.sql` pins the resulting privilege matrix exactly, so any future grant (or a missing revoke on
-- a new table) fails the suite.

-- == 1. schemas =========================================================================================
revoke create on schema public from public, anon, authenticated, service_role;
-- USAGE on `public` came from PUBLIC (=U) as well as an explicit anon grant (grants.sql); drop both, then
-- restate the two client roles that genuinely need it.
revoke usage on schema public from public, anon;
grant  usage on schema public to authenticated, service_role;

revoke all   on schema private from public, anon, service_role;
grant  usage on schema private to authenticated;          -- RLS policies call the private helpers as the user

-- == 1b. server-owned state lives where clients can't reach it ==========================================
-- profiles.last_posted_at (the flood guard's stamp) was a server-owned column on a client-readable table: every
-- friend could read when you last posted, and hiding it with a column grant breaks the client's claim-handle
-- upsert (PostgREST's return=representation is RETURNING *, which needs SELECT on every column). It moves to
-- a private table with no grants at all; only the SECURITY DEFINER post triggers (owner) touch it.
create table if not exists private.post_throttle (
  profile        uuid primary key references public.profiles(id) on delete cascade,
  last_posted_at timestamptz not null
);
alter table private.post_throttle enable row level security;   -- no policies: defence in depth behind no grants
revoke all on table private.post_throttle from public, anon, authenticated, service_role;

do $$
begin
  if exists (select 1 from information_schema.columns
              where table_schema = 'public' and table_name = 'profiles' and column_name = 'last_posted_at') then
    insert into private.post_throttle (profile, last_posted_at)
      select id, last_posted_at from public.profiles where last_posted_at is not null
      on conflict (profile) do nothing;
    alter table public.profiles drop column last_posted_at;
  end if;
end $$;

-- == 2. tables: wipe every client privilege =============================================================
-- Table-level REVOKE ALL also drops any column-level grants (e.g. profiles' UPDATE (handle, ...)).
revoke all on all tables    in schema public from public, anon, authenticated, service_role;
revoke all on all sequences in schema public from public, anon, authenticated, service_role;

-- == 2b. tables: grant back exactly what the client does ================================================
-- Rule: SELECT is table-level (RLS filters rows); INSERT/UPDATE are column lists naming only what the client
-- sends. Anything server-owned -- ids, created_at/updated_at, last_posted_at, game state -- is absent, so a
-- request that tries to write it fails with 42501 instead of being honoured.

-- profiles: read (RLS: self + friendship edge), claim/edit your handle. ClaimHandleAsync is a PostgREST
-- merge-duplicates upsert, which compiles to ON CONFLICT (id) DO UPDATE SET <every sent column> -- id
-- included -- so UPDATE must cover id. That is safe: profiles_self_update's USING and WITH CHECK both pin
-- id = auth.uid(), so id can only ever be "changed" to itself. (20260918's narrower UPDATE grant left id out,
-- which made every claim/edit fail with 403.) No DELETE: erasure is the delete-account function.
grant select                                         on public.profiles to authenticated;
grant insert (id, handle, display_name, mood_emoji)  on public.profiles to authenticated;
grant update (id, handle, display_name, mood_emoji)  on public.profiles to authenticated;

-- friendships: request (pending only), accept (addressee only), decline/unfriend. UPDATE keeps requester and
-- addressee because already-shipped clients send friend requests as a merge-duplicates upsert, whose
-- ON CONFLICT DO UPDATE sets every sent column (see CP2); friendships_guard still makes the parties immutable.
grant select                                   on public.friendships to authenticated;
grant insert (requester, addressee, status)    on public.friendships to authenticated;
grant update (requester, addressee, status)    on public.friendships to authenticated;
grant delete                                   on public.friendships to authenticated;

-- posts: read the feed, post a status. No UPDATE; no DELETE either -- nothing in the client deletes a post
-- (keep-latest prunes as the owner, erasure cascades as the owner).
grant select                          on public.posts to authenticated;
grant insert (author, body, mood_emoji) on public.posts to authenticated;
drop policy if exists posts_delete on public.posts;

-- reactions: read, react, un-react.
grant select                            on public.reactions to authenticated;
grant insert (post_id, reactor, emoji)  on public.reactions to authenticated;
grant delete                            on public.reactions to authenticated;

-- blocks: your own block list (RLS: blocker = you).
grant select                     on public.blocks to authenticated;
grant insert (blocker, blocked)  on public.blocks to authenticated;
grant delete                     on public.blocks to authenticated;

-- reports: write-only queue -- a user files a report and can never read one back.
grant insert (reporter, reported, reason) on public.reports to authenticated;

-- moderation: no client access at all (a user can't discover or lift their own suspension).

-- games: read + delete your own. No INSERT (CP3): a game is born only in accept_game_request. No UPDATE:
-- state moves only through drop_disc / resign_game.
grant select, delete on public.games to authenticated;

-- moves: read-only; written only by drop_disc / accept_game_request.
grant select on public.moves to authenticated;

-- game_requests: invite (carrying your opening column), see, cancel/decline.
grant select                                  on public.game_requests to authenticated;
grant insert (requester, addressee, first_col) on public.game_requests to authenticated;
grant delete                                  on public.game_requests to authenticated;

-- draw_games / draw_rounds: read (+ delete your own game); every transition is an RPC.
grant select, delete on public.draw_games  to authenticated;
grant select         on public.draw_rounds to authenticated;

-- draw_requests: challenge (carrying your first drawing), see, cancel/decline.
grant select on public.draw_requests to authenticated;
grant insert (requester, addressee, difficulty, word, letter_hint, strokes) on public.draw_requests to authenticated;
grant delete on public.draw_requests to authenticated;

-- service_role (the server-side secret key) bypasses RLS, so it gets only the moderation surface: suspend/lift
-- and work the report queue. Everything else it needs runs through SECURITY DEFINER functions (the sweeps) or
-- the auth admin API (account deletion cascades as the table owner). The dashboard runs as postgres.
grant select, insert, update, delete on public.moderation to service_role;
grant select, delete                 on public.reports    to service_role;

-- == 3. policies: authenticated only ====================================================================
-- Every policy was created without TO, i.e. for PUBLIC. anon now has no table privileges, so they could never
-- fire for it, but say so explicitly rather than rely on that.
do $$
declare p record;
begin
  for p in select policyname, tablename from pg_policies where schemaname = 'public' loop
    execute format('alter policy %I on public.%I to authenticated', p.policyname, p.tablename);
  end loop;
end $$;

-- CP3: no direct game creation. (The INSERT grant is already gone; the policy goes too so nothing invites
-- re-granting it.)
drop policy if exists games_create on public.games;

-- == 4. functions: wipe, then an explicit allowlist =====================================================
-- Every function in public/private that isn't part of an extension (citext's operators must stay callable).
-- Postgres grants EXECUTE to PUBLIC on every new function; this removes it along with the Supabase defaults.
do $$
declare f regprocedure;
begin
  for f in
    select p.oid::regprocedure
    from pg_proc p
    where p.pronamespace in ('public'::regnamespace, 'private'::regnamespace)
      and not exists (select 1 from pg_depend d
                      where d.classid = 'pg_proc'::regclass and d.objid = p.oid and d.deptype = 'e')
  loop
    execute format('revoke all on routine %s from public, anon, authenticated, service_role', f);
  end loop;
end $$;

-- The client RPCs (each re-checks auth.uid() itself).
grant execute on function
  public.find_profile(text),
  public.list_blocked(),
  public.drop_disc(uuid, int),
  public.resign_game(uuid),
  public.accept_game_request(uuid),
  public.accept_draw_request(uuid),
  public.submit_draw_round(uuid, public.draw_difficulty, text, text, text),
  public.submit_draw_guess(uuid, text),
  public.give_up_draw_round(uuid),
  public.resign_draw_game(uuid)
to authenticated;

-- The RLS helpers: policies evaluate as the signed-in user, so authenticated must be able to call them.
grant execute on function
  private.are_friends(uuid, uuid),
  private.is_blocked(uuid, uuid),
  private.shares_edge(uuid, uuid),
  private.is_suspended(uuid),
  private.can_see_post(uuid)
to authenticated;

-- Owner-only internals, runnable by service_role for manual maintenance (pg_cron runs them as the owner).
grant execute on function
  public.cleanup_old_games(interval),
  public.cleanup_old_draw_games(interval),
  public.connect4_has_win(uuid)
to service_role;

-- Everything else (draw_normalize, every trigger function) is callable by its owner only.

-- == 5. default privileges: new objects start closed ====================================================
-- Supabase's `alter default privileges for role postgres in schema public grant all ... to anon,
-- authenticated, service_role` is what made every table open. Reverse it for objects migrations create.
-- (The parallel entry for supabase_admin can't be altered from a migration; supabase_admin creates platform
-- objects, not ours.) Postgres' own PUBLIC EXECUTE default on functions is global, not per schema -- it is
-- left alone so extensions installed later keep working; section 4's sweep and the pgTAP tripwire in
-- security_test.sql cover our functions instead.
alter default privileges for role postgres in schema public revoke all on tables    from public, anon, authenticated, service_role;
alter default privileges for role postgres in schema public revoke all on sequences from public, anon, authenticated, service_role;
alter default privileges for role postgres in schema public revoke all on functions from anon, authenticated, service_role;

-- == 6. CP3: server-owned timestamps ====================================================================
-- Column grants already keep clients away from these columns; the triggers make the server the only source of
-- truth even for SECURITY DEFINER paths and future grants. Not SECURITY DEFINER: they only touch NEW.
create or replace function private.stamp_created_at()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  new.created_at := now();
  return new;
end;
$$;

create or replace function private.stamp_updated_at()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  new.updated_at := now();
  return new;
end;
$$;

revoke all on function private.stamp_created_at(), private.stamp_updated_at()
  from public, anon, authenticated, service_role;

do $$
declare t text;
begin
  foreach t in array array[
    'profiles', 'friendships', 'posts', 'reactions', 'blocks', 'reports',
    'games', 'moves', 'game_requests', 'draw_games', 'draw_rounds', 'draw_requests'
  ] loop
    execute format('drop trigger if exists stamp_created_at on public.%I', t);
    execute format('create trigger stamp_created_at before insert on public.%I '
                   'for each row execute function private.stamp_created_at()', t);
  end loop;

  foreach t in array array['games', 'draw_games', 'draw_rounds', 'moderation'] loop
    execute format('drop trigger if exists stamp_updated_at on public.%I', t);
    execute format('create trigger stamp_updated_at before insert or update on public.%I '
                   'for each row execute function private.stamp_updated_at()', t);
  end loop;
end $$;

-- The flood guard reads and writes private.post_throttle (section 1b), and its stamp is the server clock --
-- never the row's (formerly client-supplied) created_at.
create or replace function public.enforce_post_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  last_at timestamptz;
begin
  select last_posted_at into last_at from private.post_throttle where profile = new.author;
  if last_at is not null and now() - last_at < interval '5 seconds' then
    raise exception 'Rate limit: you are posting too quickly. Please wait a few seconds.'
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

create or replace function public.posts_keep_latest()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  delete from public.posts where author = new.author and id <> new.id;
  insert into private.post_throttle (profile, last_posted_at) values (new.author, now())
    on conflict (profile) do update set last_posted_at = excluded.last_posted_at;
  return null;   -- AFTER trigger: return value is ignored
end;
$$;
-- create or replace keeps the ACL, but restate it: owner only.
revoke all on function public.enforce_post_rate_limit(), public.posts_keep_latest()
  from public, anon, authenticated, service_role;
