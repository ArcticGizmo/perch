// Perch Social - account deletion (GDPR erasure).
//
// Verifies the CALLER from their own access token (never a body-supplied id), then deletes that auth user
// with service_role. The foreign-key cascade (profiles -> friendships/posts/reactions/blocks/games/moves/
// moderation, all ON DELETE CASCADE; reports.reporter is ON DELETE SET NULL so reports the user *filed*
// survive anonymised) removes every social row in one shot. See docs/social-account-deletion-plan.md and
// PRIVACY.md section 4.
//
// Deploy:  supabase functions deploy delete-account --workdir backend
// The client calls it with the standard Perch headers: `apikey: <publishable>` and
// `Authorization: Bearer <user access token>`.

import { createClient } from "jsr:@supabase/supabase-js@2";

// SUPABASE_URL / SUPABASE_ANON_KEY / SUPABASE_SERVICE_ROLE_KEY are injected into the function runtime by
// Supabase - nothing to wire up by hand, and the service key never leaves the server.
const URL = Deno.env.get("SUPABASE_URL")!;
const ANON = Deno.env.get("SUPABASE_ANON_KEY")!;
const SERVICE = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

Deno.serve(async (req: Request): Promise<Response> => {
  if (req.method !== "POST") return json({ error: "method_not_allowed" }, 405);

  const auth = req.headers.get("Authorization");
  if (!auth?.startsWith("Bearer ")) return json({ error: "missing_bearer" }, 401);

  // 1) Resolve WHO is calling from their own token. getUser() cryptographically validates the JWT and
  //    returns the user - we trust this, not anything in the request body, so a caller can only ever
  //    delete themselves (there is no id parameter to forge).
  const asUser = createClient(URL, ANON, { global: { headers: { Authorization: auth } } });
  const { data: { user }, error: whoErr } = await asUser.auth.getUser();
  if (whoErr || !user) return json({ error: "invalid_token" }, 401);

  // 2) Delete that (and only that) auth user as service_role. The cascade does the rest.
  const admin = createClient(URL, SERVICE);
  const { error: delErr } = await admin.auth.admin.deleteUser(user.id);

  // Idempotent: a second call (user already gone) is a success from the client's point of view.
  if (delErr && !/not[_ ]?found/i.test(delErr.message)) {
    console.error("deleteUser failed", { uid: user.id, msg: delErr.message });
    return json({ error: "delete_failed" }, 500);
  }

  return json({ ok: true }, 200);
});
