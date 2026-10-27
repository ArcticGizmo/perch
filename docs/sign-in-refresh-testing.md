# Keep sign-ins fresh (experimental): manual test prompts

Manual checks for the opt-in sign-in refresher and the **Login required** button. It adds:

- **A setting, off by default.** Settings → search "sign-in" → **Keep sign-ins fresh (experimental)** (`AppSettings.AutoRefreshSignIn`). While it's on, `SignInRefreshHost` checks every known config dir once a minute.
- **Refresh 5 minutes before expiry.** When an account's access token is within 5 minutes of expiring (or already lapsed), `SignInRefresher` exchanges its refresh token at `https://platform.claude.com/v1/oauth/token`, the same request Claude Code makes (its client id, the stored scopes). It writes the new tokens back into that dir's credentials, keeping every other field. On Windows that's `.credentials.json`. A macOS Keychain sign-in isn't written yet, so it's left alone.
- **Claude Code's refresh lock.** Before refreshing, Perch takes `<config dir>\.oauth_refresh.lock`, the lock directory Claude Code takes for its own refreshes, then re-reads the credentials. If Claude Code holds the lock, Perch waits for the next pass. If Claude Code already refreshed, Perch does nothing. Claude Code re-reads storage under the same lock, so it adopts Perch's new tokens instead of spending a stale refresh token.
- **Never a login.** If the login has expired, the refresh token is missing, or the server rejects it (`invalid_grant` / 401), the account is marked **login required**. That refresh token is never sent again. Other refusals and server errors are retried after 2 minutes.
- **The nudge.** A one-off toast, "Claude login required - <account>", says what happened (it's held by the notifications master switch). A **Login required** button sits centred over that account's usage bars, which are washed out. On a collapsed chip the button is a smaller version over the mini bars. The usage tooltip gets a "Login required" section with the reason. Clicking the button or the toast opens a terminal running `claude auth login`. The primary account inherits Perch's environment; any other account gets `CLAUDE_CONFIG_DIR=<its dir>`.
- **A lapsed login clock shows the button even with the setting off**, since that account needs `/login` either way.

Outcomes go to `logs/signin.log` (renewed / failed / lock busy / login required). Token values are never logged.

The headless render (`render <dir>` → `overlay_usage_login_required_*.png`) covers the button on an expanded account and on a chip. Only a real tray covers the refresh.

## Setup

Test against a **throwaway config dir**, so your real sign-in is never the one being rotated or broken.

1. In a terminal: `set CLAUDE_CONFIG_DIR=C:\tmp\perch-signin-test` then `claude auth login`, and sign in.
2. Run the dev tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`. Make sure the usage bars are on.
3. Add `C:\tmp\perch-signin-test` in Perch (Settings → Claude config dirs) so it's a known dir and shows in the usage strip.

To make a refresh come due without waiting ~8 h, move that dir's `expiresAt` closer. This PowerShell rewrites only that field and prints nothing:

```powershell
$f = 'C:\tmp\perch-signin-test\.credentials.json'
$j = Get-Content $f -Raw | ConvertFrom-Json
$j.claudeAiOauth.expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(6).ToUnixTimeMilliseconds()
[IO.File]::WriteAllText($f, ($j | ConvertTo-Json -Depth 20 -Compress), (New-Object Text.UTF8Encoding $false))
```

Six minutes out means it falls due within a pass or two. Your real `~/.claude` is 8 h out, so it stays "not due" and untouched. The dev tray logs to `%APPDATA%\Perch (Dev)\logs\signin.log`.

## 1. Setting off (default)

| | Before | After |
|---|---|---|
| Settings → search "sign-in" | Only **Sign-in expiry** | Also **Keep sign-ins fresh (experimental)**, off |
| `logs/signin.log` | — | Absent, or no new lines |
| `.credentials.json` modified time | — | Unchanged by Perch |

## 2. A refresh, with Claude Code open on the same dir

This is the check that matters most: a refresh issues a new refresh token, so an open Claude Code holding the old one must adopt Perch's tokens rather than get logged out.

1. Run the `expiresAt` snippet (6 min out).
2. **Then** start `claude` in a terminal with `CLAUDE_CONFIG_DIR=C:\tmp\perch-signin-test`, so it loads that near expiry. Don't type in it yet.
3. Turn the setting on and wait for `renewed` in `signin.log`.
4. Now send that Claude Code session a prompt. It sees its own token as near expiry, takes the refresh lock, re-reads the file and finds Perch's tokens.

| | Expected |
|---|---|
| `signin.log` within ~2 min | `sign-in refresh on`, then `<account>: renewed` for the test dir only |
| `C:\tmp\perch-signin-test\.credentials.json` | Modified time updated. Key names unchanged (check with a key-only grep, never print the values) |
| `~/.claude/.credentials.json` | Untouched |
| Usage strip | The test account re-polls straight away. Its hourglass chip clears, and the tooltip shows about 8 h left |
| The prompt in step 4 | Answers normally. No `/login` prompt, now or later in that session |
| A Perch-controlled session on that dir started **after** the refresh | Its sign-in banner (if any) shows the new expiry |
| Next pass | Not due again, so no new `renewed` line |

If step 4 instead asks you to log in, the lock hand-off didn't work. Note the time and check `signin.log`.

## 3. Lock busy

Run the `expiresAt` snippet again, then create the lock by hand before the next pass: `mkdir C:\tmp\perch-signin-test\.oauth_refresh.lock`.

| | Expected |
|---|---|
| `logs/signin.log` | `lock held by another process, retrying next pass`, every minute |
| Credentials | Untouched |
| After 60 s with the folder's modified time unchanged | Treated as stale: Perch takes it over, renews, and removes the folder |

## 4. Login required (rejected refresh token)

Break the throwaway dir's refresh token and make it due. Like the setup snippet, this prints nothing:

```powershell
$f = 'C:\tmp\perch-signin-test\.credentials.json'
$j = Get-Content $f -Raw | ConvertFrom-Json
$j.claudeAiOauth.refreshToken = 'sk-ant-ort01-broken-' + [guid]::NewGuid().ToString('N')
$j.claudeAiOauth.expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()
[IO.File]::WriteAllText($f, ($j | ConvertTo-Json -Depth 20 -Compress), (New-Object Text.UTF8Encoding $false))
```

| | Expected |
|---|---|
| Within a minute | Toast "Claude login required - <account>" with the server's reason (`invalid_grant`) |
| Usage strip, that account expanded | Bars washed out, a **Login required** button centred over them |
| Collapse that account to a chip | A smaller **Login required** (or **Log in** on a narrow chip) over the mini bars |
| Hover the strip | Tooltip ends with "Login required", the reason, and "Click Login required to run claude auth login…" |
| `logs/signin.log` | One `login required - …` line. Later passes don't hit the server again (no repeated lines) |
| Click the button | A terminal opens running `claude auth login` with `CLAUDE_CONFIG_DIR=C:\tmp\perch-signin-test` |
| Click the toast instead | The same terminal |
| Finish the login | Within a minute the button disappears and usage re-polls |

## 5. Expired login clock (setting on or off)

Set `refreshTokenExpiresAt` in the throwaway dir's `.credentials.json` to a time in the past.

| | Expected |
|---|---|
| Setting off | The **Login required** button shows once the usage poll re-reads the blob (up to 5 min, or Settings → Refresh). No toast |
| Setting on | Same button, plus the toast, without a request to the server |

## 6. Turning it off

Turn the setting off while an account shows a refresher-reported **Login required** (section 4).

| | Expected |
|---|---|
| `logs/signin.log` | `sign-in refresh off` |
| Button | Clears, unless that account's login clock has also lapsed (section 5) |

## Not covered

- **macOS Keychain sign-ins.** The write is refused, so a Keychain-held primary is never refreshed.
- **Sessions already running.** A headless session keeps the token it loaded at spawn, so the session banner's Restart button is still how it picks up the renewed one.
- **A truly simultaneous refresh.** Claude Code taking the lock in the same instant Perch does. On Windows the lock is taken by an atomic rename. If both still refresh, the loser re-reads, sees the winner's tokens and reports nothing wrong. This hasn't been exercised live.
