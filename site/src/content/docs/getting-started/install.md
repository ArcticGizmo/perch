---
title: Install
description: Install Perch on Windows or macOS.
---

## Windows

Run this in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex
```

That's the whole install. It needs no admin rights (Perch lands in `%LocalAppData%\Perch\`), adds a Start Menu
shortcut and a normal uninstaller under **Settings → Apps**, and starts Perch in the tray when it's done. Every
update after this happens in the app: right-click the tray icon → **Check for Updates…**.

What the script does, in order:

1. Resolves the latest release.
2. Downloads `SHA256SUMS.txt` and `Perch-win-Setup.exe`.
3. **Checks the installer against the manifest, and deletes it rather than run it on any mismatch.**
4. Runs the installer.

The script is [`install.ps1`](https://github.com/ArcticGizmo/perch/blob/main/install.ps1) in the repo. Read it
before piping it into your shell, as you should with any installer.

To install a specific version instead of the latest:

```powershell
$env:PERCH_VERSION = '0.5.38'; irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex
```

Because PowerShell does the download rather than a browser, nothing gets the mark-of-the-web, so this route
never shows the **"Windows protected your PC"** SmartScreen warning.

### Installing by hand

If you'd rather click: download `Perch-win-Setup.exe` from the
[latest release](https://github.com/ArcticGizmo/perch/releases/latest) and run it. You get the same install and
the same self-updates.

A browser download *is* tagged with the mark-of-the-web, so SmartScreen shows the blue **"Windows protected
your PC"** dialog. Click **More info → Run anyway**, or use the one-liner above to avoid it.

To check the download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Perch-win-Setup.exe').Line.Split()[0]
(Get-FileHash Perch-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

## macOS (Apple Silicon)

1. Download the `…-osx-arm64.dmg` from the [latest release](https://github.com/ArcticGizmo/perch/releases/latest).
2. Open the `.dmg` and drag **Perch** to **Applications**.
3. Clear the quarantine flag (below), then launch Perch from Applications.

Every release publishes a `SHA256SUMS.txt` covering all its files. To check the download first:

```sh
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
```

### "Perch is damaged and can't be opened"

The macOS build is **not yet code-signed or notarized**. macOS tags downloaded unsigned apps with a
`com.apple.quarantine` flag, and Gatekeeper then refuses to open them with a misleading **"damaged"** message.
The app isn't damaged. Remove the flag once, in **Terminal**:

```sh
xattr -cr /Applications/Perch.app
```

Then open Perch normally. You need to do this once per install or update.

:::note
On macOS Sonoma and Sequoia, the old "right-click → Open" trick no longer clears a *damaged* verdict, so you
must run the `xattr` command. If you put Perch somewhere other than `/Applications`, point the command there.
:::

If the `.dmg` itself won't open, the flag is on the download. Clear it on the disk image first:

```sh
xattr -cr ~/Downloads/Perch-osx-arm64.dmg
```

### First launch on macOS

Perch runs as a menu-bar app with no Dock icon. On first launch it links `perch` into `~/.local/bin`, wires its
Claude Code hooks, and may show two one-time prompts:

- **Notifications**, for session alerts.
- **Automation** (control Terminal), so clicking a session can focus the exact terminal window running it. If
  you decline, clicking a session just brings the terminal app forward.
