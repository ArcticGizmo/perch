---
title: FAQ & troubleshooting
description: Common questions and fixes.
---

## Does Perch send my code or prompts anywhere?

No. Perch reads your session files under `~/.claude/` on your own machine to draw the panel, and never uploads
them. The network features (update checks, phone push, the opt-in Social feed) are listed in
[Privacy](/perch/privacy/).

## Do I need to install a Claude Code plugin?

No. On launch Perch adds a few hooks to `~/.claude/settings.json` that point at its own small `perch-hook`
program. It keeps them up to date across updates, repairs stale entries, and removes them when you uninstall.

## Windows says "Windows protected your PC"

That's SmartScreen reacting to a browser download of an installer that isn't widely known yet. Click
**More info → Run anyway**, or install with the PowerShell one-liner, which avoids the warning entirely. See
[Install](/perch/getting-started/install/).

## macOS says Perch "is damaged and can't be opened"

The app isn't damaged; the macOS build isn't signed yet, and Gatekeeper uses that message for unsigned
downloads. Run `xattr -cr /Applications/Perch.app` once. See
[Install](/perch/getting-started/install/#perch-is-damaged-and-cant-be-opened).

## Clicking a session doesn't focus the right terminal

On macOS, Perch needs the **Automation** permission to pick the exact Terminal window. Without it, it brings
the terminal app forward instead. You can grant it later in **System Settings → Privacy & Security →
Automation**.

## I use more than one Claude account

Perch can watch several Claude config directories (one per account, set with `CLAUDE_CONFIG_DIR`) at once.
Add them in **Settings**; **Scan** suggests directories it finds, and you choose which to add.

## Where is my Perch data?

Perch's own settings live in a local file on your machine. It reads, but doesn't own, Claude Code's files under
`~/.claude/`. Uninstalling Perch removes its hooks from Claude Code's settings.

## Something else is wrong

[Open an issue](https://github.com/ArcticGizmo/perch/issues) with your Perch version (tray menu → **About**),
your OS, and what you expected to happen.
