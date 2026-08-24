#!/usr/bin/env bash
# macOS sibling of publish.bat: builds an unsigned arm64 Perch.app + DMG for local distribution.
#
#   ./publish-mac.sh [version]
#
# With no argument the version is read from src/Perch.App/Perch.App.csproj (<Version>). Mirrors the
# Windows script's three steps - publish perch, publish perch-hook alongside it, then `vpk pack` - but
# targets osx-arm64 and hands vpk our own Info.plist so LSUIElement + NSAppleEventsUsageDescription
# survive into the bundle (see src/Perch.App/Info.plist).
#
# Output lands in releases/ : Perch-osx-arm64.dmg (drag-install), a portable .zip, and the update
# feed. The build is UNSIGNED - see the README's "macOS (unsigned)" note for the Gatekeeper workaround.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

RID="osx-arm64"
PUBLISH_DIR="publish"
OUT_DIR="releases"
PLIST_SRC="src/Perch.App/Info.plist"
ICNS="src/Perch.App/Assets/icon.icns"
DMG_BG="tools/dmg-background.png"   # styled drag-install backdrop; regen via tools/gen-dmg-background.sh
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
    echo "$ICNS missing - generating it..." >&2
    ./tools/gen-icns.sh
fi
if [[ ! -f "$DMG_BG" ]]; then
    echo "$DMG_BG missing — generating it..." >&2
    ./tools/gen-dmg-background.sh
fi

echo "Building Perch v$VERSION ($RID)..."
# Clean both dirs so a re-run is repeatable - Velopack refuses to pack over an existing release of the
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
#   * the DMG is built from it - a compressed image with an /Applications drop target (the familiar
#     unsigned-mac drag-install UX the README points users at).
echo "Building DMG ..."
dmg="$OUT_DIR/Perch-osx-arm64.dmg"
app="$OUT_DIR/Perch.app"
rm -rf "$app"
unzip -q "$OUT_DIR/Perch-osx-Portable.zip" -d "$OUT_DIR"   # yields releases/Perch.app

VOL="Perch"

# Stage the DMG's contents under WORK (a transient copy, cleaned on exit) so the kept releases/Perch.app
# stays a plain bundle, not a DMG source tree. The layout Finder styling expects:
#   Perch.app                     <- the app the user drags
#   Applications -> /Applications  <- the drop target
#   .background/background.png     <- the drag-to-install backdrop (hidden)
#   .VolumeIcon.icns               <- shows the Perch icon on the mounted volume
stage="$WORK/dmg"
mkdir -p "$stage/.background"
cp -R "$app" "$stage/Perch.app"
ln -s /Applications "$stage/Applications"
cp "$DMG_BG" "$stage/.background/background.png"
cp "$ICNS" "$stage/.VolumeIcon.icns"

# Build a read-write image first, mount it, style the Finder window (icon view + background + icon
# positions), then convert to the compressed read-only DMG we ship. A bare `hdiutil create -format UDZO`
# can't be styled after the fact, which is why the old drag-install window was just a naked folder.
rw="$WORK/perch-rw.dmg"
rm -f "$rw" "$dmg"
hdiutil detach "/Volumes/$VOL" >/dev/null 2>&1 || true   # clear a stale mount from an aborted run
hdiutil create -volname "$VOL" -srcfolder "$stage" -fs HFS+ -format UDRW -ov "$rw" >/dev/null

echo "Styling DMG window ..."
dev="$(hdiutil attach -readwrite -noverify -noautoopen "$rw" | grep -Eo '/dev/disk[0-9]+' | head -1)"
vol="/Volumes/$VOL"

# Best-effort Finder styling: on a headless/locked session the AppleScript can fail — the DMG is still a
# functional drag-install image (app + Applications alias + background folder), so warn and carry on rather
# than sinking the whole release.
if osascript <<APPLESCRIPT
tell application "Finder"
    tell disk "$VOL"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {200, 120, 840, 520}
        set opts to the icon view options of container window
        set arrangement of opts to not arranged
        set icon size of opts to 128
        set text size of opts to 12
        set background picture of opts to file ".background:background.png"
        set position of item "Perch.app" of container window to {172, 170}
        set position of item "Applications" of container window to {468, 170}
        set position of item ".background" of container window to {900, 900}
        set position of item ".VolumeIcon.icns" of container window to {900, 700}
        update without registering applications
        delay 1
        close
    end tell
end tell
APPLESCRIPT
then
    # Flag the volume so Finder honours .VolumeIcon.icns (needs the custom-icon attribute set).
    if command -v SetFile >/dev/null 2>&1; then SetFile -a C "$vol" || true; fi
else
    echo "Warning: Finder styling failed (headless session?); shipping an unstyled but working DMG." >&2
fi

sync
hdiutil detach "$dev" >/dev/null 2>&1 || hdiutil detach "$vol" >/dev/null 2>&1 || true
hdiutil convert "$rw" -format UDZO -imagekey zlib-level=9 -ov -o "$dmg" >/dev/null

echo
echo "Release artifacts ready in: $OUT_DIR/"
echo "  $(basename "$dmg")   <- drag-install DMG (see README Gatekeeper note; unsigned)"
echo "  Perch.app                  <- runnable bundle (open $app)"
echo "Upload to: https://github.com/ArcticGizmo/perch/releases/new?tag=v$VERSION"
