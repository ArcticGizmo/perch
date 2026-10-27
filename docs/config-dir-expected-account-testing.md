# Config-dir expected account: manual test prompts

Each config dir can now declare the account it's **expected** to be signed into: an org (required) and optionally an email. Set it in Settings → Config directories → Edit → "Expected account". When the dir's live login drifts from it, Perch flags it:

- **Not signed in to an org:** logged out, or a personal login.
- **Another org.**
- **Another email in the right org:** only when an email was set, and only if the dir reports an email (an unreadable email never alarms).

Where it shows:

| Surface | Drifted |
|---|---|
| Settings → Config directories row | Red line: `⚠ Expected Acme — signed in to Contoso — the login may be broken` (a plain `Expected: Acme · me@acme.com` line while it matches) |
| New-session launcher pill and menu | `⚠` before the dir's label, and the warning on the line under the pill and in the item tooltip |
| PR session launcher Account dropdown | `⚠` before the label |
| Composer footer account chip | Red text with `⚠`, warning in the tooltip; its switch menu items get `⚠` too |
| `/resume` row account chip and menu | `⚠` before the label, warning in the tooltip |
| Overlay session rows (when "Config labels" is on) | The config-dir chip turns red with `⚠`, and its tooltip adds the warning. A drifted dir gets the chip even when it would normally have none (the unlabelled primary, or a single config dir). |

`ExpectedAccountTests` covers matching and wording. The overlay chip is in the headless render as `overlay_account_drift_1.5x.png`. Everything else here needs the running app.

## Setup

- **Before:** the installed Perch (0.5.43). There is no "Expected account" field.
- **After:** this branch, run with `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0` (quit the installed tray first).

Use a non-primary dir you can sign in and out of freely, e.g. `%USERPROFILE%\.claude-envs\envs\pdg`. To sign it in or out, start a session under it from the account picker and type `/login` or `/logout`; the terminal that opens runs under that dir.

## 1. Set it

Edit the dir, click **Use current sign-in**, then Save.

| | After |
|---|---|
| Org dropdown | The dir's org selected |
| Email box | The dir's email filled (clear it to accept any login in the org) |
| Row | `Expected: <org> · <email>` in muted text |

## 2. Drift: signed out

Run `/logout` in a session under the dir, finish in the terminal it opens, then reopen Settings → Config directories.

| | After |
|---|---|
| Row | Red `⚠ Expected <org> — not signed in to it — the login may be broken` |
| New session launcher, pick that dir | Pill reads `⚠ <label>`, and the line under it gives the warning |
| A running session from that dir (start it before logging out), overlay row | Red `⚠ <dir>` chip within a couple of seconds of the next status update |

Then `/login` back into the expected account.

| | After |
|---|---|
| Row, launcher and chip | Back to normal |

## 3. Drift: wrong org or wrong email

Sign the dir into a different org, or into another seat in the same org with an email set. The row reads `Expected <org> — signed in to <other org>` or `Expected <email> — signed in as <other email>`.

## 4. Clear it

Edit the dir and set Expected account back to **None**. Remove a dir with an expectation and re-add it.

| | After |
|---|---|
| `ExpectedAccounts` in `%APPDATA%\Perch\settings.json` | Entry gone (the key disappears when the list empties) |

## Not covered

- **Idle overlay refresh.** The row chip rechecks on session updates and on the per-second tick that runs while any session is running or awaiting input. With every session idle, a logout shows on the next session update, as the guardrail outline does.
- **No toast.** Nothing notifies you on drift; the signal is visual only.
- **Picker freshness.** Pickers judge the sign-in they read when the menu or window opened.
