---
title: Updating
description: How Perch updates itself, depending on how you installed it.
---

Perch checks for new releases in the background however you installed it. Who *installs* the update depends on
how you installed Perch:

| Installed with | How it updates |
| --- | --- |
| The PowerShell one-liner, `Perch-win-Setup.exe`, or the macOS `.dmg` | Right-click the tray icon → **Check for Updates…**. Perch downloads the release and restarts itself. |
| The portable zip | Download the new release and replace the folder. |

The one-liner only downloads, verifies and runs the normal installer, so a copy installed that way updates
itself like any other. You never need to run the script again.

The portable zip is the exception. Perch will tell you a new version exists, but it won't overwrite a folder
it didn't install.

On macOS, remember to clear the quarantine flag again after updating by hand. See
[Install](/perch/getting-started/install/#perch-is-damaged-and-cant-be-opened).
