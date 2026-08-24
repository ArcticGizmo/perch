#!/usr/bin/env bash
# Regenerates the drag-install DMG background (tools/dmg-background.png) from tools/gen-dmg-background.swift.
#
# Mirrors tools/gen-icns.sh: a committed raster + a regenerate script, using only stock macOS tooling
# (swift ships with the Xcode Command Line Tools; sips stamps the DPI). publish-mac.sh consumes the PNG and
# also falls back to running this on demand if it's missing. Re-run after editing the .swift renderer, then
# commit the PNG:
#   tools/gen-dmg-background.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
swift_src="$repo_root/tools/gen-dmg-background.swift"
out_png="$repo_root/tools/dmg-background.png"

if ! command -v swift >/dev/null 2>&1; then
    echo "Error: swift not found (install the Xcode Command Line Tools: xcode-select --install)" >&2
    exit 1
fi

swift "$swift_src" "$out_png"

# The renderer emits 1280x800 px; stamp 144 DPI so Finder maps it to the 640x400-point window content area
# and draws it crisply on Retina rather than blowing it up 2x.
sips -s dpiWidth 144 -s dpiHeight 144 "$out_png" >/dev/null

echo "Wrote $out_png"
