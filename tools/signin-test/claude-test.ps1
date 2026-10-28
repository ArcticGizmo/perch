# Runs claude against the throwaway sign-in test config dir, so your real sign-in is never touched.
# Any arguments pass straight through:
#   .\tools\signin-test\claude-test.ps1              -> interactive claude on the test account
#   .\tools\signin-test\claude-test.ps1 auth login   -> sign the test account back in
param(
    [string]$ConfigDir = (Join-Path $env:USERPROFILE '.claude-envs\envs\test-dir'),
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ClaudeArgs
)

if (-not (Test-Path -LiteralPath $ConfigDir)) {
    Write-Host "Test config dir not found: $ConfigDir"
    exit 1
}

Write-Host "claude with CLAUDE_CONFIG_DIR=$ConfigDir"
$previous = $env:CLAUDE_CONFIG_DIR
$env:CLAUDE_CONFIG_DIR = $ConfigDir
try { & claude @ClaudeArgs }
finally { $env:CLAUDE_CONFIG_DIR = $previous }
