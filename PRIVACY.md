# Perch — Privacy

_Last updated: 2026-09-17_

Perch is a desktop app that watches your local Claude Code sessions and shows their status. It is
**local-first**: almost everything it does happens on your own machine and never leaves it. The one feature
that stores data on a server, the **Social feed**, is **opt-in** and only ever active after you choose to
sign in.

This document explains what data Perch handles, what (if anything) leaves your device, and how to delete
it. It describes the app as currently built; the authoritative field-by-field list is the database schema
in `backend/supabase/migrations/`.

> Placeholders in **[brackets]** are for whoever operates a given Perch deployment to fill in (data
> controller, contact, hosting region). The public build points at the project maintainer's Supabase
> project.

## 1. Data that never leaves your device

Perch's core job runs entirely locally:

- It **reads files under `~/.claude/`** — your session state, transcripts, and settings — to show status,
  stats, and history. **Perch never uploads any of this.** The contents of your Claude Code sessions
  (prompts, code, tool output) are read on your machine to draw the overlay and are **never** sent anywhere,
  and there is no path that auto-posts them.
- Your **Perch settings** live in a local settings file on your machine.

None of the above is transmitted off your device.

## 2. Data that leaves your device only when you act

Three features make network connections, each either automatic-but-impersonal or something you explicitly
turn on:

- **Software updates** — Perch checks GitHub Releases (`github.com/ArcticGizmo/perch`) for a newer version
  and downloads it. This sends **no personal data** — it's the same request anyone fetching a public release
  makes.
- **External notifications (optional, off by default)** — if you configure an [ntfy](https://ntfy.sh)
  server, Perch sends your notification text to the server and topic **you specify**. Only enabled if you
  set it up; nothing is sent otherwise.
- **The Social feed (opt-in)** — covered in detail below. Nothing about Social happens until you sign in.

## 3. The Social feed

The Social feed is an optional feature that lets you share short status posts with people you've added as
friends. It requires you to **sign in with GitHub**, and it stores data on a hosted
[Supabase](https://supabase.com) (PostgreSQL) database operated by **Jonathan Howell**.

### 3.1 What we store

| Data | Where | Notes |
|------|-------|-------|
| **Handle** (unique) | `profiles` | How friends find and recognise you. |
| **Display name** (optional) | `profiles` | Free text you choose, ≤40 chars. |
| **Mood emoji** (optional) | `profiles` | A status glyph. |
| **Status posts** | `posts` | The text you post, ≤280 chars, plus timestamp. |
| **Reactions** | `reactions` | Emoji you add to posts you can see. |
| **Friend connections** | `friendships` | Who you've added / who's added you. |
| **Blocks** | `blocks` | People you've blocked (private to you). |
| **Connect 4 games** | `games`, `moves` | Games you play with friends. |
| **Abuse reports** | `reports` | If you report someone (see §4.2). |
| **Your GitHub email** | Auth provider (`auth.users`) | Held by the sign-in system, **not** in the tables above. |

We deliberately store **no real name and no email in the profile tables** — the email you sign in with
stays with the authentication system. Perch does **not** upload anything from your Claude Code sessions to
the Social feed; posting is always manual and explicit.

### 3.2 Who can see it

Access is enforced by database row-level security, not just the app:

- Your **posts and reactions** are visible only to **you and your accepted friends**.
- Your **friend connections** are visible only to the two people involved.
- **Blocks and reports** are private to you (reports are write-only — only the moderator can read them).

### 3.3 Legal basis (GDPR)

- **Consent** — Social does nothing until you opt in and sign in; you can stop and delete at any time.
- **Legitimate interest** — keeping abuse reports for safety/moderation (§4.2).

## 4. Deleting your account and data (right to erasure)

You can permanently delete your Social account and data at any time:

**Settings → Social → "Delete account and data"**, then confirm.

### 4.1 What deletion removes

Everything in §3.1 that belongs to you — your profile, posts, reactions, friend connections, blocks, and
Connect 4 games — **and your authentication identity, including the GitHub email** the sign-in system held.
This is irreversible. Posts already shown in a friend's app disappear from their view the next time it
refreshes.

### 4.2 What we keep, and why

If you **filed abuse reports about other people**, those reports are **kept but anonymised** — your identity
as the reporter is removed. This lets moderation act on genuine safety reports without a report vanishing
the moment its author leaves. The **exact** retained shape is one row per report you filed:

```
reports(
  id         — an opaque identifier
  reporter   — NULL          (this was you; it is erased, so the report no longer identifies you)
  reported   — the other user you reported (their account is untouched)
  reason     — the text you wrote when reporting (up to 500 characters)
  created_at — when the report was filed
)
```

Note that `reason` is text **you** wrote, so we retain exactly what you typed there. Nothing else that
identifies you or that you authored survives deletion. This retention is on the basis of legitimate interest
in platform safety.

### 4.3 Your GitHub authorization is separate

Deleting your Perch account removes the email the sign-in system stored, but it does **not** remove Perch
from your GitHub account's list of authorized apps — that's controlled by GitHub, not us. To remove it,
revoke Perch here:

**https://github.com/settings/connections/applications/[PERCH_GITHUB_CLIENT_ID]**

(or GitHub → Settings → Applications → Authorized OAuth Apps → Perch → Revoke access).

## 5. Your other rights

- **Access / portability** — your profile is visible and editable in-app; a "download my data" export may be
  offered in a future version.
- **Rectification** — edit your handle, display name, and mood any time in the app.
- **Withdraw consent** — sign out, and/or delete your account (§4).
- Under the GDPR you may also lodge a complaint with your local supervisory authority.

## 6. Third parties

Perch relies on these services; each has its own privacy policy:

- **Supabase** (database + authentication for Social) — https://supabase.com/privacy
- **GitHub** (OAuth sign-in for Social; software updates) — https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement
- **ntfy** (only if you configure external notifications) — the policy of whichever server you point it at.

We do not sell your data and do not use it for advertising.

## 7. Data location and retention

- Social data is stored in the operator's Supabase project in the **`ap-southeast-2` (Sydney)** region.
- We keep your Social data until you delete it (§4); anonymised abuse reports are kept as described in §4.2.
- Local data on your device persists until you remove Perch or clear its settings.

## 8. Children

Perch is a developer tool and is not directed at children under 13 (or the minimum age in your
jurisdiction).

## 9. Changes to this policy

We may update this document as the app changes; the "Last updated" date at the top reflects the latest
revision. Material changes to the Social feature will be noted in the changelog.

## 10. Contact

Questions or requests about your data: **[contact email / method]**, or open an issue at
https://github.com/ArcticGizmo/perch/issues.
