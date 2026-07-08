#!/usr/bin/env pwsh
# Regenerates every raster icon asset from the source-of-truth SVG (perch.svg).
#
#   src/Perch.App/Assets/icon.png   256x256 PNG  (window icons + in-app logo)
#   src/Perch.App/Assets/icon.ico   multi-res ICO (tray icon + .exe ApplicationIcon)
#   landing-icon.png                512x512 PNG  (README header)
#
# The macOS app icon (Assets/icon.icns) is regenerated separately by tools/gen-icns.sh — it derives from
# landing-icon.png via sips/iconutil, because this IconGen path renders the SVG through System.Drawing,
# which only runs on Windows.
#
# Run this after editing perch.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
