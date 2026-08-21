# Perch sample plugin: Weather Badge.
# Fetches the current temperature from the (free, no-key) Open-Meteo API for a fixed location and paints
# it as an overlay glyph. Demonstrates the "network" capability: the consent dialog lists api.open-meteo.com
# and only an explicit Allow lets it run. Reads nothing about your machine or sessions.
#
# ASCII-only source; the sun and degree symbols are emitted via char codes and ConvertTo-Json escapes them,
# so the file stays plain ASCII and the output stays valid JSON.

$ErrorActionPreference = 'SilentlyContinue'

# Edit these for your location (defaults to Melbourne, AU).
$lat = -37.81
$lon = 144.96

# Drain the request line (this plugin needs no context).
[Console]::In.ReadLine() | Out-Null

$sun = [char]0x2600      # a small sun glyph
$deg = [char]0x00B0      # degree sign

try {
    $uri = "https://api.open-meteo.com/v1/forecast?latitude=$lat&longitude=$lon&current=temperature_2m"
    $resp = Invoke-RestMethod -Uri $uri -TimeoutSec 10
    $t = [math]::Round([double]$resp.current.temperature_2m)
    $obj = @{ type = 'render'; glyph = @{ glyph = "$sun"; text = "$t$deg"; tooltip = "$t$deg C at $lat,$lon" } }
}
catch {
    $obj = @{ type = 'render'; glyph = @{ glyph = ''; text = '--'; tooltip = 'Weather unavailable' } }
}

# Write the JSON as raw UTF-8 bytes so the sun/degree symbols survive regardless of the console codepage
# (Perch reads plugin stdout as UTF-8).
$json = ($obj | ConvertTo-Json -Compress)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
$stdout = [Console]::OpenStandardOutput()
$stdout.Write($bytes, 0, $bytes.Length)
$stdout.Flush()
exit 0
