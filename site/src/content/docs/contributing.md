---
title: Contributing
description: Build Perch from source, preview UI changes, and cut a release.
---

Perch is a .NET 10 app with an [Avalonia](https://avaloniaui.net/) UI. The code is at
[github.com/ArcticGizmo/perch](https://github.com/ArcticGizmo/perch).

## Build and run

You need the .NET 10 SDK.

```sh
dotnet build perch.slnx
```

On Windows the app project builds for two targets, so `dotnet run` needs the Windows one named:

```sh
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0   # Windows
dotnet run --project src/Perch.App                                   # macOS
```

A Debug build runs as an isolated **dev instance** with its own settings and a pink border, so it can sit next
to an installed Perch without the two interfering.

## Tests

```sh
dotnet test tests/Perch.Tests/Perch.Tests.csproj
```

After changing `install.ps1`, also run the installer tests:

```powershell
powershell -NoProfile -File tools\test-install.ps1
```

Warnings are treated as errors in every project, so fix a new warning rather than suppressing it.

## Previewing UI changes

Most of Perch's UI is drawn by hand. To check a change without launching the tray, render every surface to PNG
at 1× and 1.5×:

```sh
dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render out   # Windows
dotnet run --project src/Perch.App -- render out                                   # macOS
```

Add a theme id as a third argument to render under a different theme.

## This site

The site lives in [`site/`](https://github.com/ArcticGizmo/perch/tree/main/site) and is built with
[Astro Starlight](https://starlight.astro.build/). The changelog, privacy page and `install.ps1` are copied in
from the repo root at build time, so edit the root files, not the copies.

```sh
cd site
npm ci
npm run dev
```

Pushing to `main` deploys it.

## Releasing

Releases are built by GitHub Actions when a version tag is pushed:

1. Bump `<Version>` in `src/Perch.App/Perch.App.csproj`.
2. Commit the change.
3. Push a matching tag, for example `git tag v0.6.0 && git push origin v0.6.0`.

The workflow builds and packages both platforms, writes `SHA256SUMS.txt` over the exact files it uploads, and
publishes them to the GitHub Release. Installed copies pick the release up through **Check for Updates…**.

Checksums prove the bytes you ran are the bytes that were published. They aren't a signature: the manifest sits
on the same release as the installer. Code signing is the fix for that, and is tracked in
[`docs/distribution-plan.md`](https://github.com/ArcticGizmo/perch/blob/main/docs/distribution-plan.md).
