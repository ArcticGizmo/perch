# Keep sign-ins fresh (experimental): manual test prompts

Manual checks for the opt-in sign-in refresher and the **Login required** button. It adds:

- **A setting, off by default.** Settings → search "sign-in" → **Keep sign-ins fresh (experimental)** (`AppSettings.AutoRefreshSignIn`). While it's on, `SignInRefreshHost` checks every known config dir once a minute. A check is a file read; nothing runs unless a dir is due.
- **Claude Code does the refresh.** When an account's access token is within 5 minutes of expiring (or already lapsed), `SignInRefresher` runs `claude -p --safe-mode --no-session-persistence` with empty input, in that config dir (the primary inherits Perch's environment; any other dir gets `CLAUDE_CONFIG_DIR`). On startup the CLI renews a near-expiry token, taking its own refresh lock and writing its own storage, then exits on the empty prompt. No model call is made, no session is saved, and safe mode keeps hooks, plugins and MCP servers out of it. It takes about a second. Perch never calls the token endpoint itself: it refuses requests that aren't from Claude Code (they get a 429).
- **Judged from the stored sign-in.** Afterwards Perch re-reads the credentials. A later `expiresAt` means renewed. Blank tokens mean Claude Code's refresh was rejected and it signed the account out, so the account needs a login. Unchanged means the run didn't renew it (offline, say), and it's retried after 2 minutes.
- **Never a login.** An expired login, a missing refresh token or a signed-out account is marked **login required**, and the CLI isn't run for it.
- **The nudge.** A one-off toast, "Claude login required - <account>", says what happened (it's held by the notifications master switch). A **Login required** button sits centred over that account's usage bars, which are washed out. On a collapsed chip the button is a smaller version over the mini bars. The usage tooltip gets a "Login required" section with the reason. Clicking the button or the toast opens a terminal running `claude auth login`. The primary account inherits Perch's environment; any other account gets `CLAUDE_CONFIG_DIR=<its dir>`.
- **A lapsed login clock shows the button even with the setting off**, since that account needs `/login` either way.

Outcomes go to `logs/signin.log` (renewed / failed / login required). Token values are never logged.

The headless render (`render <dir>` → `overlay_usage_login_required_*.png`) covers the button on an expanded account and on a chip. Only a real tray covers the refresh.

## Setup

Test against a **throwaway config dir**, so your real sign-in is never the one being renewed or broken.

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

A refresh issues a new refresh token, so an open Claude Code holding the old one must pick up the new tokens rather than get logged out. With Claude Code doing the refresh, that's its own cross-process handling, but it's still the check worth making.

1. Run the `expiresAt` snippet (6 min out).
2. **Then** start `claude` in a terminal with `CLAUDE_CONFIG_DIR=C:\tmp\perch-signin-test`, so it loads that near expiry. Don't type in it yet.
3. Turn the setting on and wait for `renewed` in `signin.log`.
4. Now send that Claude Code session a prompt.

| | Expected |
|---|---|
| `signin.log` within ~2 min | `sign-in refresh on`, then `<account>: renewed` for the test dir only |
| `C:\tmp\perch-signin-test\.credentials.json` | Modified time updated, `expiresAt` about 8 h out. Key names unchanged (check with a key-only grep, never print the values) |
| `~/.claude/.credentials.json` | Untouched |
| Usage strip | The test account re-polls straight away. Its hourglass chip clears, and the tooltip shows about 8 h left |
| Overlay while it renews | At most a ~1 s blip of a background session under Autonomous (the CLI run's own session file) |
| The prompt in step 4 | Answers normally. No `/login` prompt, now or later in that session |
| A Perch-controlled session on that dir started **after** the refresh | Its sign-in banner (if any) shows the new expiry |
| Next pass | Not due again, so no new `renewed` line |

If step 4 instead asks you to log in, note the time and check `signin.log`.

## 3. Login required (rejected refresh token)

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
| Within a minute | Claude Code's refresh is rejected and it blanks the tokens. Toast "Claude login required - <account>": "Claude Code's refresh of the sign-in was rejected, so it signed this account out." |
| Usage strip, that account expanded | Bars washed out, a **Login required** button centred over them |
| Collapse that account to a chip | A smaller **Login required** (or **Log in** on a narrow chip) over the mini bars |
| Hover the strip | Tooltip ends with "Login required", the reason, and "Click Login required to run claude auth login…" |
| `logs/signin.log` | One `login required - …` line. Later passes don't run the CLI again (no repeated lines) |
| Click the button | A terminal opens running `claude auth login` with `CLAUDE_CONFIG_DIR=C:\tmp\perch-signin-test` |
| Click the toast instead | The same terminal |
| Finish the login | Within a minute the button disappears and usage re-polls |

## 4. Expired login clock (setting on or off)

Set `refreshTokenExpiresAt` in the throwaway dir's `.credentials.json` to a time in the past.

| | Expected |
|---|---|
| Setting off | The **Login required** button shows once the usage poll re-reads the blob (up to 5 min, or Settings → Refresh). No toast |
| Setting on | Same button, plus the toast, without running the CLI |

## 5. Can't run the CLI

Temporarily rename `claude.exe` on PATH (or run the dev tray from a shell where `claude` isn't on PATH), then make the test dir due.

| | Expected |
|---|---|
| `logs/signin.log` | `renewal failed, retrying in 2 min - Couldn't run the claude CLI: …`, every 2 minutes |
| Overlay | No **Login required**: it's not a login problem |

## 6. Turning it off

Turn the setting off while an account shows a refresher-reported **Login required** (section 3).

| | Expected |
|---|---|
| `logs/signin.log` | `sign-in refresh off` |
| Button | Clears, unless that account's login clock has also lapsed (section 4) |

## Not covered

- **macOS.** The CLI run goes through a login shell and Claude Code writes the Keychain itself, so it should work there, but it hasn't been run on a Mac.
- **Sessions already running.** A headless session keeps the token it loaded at spawn, so the session banner's Restart button is still how it picks up the renewed one.
- **A future CLI that stops renewing on startup.** Every renewal would then read as "didn't renew" and retry every 2 minutes, which `signin.log` makes visible.
