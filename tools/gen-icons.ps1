#!/usr/bin/env pwsh
# Regenerates every raster icon asset from the source-of-truth SVG (perch.svg).
#
#   src/icon.png      256x256 PNG  (window icons + in-app logo)
#   src/icon.ico      multi-res ICO (tray icon + .exe ApplicationIcon)
#   landing-icon.png  512x512 PNG  (README header)
#
# Run this after editing perch.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
