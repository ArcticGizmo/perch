<#
.SYNOPSIS
  Test whether Perch can focus the terminal window hosting a Claude Code session.

.DESCRIPTION
  Reproduces WindowActivator's decision for a given process id and reports it, then
  (unless -NoFocus) actually raises the resolved window so you can confirm it works.

  It runs BOTH resolvers and compares them:
    1. Traversal   - walk the process ancestry, collect windows via GA_ROOTOWNER,
                     exclude explorer.exe, pick the closest non-explorer host.
                     (This is what Perch does today.)
    2. AttachConsole - attach to the target's console, GetConsoleWindow -> GA_ROOTOWNER.
                     (The authoritative fallback; see docs/terminal-focus-correlation.md.)

  Pass the claude.exe pid of the session (the "pid" field in
  ~/.claude/sessions/<pid>.json). Passing the shell pid works too.

.PARAMETER ProcessId
  The pid to resolve (positional). Usually the session's claude.exe pid.

.PARAMETER Project
  Optional project/title hint used to disambiguate multiple windows sharing a host
  (mirrors Perch's projectHint). Case-insensitive substring match on window title.

.PARAMETER NoFocus
  Diagnose only; do not raise any window.

.EXAMPLE
  tools\focus.ps1 25544
  tools\focus.ps1 25544 -Project Safety
  tools\focus.ps1 25544 -NoFocus
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [int] $ProcessId,
    [string] $Project,
    [switch] $NoFocus
)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Focus {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    // elevation
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int outLen);
    public static string Title(IntPtr h){int n=GetWindowTextLength(h); if(n<=0) return ""; var sb=new StringBuilder(n+1); GetWindowText(h,sb,sb.Capacity); return sb.ToString();}
    public static string Cls(IntPtr h){var sb=new StringBuilder(256); GetClassName(h,sb,256); return sb.ToString();}
}
"@

$GA_ROOTOWNER = 3
$SW_SHOW = 5
$SW_RESTORE = 9

# --- process snapshot: pid -> @{ Parent; Name } ---
$proc = @{}
Get-CimInstance Win32_Process | ForEach-Object {
    $proc[[int]$_.ProcessId] = @{ Parent = [int]$_.ParentProcessId; Name = $_.Name }
}

function Get-Name([int]$id) { if ($proc.ContainsKey($id)) { $proc[$id].Name } else { '(gone)' } }

if (-not $proc.ContainsKey($ProcessId)) {
    Write-Host "PID $ProcessId is not a running process." -ForegroundColor Red
    exit 2
}

Write-Host ""
Write-Host "Target: pid=$ProcessId ($(Get-Name $ProcessId))" -ForegroundColor Cyan

# --- ancestry (closest-first), with explorer marked excluded ---
$depthByPid = @{}
$excluded = New-Object System.Collections.Generic.HashSet[int]
$cur = $ProcessId
for ($d = 0; $d -lt 10 -and $cur -gt 0; $d++) {
    if ($depthByPid.ContainsKey($cur)) { break }
    $depthByPid[$cur] = $d
    $n = Get-Name $cur
    $mark = ''
    if ($n -ieq 'explorer.exe') { [void]$excluded.Add($cur); $mark = '   <- excluded (never a host)' }
    "  depth {0}: pid={1} {2}{3}" -f $d, $cur, $n, $mark | Write-Host
    if ($proc.ContainsKey($cur)) { $cur = $proc[$cur].Parent } else { $cur = 0 }
}

# --- elevation of target (best-effort, via TokenElevation) ---
# Returns 'elevated', 'not elevated', or 'denied' (can't open -> almost always higher integrity).
function Get-Elevation([int]$id) {
    $PROCESS_QUERY_LIMITED = 0x1000
    $TOKEN_QUERY = 0x0008
    $TokenElevation = 20
    $h = [Focus]::OpenProcess($PROCESS_QUERY_LIMITED, $false, [uint32]$id)
    if ($h -eq [IntPtr]::Zero) { return 'denied' }
    try {
        $tok = [IntPtr]::Zero
        if (-not [Focus]::OpenProcessToken($h, $TOKEN_QUERY, [ref]$tok)) { return 'unknown' }
        try {
            $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal(4)
            try {
                $outLen = 0
                if (-not [Focus]::GetTokenInformation($tok, $TokenElevation, $buf, 4, [ref]$outLen)) { return 'unknown' }
                if ([Runtime.InteropServices.Marshal]::ReadInt32($buf) -ne 0) { return 'elevated' } else { return 'not elevated' }
            } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buf) }
        } finally { [void][Focus]::CloseHandle($tok) }
    } finally { [void][Focus]::CloseHandle($h) }
}

$targetElev = Get-Elevation $ProcessId
$selfElev = Get-Elevation ([int]$PID)
Write-Host ""
Write-Host "Elevation: target=$targetElev ; this script=$selfElev"
if (($targetElev -eq 'elevated' -or $targetElev -eq 'denied') -and $selfElev -ne 'elevated') {
    Write-Host "  WARNING: target appears to run elevated / at higher integrity than this script." -ForegroundColor Yellow
    Write-Host "           UIPI will block SetForegroundWindow - focus will fail even if the window is found." -ForegroundColor Yellow
}

# --- resolver 1: traversal ---
function Resolve-Traversal([bool]$includeHidden) {
    $script:hits = New-Object System.Collections.ArrayList
    $cb = [Focus+EnumProc] {
        param($h, $l)
        if (-not $includeHidden -and -not [Focus]::IsWindowVisible($h)) { return $true }
        [uint32]$wp = 0; [Focus]::GetWindowThreadProcessId($h, [ref]$wp) | Out-Null
        $iwp = [int]$wp
        if ($excluded.Contains($iwp)) { return $true }
        if (-not $depthByPid.ContainsKey($iwp)) { return $true }
        $owner = [Focus]::GetAncestor($h, $GA_ROOTOWNER); if ($owner -eq [IntPtr]::Zero) { $owner = $h }
        [void]$script:hits.Add([pscustomobject]@{ Depth = $depthByPid[$iwp]; HWnd = $owner; Title = [Focus]::Title($owner) })
        return $true
    }
    [Focus]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    if ($script:hits.Count -eq 0) { return $null }
    $closest = ($script:hits | Sort-Object Depth | Select-Object -First 1).Depth
    $atClosest = @($script:hits | Where-Object { $_.Depth -eq $closest })
    $chosen = $null
    if ($Project) { $chosen = $atClosest | Where-Object { $_.Title -and $_.Title.ToLower().Contains($Project.ToLower()) } | Select-Object -First 1 }
    if (-not $chosen) { $chosen = $atClosest[0] }
    return $chosen
}

$trav = Resolve-Traversal $false
if (-not $trav) { $trav = Resolve-Traversal $true }

Write-Host ""
Write-Host "[1] Traversal resolver:" -ForegroundColor Cyan
if ($trav) {
    [uint32]$op = 0; [Focus]::GetWindowThreadProcessId($trav.HWnd, [ref]$op) | Out-Null
    Write-Host ("    hwnd={0} depth={1} owner=pid {2} ({3}) title='{4}'" -f $trav.HWnd, $trav.Depth, $op, (Get-Name ([int]$op)), $trav.Title) -ForegroundColor Green
} else {
    Write-Host "    no host window found (only explorer/none in ancestry) -> traversal FAILS" -ForegroundColor Yellow
}

# --- resolver 2: AttachConsole ---
# Runs in a CHILD powershell so its FreeConsole/AttachConsole juggling cannot disturb this
# process's own console (which Write-Host needs). The child prints one pipe-delimited line.
$AttachChild = @'
param([int]$Id)
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class AC {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool FreeConsole();
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool AttachConsole(uint pid);
 [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
 [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h,uint f);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 public static string Title(IntPtr h){int n=GetWindowTextLength(h);if(n<=0)return "";var sb=new StringBuilder(n+1);GetWindowText(h,sb,sb.Capacity);return sb.ToString();}
 public static string Cls(IntPtr h){var sb=new StringBuilder(256);GetClassName(h,sb,256);return sb.ToString();}
}
"@
[void][AC]::FreeConsole()
if (-not [AC]::AttachConsole([uint32]$Id)) { "FAIL|" + [Runtime.InteropServices.Marshal]::GetLastWin32Error(); return }
$cw = [AC]::GetConsoleWindow()
if ($cw -eq [IntPtr]::Zero) { [void][AC]::FreeConsole(); "NOCON|"; return }
$o = [AC]::GetAncestor($cw, 3); if ($o -eq [IntPtr]::Zero) { $o = $cw }
[uint32]$op = 0; [AC]::GetWindowThreadProcessId($o, [ref]$op) | Out-Null
$line = "OK|" + $cw.ToInt64() + "|" + $o.ToInt64() + "|" + $op + "|" + [AC]::Cls($cw) + "|" + [AC]::Title($o)
[void][AC]::FreeConsole()
$line
'@

function Resolve-AttachConsole([int]$id) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("focus-attach-" + [Guid]::NewGuid().ToString('N') + ".ps1")
    Set-Content -Path $tmp -Value $AttachChild -Encoding UTF8
    try {
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $tmp -Id $id 2>$null
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

    $line = @($out | Where-Object { $_ -match '\|' } | Select-Object -Last 1)
    if (-not $line) { return [pscustomobject]@{ Ok = $false; Err = -1 } }
    $parts = ([string]$line).Split('|')
    switch ($parts[0]) {
        'OK'    { return [pscustomobject]@{ Ok = $true; Console = [IntPtr][int64]$parts[1]; HWnd = [IntPtr][int64]$parts[2]; OwnerPid = [int]$parts[3]; ConsoleCls = $parts[4]; Title = ($parts[5..($parts.Length-1)] -join '|') } }
        'NOCON' { return [pscustomobject]@{ Ok = $false; Err = 0; NoConsoleWindow = $true } }
        'FAIL'  { return [pscustomobject]@{ Ok = $false; Err = [int]$parts[1] } }
        default { return [pscustomobject]@{ Ok = $false; Err = -1 } }
    }
}

$att = Resolve-AttachConsole $ProcessId
Write-Host ""
Write-Host "[2] AttachConsole resolver:" -ForegroundColor Cyan
$attHwnd = [IntPtr]::Zero
if ($att.Ok) {
    [uint32]$op2 = 0; [Focus]::GetWindowThreadProcessId($att.HWnd, [ref]$op2) | Out-Null
    $attHwnd = $att.HWnd
    Write-Host ("    console={0} ({1}) -> hwnd={2} owner=pid {3} ({4}) title='{5}'" -f $att.Console, $att.ConsoleCls, $att.HWnd, $op2, (Get-Name ([int]$op2)), $att.Title) -ForegroundColor Green
} elseif ($att.NoConsoleWindow) {
    Write-Host "    attached, but GetConsoleWindow=0 (mintty/Git Bash or no console window)" -ForegroundColor Yellow
} else {
    $emap = @{ 5 = 'ACCESS_DENIED (higher integrity?)'; 6 = 'INVALID_HANDLE'; 87 = 'INVALID_PARAMETER (no such console / process gone)' }
    $desc = if ($emap.ContainsKey($att.Err)) { $emap[$att.Err] } else { "err=$($att.Err)" }
    Write-Host "    AttachConsole failed: $desc" -ForegroundColor Yellow
}

# --- agreement ---
Write-Host ""
if ($trav -and $att.Ok) {
    if ($trav.HWnd -eq $attHwnd) { Write-Host "Both resolvers AGREE on hwnd $($trav.HWnd)." -ForegroundColor Green }
    else { Write-Host "Resolvers DISAGREE: traversal=$($trav.HWnd) attach=$attHwnd" -ForegroundColor Yellow }
} elseif (-not $trav -and $att.Ok) {
    Write-Host "Traversal failed; AttachConsole rescued it -> hwnd $attHwnd (this is the DefTerm/conhost case)." -ForegroundColor Yellow
} elseif ($trav -and -not $att.Ok) {
    Write-Host "AttachConsole unavailable; traversal is the answer." -ForegroundColor Yellow
} else {
    Write-Host "Neither resolver found a window." -ForegroundColor Red
}

# --- focus action ---
$target = if ($trav) { $trav.HWnd } elseif ($att.Ok) { $attHwnd } else { [IntPtr]::Zero }

if ($NoFocus) {
    Write-Host ""
    Write-Host "(-NoFocus: not raising anything.)"
    exit 0
}
if ($target -eq [IntPtr]::Zero) {
    Write-Host ""
    Write-Host "Nothing to focus." -ForegroundColor Red
    exit 1
}

function Set-Focus([IntPtr]$h) {
    if (-not [Focus]::IsWindow($h)) { return $false }
    if (-not [Focus]::IsWindowVisible($h)) { [void][Focus]::ShowWindow($h, $SW_SHOW) }
    if ([Focus]::IsIconic($h)) { [void][Focus]::ShowWindow($h, $SW_RESTORE) }
    $foreThread = [Focus]::GetWindowThreadProcessId([Focus]::GetForegroundWindow(), [ref]([uint32]0))
    $thisThread = [Focus]::GetCurrentThreadId()
    if ($foreThread -ne 0 -and $foreThread -ne $thisThread) {
        [void][Focus]::AttachThreadInput($foreThread, $thisThread, $true)
        $r = [Focus]::SetForegroundWindow($h)
        [void][Focus]::AttachThreadInput($foreThread, $thisThread, $false)
    } else {
        $r = [Focus]::SetForegroundWindow($h)
    }
    return $r
}

Write-Host ""
Write-Host "Focusing hwnd $target ..." -ForegroundColor Cyan
$ok = Set-Focus $target
Start-Sleep -Milliseconds 150
$now = [Focus]::GetForegroundWindow()
if ($now -eq $target) {
    Write-Host "SUCCESS: it is now the foreground window." -ForegroundColor Green
    exit 0
} else {
    Write-Host "SetForegroundWindow returned $ok, but foreground is now $now (not the target)." -ForegroundColor Yellow
    Write-Host "  If the target is elevated and this script is not, UIPI blocks the raise." -ForegroundColor Yellow
    exit 1
}
