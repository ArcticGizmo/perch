@echo off
rem Regenerates every raster icon asset from the source-of-truth SVG (perch.svg).
rem
rem   src/icon.png      256x256 PNG  (window icons + in-app logo)
rem   src/icon.ico      multi-res ICO (tray icon + .exe ApplicationIcon)
rem   landing-icon.png  512x512 PNG  (README header)
rem
rem Run this after editing perch.svg, then commit the regenerated assets.

dotnet run --project "%~dp0IconGen" -c Release
