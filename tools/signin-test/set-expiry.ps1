# Moves the test account's access-token expiry so Perch's sign-in refresh comes due without waiting ~8 h.
# Prints only times, never token values.
#   .\tools\signin-test\set-expiry.ps1                     -> expires in 6 minutes (renewal test)
#   .\tools\signin-test\set-expiry.ps1 -Minutes -1         -> already expired
#   .\tools\signin-test\set-expiry.ps1 -BreakRefreshToken  -> also corrupts the refresh token (login-required test)
param(
    [int]$Minutes = 6,
    [switch]$BreakRefreshToken,
    [string]$ConfigDir = (Join-Path $env:USERPROFILE '.claude-envs\envs\test-dir')
)

$file = Join-Path $ConfigDir '.credentials.json'
if (-not (Test-Path -LiteralPath $file)) {
    Write-Host "No credentials at $file - sign in first: .\tools\signin-test\claude-test.ps1 auth login"
    exit 1
}

$json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
$oauth = $json.claudeAiOauth
if ($null -eq $oauth -or -not $oauth.refreshToken) {
    Write-Host "The test account is signed out - sign in first: .\tools\signin-test\claude-test.ps1 auth login"
    exit 1
}

$expires = [DateTimeOffset]::UtcNow.AddMinutes($Minutes)
$oauth.expiresAt = $expires.ToUnixTimeMilliseconds()
if ($BreakRefreshToken) {
    $oauth.refreshToken = 'sk-ant-ort01-broken-' + [guid]::NewGuid().ToString('N')
}
[IO.File]::WriteAllText($file, ($json | ConvertTo-Json -Depth 20 -Compress), (New-Object Text.UTF8Encoding $false))

Write-Host ("Access token now expires at {0:HH:mm:ss} local ({1} min)" -f $expires.ToLocalTime(), $Minutes)
if ($BreakRefreshToken) { Write-Host "Refresh token corrupted - expect Login required" }
