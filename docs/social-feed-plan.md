# Perch Social Feed — options & infrastructure assessment

> Status: **investigation only** (no code). This weighs the ways to add a MySpace-style
> friends/status feed under the Perch UI, what backend each needs, the security model, and
> what it costs to run. Decision points are at the end.

## 1. What the feature actually requires

Perch today is **fully local**: it reads `~/.claude` files and renders overlays. It has no
network identity, no account, no server. A social feed is the first thing that reaches off the
machine, so it needs five capabilities that don't exist yet:

| # | Capability | Why it's non-trivial |
|---|------------|----------------------|
| 1 | **Identity** — a stable "who am I" per user | The desktop app is fully inspectable; you cannot trust anything the client claims about itself. |
| 2 | **Friend graph** — mutual connections | Needs consent (requests/accept) and per-read authorization so A only sees B's posts if they're friends. |
| 3 | **Publish** — post a status | User-generated content ⇒ size limits, rate limits, abuse/moderation. |
| 4 | **Feed read** — fetch friends' recent statuses | Authorization on *every* read, not just at friend-time. |
| 5 | **Real-time push** — new posts appear without a manual refresh | This is the firewall-sensitive part (§2). |

Only #5 needs a live connection. #1–4 are ordinary request/response HTTPS. That split matters:
you can ship a perfectly good feed with **plain polling** and add live push later.

## 2. The firewall / transport question (answered first, because it drives the rest)

Corporate firewalls almost universally allow **outbound HTTPS on 443**. What they break is
*unusual* traffic: raw TCP, non-standard ports, and sometimes long-lived WebSocket upgrades
through deep-packet-inspection proxies. So the ranking for "works from behind an office
firewall", most robust last:

1. **WebSockets (`wss://`, 443)** — lowest latency, but the upgrade handshake is what strict
   proxies occasionally kill.
2. **Server-Sent Events (SSE)** — a single long-lived HTTPS `GET` that streams. Looks like an
   ordinary slow download to a proxy. Server→client only (fine: the feed only needs the server
   to push new posts). Very firewall-friendly.
3. **Long-polling** — a normal `GET` that the server holds open a few seconds. Indistinguishable
   from any other HTTPS request. Works essentially everywhere.

**The key insight:** you don't pick one — you pick a transport layer that **negotiates and falls
back automatically**. Both of the recommended stacks below do this:

- **ASP.NET Core SignalR** negotiates WebSockets → SSE → Long-polling out of the box, no client
  code. ([Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0),
  [transport docs](https://deepwiki.com/aspnet/SignalR/3.2-server-sent-events-and-long-polling))
- **Supabase Realtime / Ably / Pusher** ship the same degrade-gracefully behavior in their
  clients.

Do **not** try to "get around" a firewall with anything sketchy (DNS tunneling, non-standard
ports, disguising traffic) — that's the kind of thing that gets an app blocked org-wide and is
the wrong side of an IT policy. The legitimate move is: **HTTPS/443, on a well-known hosted
domain, with a transport that degrades to long-polling.** That satisfies "works behind the
firewall" without fighting it. And honestly, for a status feed, **60-second polling is
indistinguishable from real-time to a human** — you can launch on polling alone and treat live
push as a nicety.

## 3. The options

### Option A — Managed Backend-as-a-Service (Supabase) — *least code*

Postgres + Auth + Realtime + Row-Level-Security in one managed product. You write **almost no
server code**: the friend graph and posts are tables, and **Row-Level Security (RLS) policies**
enforce "you can only read a post if you're friends with the author" *in the database*, so even
a tampered client can't read what it shouldn't.

- **Identity:** Supabase Auth (magic-link email, or GitHub OAuth — devs already have GitHub).
- **Real-time:** Supabase Realtime pushes row inserts over WebSockets with fallback.
- **.NET fit:** community `supabase-csharp` client exists; worst case it's plain REST + a WS lib.
- **Free tier (2026):** 500 MB DB, 50k monthly active users, 200 concurrent realtime
  connections, 2M realtime msgs/mo, unlimited API requests.
  ([Supabase pricing breakdown](https://uibakery.io/blog/supabase-pricing)) Comfortably covers a
  friends-of-friends userbase for $0. **Caveat:** free projects **pause after 7 days of
  inactivity** — fine for a hobby project, annoying if you want always-warm (that's the $25/mo
  Pro tier).
- **Effort:** lowest. Days, not weeks. The RLS model is the big win — authorization is declared,
  not hand-coded on every endpoint.
- **Downside:** you're modeling auth/authz in someone else's product; less control; vendor lock.

### Option B — Small self-hosted API + SignalR — *best fit for your stack*

A tiny **ASP.NET Core minimal API** (identity, friend requests, post, feed) + **SignalR** for
push + **Postgres**. This is the most *native* option: same language as Perch, SignalR gives you
the firewall-proof transport fallback for free, and you own the whole model.

- **Identity:** magic-link email or GitHub OAuth (don't roll your own passwords — see §4).
- **Real-time:** SignalR hub; the desktop client subscribes to a "friends' posts" group.
- **Hosting:** the free tiers here have thinned out. **Fly.io and Railway both dropped their
  free tiers**; realistic minimum is **~$5/mo** (Railway Hobby, or a small Fly VM).
  ([Railway vs Fly.io 2026](https://northflank.com/blog/railway-vs-flyio),
  [Fly free tier 2026](https://www.saaspricepulse.com/tools/flyio)) **Azure Container Apps** has
  a standing monthly free grant (180k vCPU-sec, 360k GiB-sec, 2M requests) that a hobby app can
  live inside. ([ACA billing](https://learn.microsoft.com/en-us/azure/container-apps/billing))
- **Effort:** highest of the three — you write and maintain the API, auth, DB migrations, and ops
  (backups, patching, uptime). Weeks, and it's *ongoing*.
- **Downside:** you're now running a service. For a "silly feature," that's a real commitment.

### Option C — Serverless + managed pub/sub — *cheapest at rest*

**Cloudflare Workers + Durable Objects** (Durable Objects are now on the **free** plan, ~3M
requests/mo, and free-plan SQLite storage isn't billed).
([DO free tier](https://developers.cloudflare.com/changelog/post/2025-04-07-durable-objects-free-tier/))
A Durable Object is a natural fit for "one live fan-out hub per user's friend circle." Or keep
storage anywhere and bolt on **Ably/Pusher** purely for the firewall-friendly real-time layer.

- **Effort:** medium, but a different runtime (JS/TS Workers) than Perch's .NET — context switch.
- **Best when:** you want near-zero idle cost and don't mind the edge/JS model.

### Option summary

| | A · Supabase (BaaS) | B · ASP.NET + SignalR | C · Serverless / pub-sub |
|---|---|---|---|
| Code to write | Least (mostly config + RLS) | Most (full API) | Medium |
| Language fit | REST/WS client in C# | **Native .NET** | JS/TS (Workers) |
| Firewall traversal | Built-in fallback | **SignalR fallback (best)** | Provider-dependent |
| Authorization model | **RLS in the DB** | Hand-coded per endpoint | Hand-coded |
| Idle cost | $0 (pauses after 7d) | ~$5/mo (or ACA free grant) | ~$0 |
| Ops burden | Near-zero | **You run a service** | Low |
| Lock-in | High | Low | Medium |

## 4. Security model (the part that actually matters)

This applies to **all** options — the transport and host are the easy decisions; getting authz
right is the hard one.

- **Never trust the client.** The desktop app can be decompiled and any embedded secret
  extracted. There must be **no shared app-wide secret** that grants data access. Every request
  carries a **per-user token** the server issued; the server authorizes every read/write itself.
- **Don't roll your own passwords.** Use **OAuth (GitHub is ideal — your users are developers)**
  or **magic-link email**. This offloads credential storage and breach risk entirely.
- **Mutual-consent friend graph.** Posts are readable only by *accepted* friends, enforced
  server-side on every feed read (Supabase RLS makes this declarative; a custom API needs an
  explicit check on each endpoint — easy to forget, so centralize it).
- **Rate-limit and size-cap posts.** A public-facing write endpoint invites spam/DoS. Cap status
  length (e.g. 280 chars), throttle per user, and consider a basic profanity/abuse filter since
  content is shown to others.
- **TLS everywhere**, HTTPS/443 only.
- **Minimal PII — and this is a real constraint here.** Your org's data-handling policy prohibits
  entering PII. A social feature inherently wants a display name and an email (for auth). Keep it
  to the absolute minimum: let users pick a **handle**, don't require real names, and if you use
  email-based auth, the email lives in the auth provider (Supabase/GitHub), **not** in Perch's
  own tables or logs. Treat status text as public-ish and never auto-ingest anything from
  `~/.claude` (transcripts, cwd paths, project names) into a post — that's exactly the kind of
  incidental PII/leak that would violate the policy. Posting must be **explicit and manual.**
- **Abuse kill-switch.** A hobby social feature can attract griefing. Have a way to block a user
  and delete content server-side from day one.

## 5. Infrastructure sizing & cost

For a friends-and-friends-of-friends userbase (tens, low hundreds of users), this is **tiny**:

- **Storage:** a few small tables (users, friendships, posts). Megabytes.
- **Compute:** negligible; posts are infrequent.
- **Real-time connections:** one per running Perch instance. Well within every free/near-free
  tier above (Supabase free = 200 concurrent).

**Bottom line on cost:** Option A can run at **$0** (accepting the 7-day pause). Option B is
**~$5/mo** or free-grant-hosted on Azure Container Apps. Option C is **~$0** at rest. None of
these is a budget concern; the real cost is **your time and the ongoing ops/moderation
responsibility**, which is why "least code + managed" weighs heavily for a fun side-feature.

## 5a. Supabase vs Cloudflare — deep comparison (private, threat-aware)

The two managed options are strong in **opposite halves** of the security problem, which is what
makes this the real decision.

| Axis | **Supabase** | **Cloudflare (Workers + Durable Objects)** |
|---|---|---|
| Build-on | Postgres + Auth + Realtime + **RLS**, managed | Workers (JS/TS) + Durable Objects + D1/KV |
| Auth | **Turnkey** (GitHub OAuth, magic-link, JWTs) | **You build it** (roll OAuth, or Clerk/Auth0) |
| Authorization | **RLS — declared in SQL, enforced in the DB.** Tampered client hitting the public API still can't read forbidden rows | **Code in every Worker.** Full control, no safety net |
| Real-time + fallback | Realtime over WS **with auto fallback** | WS via Durable Objects, **you hand-build SSE/long-poll fallback** (weakest for the firewall goal) |
| .NET fit | REST/Realtime from C# (`supabase-csharp`, community) | **None** — TS runtime, context switch |
| Abuse / DDoS / bot | Basic auth rate limits; no WAF | **Best-in-class WAF, DDoS, edge rate-limiting, ~free** |
| Cost at rest | **$0**, pauses after 7d idle | **~$0**, DO now free-tier, **no idle pause** |
| Time to a *correct* private feed | **Fastest** | Slowest (own auth + authz + realtime fallback) |

**Malicious-actor split (the whole decision).** Threats to a *private* feed: (a) reading posts
you aren't friends with — broken authorization; (b) spamming writes; (c) enumerating/scraping
users; (d) DDoS/griefing.

- **(a) is the highest-impact class, and Supabase closes it by construction** — RLS makes
  "friends-only read" a *database* rule, not app code you can forget on one endpoint.
- **(b)/(c)/(d) are where Cloudflare's edge wins** (WAF, rate-limit, DDoS) — but those bite hard
  mainly at *public* scale, which is ruled out. On a small private feed they're handled with
  per-user rate limits + a block/delete switch.

**The Supabase caveat to internalize:** the model rests on the **public anon key** (shipped in
the client by design) + **correct RLS**. Failure mode = one table with RLS off / a wrong policy
is wide open to anyone holding that public key. Discipline: **RLS on every table**, the
`service_role` key **never** in the client, a test per policy. Done right, a malicious client is
boxed in.

**Verdict for this project (private, .NET, threat-aware, hobby effort): Supabase.** It eliminates
the worst vuln class (authorization) by design, hands you auth + firewall-friendly real-time for
free, and is $0. Cloudflare's edge advantages pay off most at public traffic scale, which isn't
the target. **Strongest-posture combo if wanted:** Supabase for data/auth/RLS **with Cloudflare
in front** for WAF + rate-limiting — one extra moving part, worth it only if the abuse concern is
front-of-mind.

## 6. Recommendation

Two sensible paths, depending on how much you want to *own* vs *ship*:

- **Want it shipped fastest, least maintenance → Option A (Supabase).** RLS gives you a correct
  authorization model almost for free, GitHub OAuth fits your users, real-time and fallback are
  built in, and it's $0. Best "silly feature, minimal commitment" choice.
- **Want it native and fully owned → Option B (ASP.NET + SignalR)**, hosted on **Azure Container
  Apps** (free grant) or a $5 Railway box. Same language as Perch, the best firewall story
  (SignalR's automatic WS→SSE→long-poll), no vendor lock — at the cost of writing and running a
  real service.

**Either way, phase it:**

1. **Phase 0 — identity + friend graph + post + feed over plain HTTPS polling (60s).** No
   real-time yet. This is the whole feature, minus liveness, and it de-risks the auth/authz model
   first.
2. **Phase 1 — add live push** (Supabase Realtime or a SignalR hub) with automatic fallback.
3. **Phase 2 — the UI:** a small feed strip under the overlay (owner-drawn, per Perch's
   conventions), plus a compose box. Reuse the existing `INotifier` for "X posted" nudges.

And keep every OS/network capability behind a `Perch.Core` interface (e.g. `ISocialClient`), per
the project's platform-abstraction rule, so the macOS head can share it.

## 7. Open decisions (for you)

1. **How much do you want to run?** Managed BaaS (A) vs your own service (B). This is the main
   fork.
2. **Auth method:** GitHub OAuth (devs have it, no email stored) vs magic-link email.
3. **Scale intent:** private friends-only (tens of users) — which everything above assumes — or
   ever public? Public changes the moderation/abuse story materially.
4. **Real-time at launch, or polling-first?** Recommend polling-first (Phase 0) regardless.

---

### Sources
- [Supabase pricing / free tier 2026](https://uibakery.io/blog/supabase-pricing)
- [ASP.NET Core SignalR configuration & transport fallback](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0)
- [SignalR SSE & long-polling](https://deepwiki.com/aspnet/SignalR/3.2-server-sent-events-and-long-polling)
- [Railway vs Fly.io 2026 pricing](https://northflank.com/blog/railway-vs-flyio)
- [Fly.io free tier 2026](https://www.saaspricepulse.com/tools/flyio)
- [Cloudflare Durable Objects free tier](https://developers.cloudflare.com/changelog/post/2025-04-07-durable-objects-free-tier/)
- [Azure Container Apps billing / free grant](https://learn.microsoft.com/en-us/azure/container-apps/billing)
