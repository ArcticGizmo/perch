#!/usr/bin/env bash
# Regenerates the macOS app icon (src/Perch.App/Assets/icon.icns) from the committed raster.
#
# The other icon assets (icon.png, icon.ico, landing-icon.png) come from tools/IconGen, which renders
# perch.svg via System.Drawing — a Windows-only runtime, so it can't run on a Mac. This script is the mac
# path the port plan calls for: it derives the .icns from landing-icon.png (512px, the highest-res raster
# IconGen emits from the same SVG + crop), using the stock `sips` + `iconutil` tools present on every Mac.
#
# Run after regenerating the PNGs (on Windows) or editing perch.svg, then commit the .icns:
#   tools/gen-icns.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src_png="$repo_root/landing-icon.png"          # 512x512, same SVG + crop as the other assets
out_icns="$repo_root/src/Perch.App/Assets/icon.icns"

if [[ ! -f "$src_png" ]]; then
    echo "Source raster not found: $src_png (run tools/IconGen first)" >&2
    exit 1
fi

iconset="$(mktemp -d)/icon.iconset"
mkdir -p "$iconset"

# Standard Retina iconset ladder. 1024 (512@2x) is upscaled from the 512 source — acceptable for an
# unsigned local build; the sizes the menu bar and Finder actually ask for (<=512) are downscales.
gen() { sips -z "$1" "$1" "$src_png" --out "$iconset/$2" >/dev/null; }
gen 16   icon_16x16.png
gen 32   icon_16x16@2x.png
gen 32   icon_32x32.png
gen 64   icon_32x32@2x.png
gen 128  icon_128x128.png
gen 256  icon_128x128@2x.png
gen 256  icon_256x256.png
gen 512  icon_256x256@2x.png
gen 512  icon_512x512.png
gen 1024 icon_512x512@2x.png

iconutil -c icns "$iconset" -o "$out_icns"
rm -rf "$(dirname "$iconset")"
echo "Wrote $out_icns"
