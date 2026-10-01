-- Perch Social -- Realtime inbox authorisation (review fixes CP4, docs/review-fixes-plan.md)
--
-- The problem: `perch:inbox:<uid>` was a PUBLIC broadcast topic. Anyone holding the publishable key could
-- subscribe to any user's inbox (seeing who invites or nudges them, with game ids), post into it with a forged
-- sender, or flood it (every message triggered a burst of REST polls on the victim's machine).
--
-- The fix: clients now use it as a PRIVATE channel (`config.private = true` on join, `private: true` on the
-- REST broadcast). Realtime authorises private channels with RLS on realtime.messages, evaluated as the
-- caller's JWT role with realtime.topic() set to the channel's topic:
--   - SELECT (join / receive): only the inbox's owner.
--   - INSERT (broadcast):      only an accepted, unblocked friend of the owner (private.are_friends already
--                              folds in blocks). Every inbox message (invites, responses, nudges) is between
--                              friends, so nothing legitimate is lost.
-- Private and public channels are separate namespaces in Realtime, so a public subscriber or public
-- broadcaster on the same topic name never reaches a private subscriber.
--
-- realtime.messages is owned by supabase_realtime_admin, whose grants (arw to anon/authenticated) a migration
-- can't revoke; RLS is on and has no other policy, so anything not matched below is denied. Policies are
-- scoped TO authenticated, so the anon key gets nothing.

-- Strict parse of an inbox topic: the owner's uuid, or null for anything else (never raises, so a malformed
-- topic just fails the policy). Immutable, no table access.
create or replace function private.inbox_owner(topic text)
returns uuid
language sql
immutable
set search_path = ''
as $$
  select case
           when topic ~ '^perch:inbox:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
           then substr(topic, 13)::uuid
         end
$$;

-- Policies evaluate as the signed-in user, so authenticated needs EXECUTE (it already has USAGE on private).
revoke all on function private.inbox_owner(text) from public, anon, authenticated, service_role;
grant execute on function private.inbox_owner(text) to authenticated;

drop policy if exists perch_inbox_receive on realtime.messages;
create policy perch_inbox_receive on realtime.messages
  for select to authenticated
  using (
    realtime.messages.extension = 'broadcast'
    and private.inbox_owner(realtime.topic()) = auth.uid()
  );

drop policy if exists perch_inbox_send on realtime.messages;
create policy perch_inbox_send on realtime.messages
  for insert to authenticated
  with check (
    realtime.messages.extension = 'broadcast'
    and private.inbox_owner(realtime.topic()) is not null
    and private.are_friends(auth.uid(), private.inbox_owner(realtime.topic()))
  );
