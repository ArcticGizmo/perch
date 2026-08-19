-- Perch Social — one reaction per user per post (server-side)
-- The client already clears your previous reaction before adding a new one, but enforce it in the schema too so
-- a post can never accumulate more than one reaction from the same person. Change the primary key from
-- (post_id, reactor, emoji) to (post_id, reactor). Switching emoji stays a delete-then-insert on the client;
-- the PK just guarantees the invariant regardless of who's writing.
--
-- Idempotent: only restructures while the PK is still the original three-column shape, so re-running is a no-op.

do $$
begin
  if exists (
    select 1 from pg_constraint c
    where c.conrelid = 'public.reactions'::regclass
      and c.contype = 'p'
      and array_length(c.conkey, 1) = 3          -- old PK still includes emoji
  ) then
    -- Collapse any pre-existing multi-emoji reactions per (post, reactor) down to the most recent one, so the
    -- narrower primary key can be added without a uniqueness conflict.
    delete from public.reactions r
    where exists (
      select 1 from public.reactions o
      where o.post_id = r.post_id
        and o.reactor = r.reactor
        and (o.created_at > r.created_at
             or (o.created_at = r.created_at and o.ctid > r.ctid))
    );

    alter table public.reactions drop constraint reactions_pkey;
    alter table public.reactions add constraint reactions_pkey primary key (post_id, reactor);
  end if;
end $$;
