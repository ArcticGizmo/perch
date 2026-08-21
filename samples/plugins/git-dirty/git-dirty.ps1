# Perch sample plugin: Git Dirty Count.
# Reads Perch's one-line poll request from stdin, looks at the active session's project directory
# (delivered in context.cwd only because the manifest requests the read.cwd capability), counts the
# uncommitted changes there, and prints one render line. Emits nothing else - no network, no writes.
#
# Kept ASCII-only and JSON is built via ConvertTo-Json so backslashes in a Windows path are escaped
# correctly (a naive string concat would emit invalid JSON and Perch would drop the line).

$ErrorActionPreference = 'SilentlyContinue'

function Send-Render($text, $tooltip) {
    $obj = @{ type = 'render'; glyph = @{ glyph = ''; text = "$text"; tooltip = "$tooltip" } }
    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress))
}

# Read the request (we only need context.cwd).
$line = [Console]::In.ReadLine()
$cwd = $null
if ($line) {
    try { $cwd = ($line | ConvertFrom-Json).context.cwd } catch { $cwd = $null }
}

if (-not $cwd) {
    # No project directory (no active session, or the read.cwd capability was declined).
    Send-Render '-' 'No active project directory'
    exit 0
}

# Count porcelain lines; each is one changed/untracked path.
$status = & git -C "$cwd" status --porcelain 2>$null
$count = 0
if ($status) { $count = @($status).Count }

$leaf = Split-Path -Leaf "$cwd"
if ($count -eq 0) {
    Send-Render 'clean' "$leaf is clean"
} else {
    Send-Render "$count" "$count uncommitted change(s) in $leaf"
}
exit 0
