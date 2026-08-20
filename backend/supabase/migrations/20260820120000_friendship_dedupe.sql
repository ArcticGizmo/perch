-- Perch Social — dedupe friendships + enforce unordered uniqueness (feedback round)
-- The primary key is the ORDERED pair (requester, addressee), so (A -> B) and (B -> A) are two distinct
-- rows. If both people request each other (or a request crosses an existing one), the graph ends up with a
-- duplicate for the same pair of people. This migration collapses any such duplicates — PRESERVING the
-- relationship — and then adds a unique index on the UNORDERED pair so it can never happen again.
--
-- Idempotent: the collapse only touches pairs that currently have >1 row, and the index is created
-- `if not exists`, so re-running is a no-op.

-- ── 1. collapse duplicate unordered pairs ───────────────────────────────────────
-- A pair with two rows means both sides engaged, so the relationship is mutual → keep a single ACCEPTED
-- row (a lone pending request never duplicates). A legacy 'blocked' friendship row (blocking moved to the
-- blocks table in M6, so these shouldn't exist) is preserved as blocked to stay on the safe side. The
-- earliest created_at is kept so ordering isn't disturbed.
do $$
declare r record;
begin
  for r in
    select least(requester, addressee)  as a,
           greatest(requester, addressee) as b,
           bool_or(status = 'accepted')            as any_accepted,
           bool_or(status = 'blocked')             as any_blocked,
           count(*) filter (where status = 'pending') as pendings,
           min(created_at)                         as created
    from public.friendships
    group by least(requester, addressee), greatest(requester, addressee)
    having count(*) > 1
  loop
    -- Remove every row for this pair, then re-insert one canonical (a < b) row with the resolved status.
    delete from public.friendships
      where least(requester, addressee) = r.a
        and greatest(requester, addressee) = r.b;

    insert into public.friendships (requester, addressee, status, created_at)
    values (
      r.a, r.b,
      (case when r.any_blocked                          then 'blocked'
            when r.any_accepted or r.pendings >= 2      then 'accepted'
            else 'pending' end)::public.friend_status,
      r.created
    );
  end loop;
end $$;

-- ── 2. enforce one row per unordered pair ───────────────────────────────────────
-- least()/greatest() over the two uuids gives a direction-independent key, so a reverse-direction insert
-- now violates this index instead of creating a duplicate. The client (SendRequestAsync) checks for an
-- existing edge either way first, turning a crossed request into an accepted friendship rather than a 409.
create unique index if not exists friendships_unordered
  on public.friendships (least(requester, addressee), greatest(requester, addressee));
