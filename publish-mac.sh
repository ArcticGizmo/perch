#!/usr/bin/env bash
# macOS sibling of publish.bat: builds an unsigned arm64 Perch.app + DMG for local distribution.
#
#   ./publish-mac.sh [version]
#
# With no argument the version is read from src/Perch.App/Perch.App.csproj (<Version>). Mirrors the
# Windows script's three steps — publish perch, publish perch-hook alongside it, then `vpk pack` — but
# targets osx-arm64 and hands vpk our own Info.plist so LSUIElement + NSAppleEventsUsageDescription
# survive into the bundle (see src/Perch.App/Info.plist).
#
# Output lands in releases/ : Perch-<ver>-osx-arm64.dmg (drag-install), a portable .zip, and the update
# feed. The build is UNSIGNED — see the README's "macOS (unsigned)" note for the Gatekeeper workaround.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

RID="osx-arm64"
PUBLISH_DIR="publish"
OUT_DIR="releases"
PLIST_SRC="src/Perch.App/Info.plist"
ICNS="src/Perch.App/Assets/icon.icns"
# The bundle id (com.arcticgizmo.perch) lives in Info.plist; vpk rejects --bundleId alongside --plist.

# --- version -------------------------------------------------------------------------------------
if [[ $# -ge 1 && -n "$1" ]]; then
    VERSION="$1"
else
    VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Perch.App/Perch.App.csproj | head -1)"
fi
if [[ -z "${VERSION:-}" ]]; then
    echo "Error: could not determine version. Pass it: ./publish-mac.sh 1.2.3" >&2
    exit 1
fi

# --- prerequisites -------------------------------------------------------------------------------
if ! command -v vpk >/dev/null 2>&1; then
    echo "Error: the vpk CLI isn't on PATH. Install it: dotnet tool install -g vpk" >&2
    echo "(then ensure ~/.dotnet/tools is on PATH)" >&2
    exit 1
fi
if [[ ! -f "$ICNS" ]]; then
    echo "$ICNS missing — generating it..." >&2
    ./tools/gen-icns.sh
fi

echo "Building Perch v$VERSION ($RID)..."
# Clean both dirs so a re-run is repeatable — Velopack refuses to pack over an existing release of the
# same version, and stale files in the publish dir would otherwise be packed into the bundle.
rm -rf "$PUBLISH_DIR" "$OUT_DIR"

# All intermediate work stays under releases/ (a project-local, gitignored dir) rather than the system
# temp dir, so everything the build touches is visible next to the artifacts. WORK is cleaned on exit.
WORK="$OUT_DIR/.work"
mkdir -p "$WORK"
trap 'rm -rf "$WORK"' EXIT

# --- perch (the app head, self-contained) --------------------------------------------------------
# net10.0 is the only head on a Mac host, but the project still declares TargetFrameworks (plural), so
# `publish` demands an explicit -f. Self-contained so the .app has no external .NET dependency. No
# PublishSingleFile: a .app bundle already gathers the files under Contents/MacOS.
dotnet publish src/Perch.App/Perch.App.csproj -c Release -f net10.0 -r "$RID" --self-contained true \
    -p:Version="$VERSION" \
    -p:DebugType=embedded \
    -o "$PUBLISH_DIR"

# --- perch-hook (NativeAOT, into the SAME dir so vpk packs them together) ------------------------
# HookInstaller copies this out of the bundle to a stable per-user bin on first launch.
echo "Publishing perch-hook (NativeAOT) ..."
dotnet publish src/Perch.Hook/Perch.Hook.csproj -c Release -r "$RID" \
    -p:Version="$VERSION" \
    -o "$PUBLISH_DIR"

# NativeAOT leaves a perch-hook.dSYM debug-symbol bundle next to the binary; it has no place in a shipped
# .app (the Windows lane strips .pdb the same way via vpk's default --exclude).
rm -rf "$PUBLISH_DIR"/*.dSYM

# --- Info.plist: substitute the release version into a local working copy ------------------------
plist_tmp="$WORK/Info.plist"
sed "s/__VERSION__/$VERSION/g" "$PLIST_SRC" > "$plist_tmp"

# --- pack the unsigned .app + DMG ----------------------------------------------------------------
echo "Packaging ..."
vpk pack \
    --runtime "$RID" \
    --packId Perch \
    --packTitle "Perch" \
    --packAuthors "ArcticGizmo" \
    --packVersion "$VERSION" \
    --packDir "$PUBLISH_DIR" \
    --mainExe perch \
    --icon "$ICNS" \
    --plist "$plist_tmp" \
    --outputDir "$OUT_DIR"

# --- DMG + a runnable local Perch.app -------------------------------------------------------------
# Velopack's mac lane emits a .pkg installer + a portable .zip, but not a .dmg, and leaves no loose .app
# (it assembles the bundle in its own temp dir and deletes it). Unpack the portable zip locally so:
#   * releases/Perch.app  is a runnable bundle you can launch/inspect without mounting anything, and
#   * the DMG is built from it — a compressed image with an /Applications drop target (the familiar
#     unsigned-mac drag-install UX the README points users at).
echo "Building DMG ..."
dmg="$OUT_DIR/Perch-$VERSION-osx-arm64.dmg"
app="$OUT_DIR/Perch.app"
rm -rf "$app"
unzip -q "$OUT_DIR/Perch-osx-Portable.zip" -d "$OUT_DIR"   # yields releases/Perch.app

# hdiutil needs a folder holding the .app + the Applications symlink; stage that under WORK (a transient
# copy, cleaned on exit) so the kept releases/Perch.app stays a plain bundle, not a DMG source tree.
stage="$WORK/dmg"
mkdir -p "$stage"
cp -R "$app" "$stage/Perch.app"
ln -s /Applications "$stage/Applications"
rm -f "$dmg"
hdiutil create -volname "Perch" -srcfolder "$stage" -ov -format UDZO "$dmg" >/dev/null

echo
echo "Release artifacts ready in: $OUT_DIR/"
echo "  $(basename "$dmg")   <- drag-install DMG (see README Gatekeeper note; unsigned)"
echo "  Perch.app                  <- runnable bundle (open $app)"
echo "Upload to: https://github.com/ArcticGizmo/perch/releases/new?tag=v$VERSION"
