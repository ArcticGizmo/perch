# Supabase "JWT issued at future" — timestamp drift

## Summary

Perch Social sign-in appeared to succeed (a valid GitHub session), but then **every** REST
call failed and the user was bounced to "pick a handle" / "cannot load profile". The cause was
**not** the user's machine clock and **not** Perch's code — it was a **clock skew inside the
Supabase backend**: the service that *mints* the JWT (GoTrue) was running ~1–2s ahead of the
service that *validates* it (PostgREST). PostgREST has no `iat` leeway, so it rejected every
otherwise-valid token as `PGRST303 "JWT issued at future"`.

## How we know (field diagnostic)

A temporary diagnostic decoded the exact token PostgREST refused and logged its claims against
the local clock. The decisive line:

```
"load profile" failed (401): now=2026-08-26 01:25:10Z
    iat=2026-08-26 01:25:09Z [-1.1s (past)]  nbf=(absent)  exp=…[+3598.9s (future)]
    — {"code":"PGRST303","details":null,"hint":null,"message":"JWT issued at future"}
```

Reading it:

- `iat` (01:25:09) is **1.1s in the past** relative to this machine, and `exp` is a full hour
  out — so by our clock *and* GoTrue's clock the token is valid. Not expired, not future.
- Yet **PostgREST** rejects it as "issued at future". The only way a past-`iat` token looks
  "future" to PostgREST is if **PostgREST's own clock is behind the `iat`** — i.e. behind the
  GoTrue clock that stamped it, by more than a second.
- It reproduced across `load profile`, `load friends`, `load your games` — every call with that
  token — because the fault is the token's timing vs. the validator, not any one endpoint.

### Why the client-vs-server clock test looked clean

An earlier check compared this machine's clock to Supabase's HTTP `Date` header and found no
skew. That measured **machine vs. the API front door**, never **GoTrue vs. PostgREST
internally** — which is where the disagreement actually is. So a clean client/server result does
**not** rule this out. (That script was removed; it measured the wrong pair of clocks for this
problem.)

## The real fix is at the backend

This is a backend clock problem; the client can only paper over a small transient skew. To cure
it at the source:

- **Local `supabase start` (Docker/WSL2):** the GoTrue and PostgREST containers drifted apart —
  common after the host sleeps/resumes. Resync and restart:
  `wsl --shutdown`, restart Docker Desktop, then `supabase stop && supabase start`.
- **Hosted project:** a persistent GoTrue↔PostgREST skew is a Supabase-side infrastructure
  issue worth raising with them.

## What Perch does about it now

1. **Proactive settle** — after minting a token, decode its `iat`/`nbf` and, if the validity
   window hasn't opened by the local clock, wait it out (buffer 1s, capped 5s) before first use.
   Zero cost when clocks agree. (`AwaitTokenValidityAsync`.)
2. **Bounded retry** — the first REST call after a mint (`LoadMeAsync`) retries up to 4 times,
   1.5s apart, on a drift rejection. Because `iat` is a fixed instant and real time advances the
   validator's clock past it, a few seconds of retry rides out a ~1–2s backend skew; the token
   is then valid for its whole hour, so later calls and the pollers succeed. (`RetryOnTokenNotYetValidAsync`.)
3. **Drift detection** — the rejection wording varies (GoTrue "issued in the future"; PostgREST
   "JWT issued at future" with code `PGRST303`), so detection matches the `PGRST303` code and
   `issued`+`future`, plus the other known phrasings. (`LooksLikeTokenNotYetValid`.)
4. **Feature-wide fault state** — if the drift **survives** the retries (or persists past a ~6s
   grace via the background pollers), the whole Social feature flips into a **"Timestamp drift"**
   error state: an error-hued strip on the overlay ("Social unavailable — Timestamp drift"), a
   one-time warning toast, and an explanatory banner on Settings → Social. It **clears itself**
   automatically on the next successful call. (`AuthState.Fault` = `SocialFault.TimestampDrift`,
   set/cleared centrally in `EnsureOkAsync`.)

So a transient skew is absorbed silently; a genuinely broken backend clock is surfaced plainly
so people know something is wrong (and that it isn't their account).

## Key code

- `src/Perch.Core/Social/SupabaseSocialClient.cs` — settle, retry, detection, fault set/clear.
- `src/Perch.Core/Social/SocialModels.cs` — `SocialFault` enum + `AuthState.Fault`.
- `src/Perch.App/Views/OverlayCanvas.Social.cs` — overlay error strip (`SetSocialFault`).
- `src/Perch.App/App.axaml.cs` — `OnSocialFaultChanged` (overlay push + toast).
- `src/Perch.App/Windows/SettingsWindow.cs` — `RefreshSocialPage` drift banner.
