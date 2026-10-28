# Runs the dev tray pinned to the sign-in test config dir, so the test account is the tray's only account.
# Needed when the test dir is signed into the same account as another dir: the usage strip shows one row per
# account, and that row belongs to the first dir, so the test dir's pill and Login required button never show.
# Quit any other dev tray first. Run from the repo root.
param(
    [string]$ConfigDir = (Join-Path $env:USERPROFILE '.claude-envs\envs\test-dir')
)

if (-not (Test-Path -LiteralPath $ConfigDir)) {
    Write-Host "Test config dir not found: $ConfigDir"
    exit 1
}

Write-Host "Dev tray with CLAUDE_CONFIG_DIR=$ConfigDir"
$previous = $env:CLAUDE_CONFIG_DIR
$env:CLAUDE_CONFIG_DIR = $ConfigDir
try { & dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 }
finally { $env:CLAUDE_CONFIG_DIR = $previous }
