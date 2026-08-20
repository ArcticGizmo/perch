<#
.SYNOPSIS
    Spike: prove that a normal app can reserve a screen edge so maximized windows
    never overlap it, using the Windows AppBar API (SHAppBarMessage).

.DESCRIPTION
    Registers a right-edge "application desktop toolbar" (the same mechanism the
    taskbar uses) the width of a mock Perch column, then parks a small always-on-top
    form in the reserved strip. While it is registered the OS shrinks the desktop
    work area, so any window you maximize stops at the strip's left edge instead of
    covering it.

    Prints the primary screen's work area BEFORE, DURING and AFTER registration so
    you can see the reservation take effect (and be cleanly released) even without
    watching the screen.

.PARAMETER Width
    Width of the reserved column in physical pixels. Default 320 (roughly Perch's
    floating panel width).

.PARAMETER Edge
    Which edge to dock to: Right (default), Left, Top, Bottom.

.PARAMETER Seconds
    If > 0, auto-unregister and exit after this many seconds (good for an unattended
    smoke test). If 0 (default), the strip stays until you close its window.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge.ps1
    # Reserves a 320px right column until you close the little window.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge.ps1 -Seconds 8
    # Reserves for 8s then cleanly releases - safe unattended smoke test.
#>
[CmdletBinding()]
param(
    [int]$Width = 320,
    [ValidateSet('Left','Top','Right','Bottom')]
    [string]$Edge = 'Right',
    [int]$Seconds = 0
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Per-monitor coordinates come back physical only when the process is DPI-aware.
# System-DPI-aware is enough for a primary-monitor spike. (The real Perch head is
# already per-monitor aware via Avalonia.)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AppBarInterop {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA {
        public int    cbSize;
        public IntPtr hWnd;
        public uint   uCallbackMessage;
        public uint   uEdge;
        public RECT   rc;
        public IntPtr lParam;
    }

    // dwMessage values
    public const uint ABM_NEW      = 0x0;
    public const uint ABM_REMOVE   = 0x1;
    public const uint ABM_QUERYPOS = 0x2;
    public const uint ABM_SETPOS   = 0x3;

    // uEdge values
    public const uint ABE_LEFT   = 0;
    public const uint ABE_TOP    = 1;
    public const uint ABE_RIGHT  = 2;
    public const uint ABE_BOTTOM = 3;

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    // Authoritative work-area read - NOT cached like WinForms Screen.WorkingArea.
    public const uint SPI_GETWORKAREA = 0x0030;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
}
"@

[void][AppBarInterop]::SetProcessDPIAware()

function Get-WorkArea {
    $r = New-Object AppBarInterop+RECT
    [void][AppBarInterop]::SystemParametersInfo([AppBarInterop]::SPI_GETWORKAREA, 0, [ref]$r, 0)
    return "X=$($r.left) Y=$($r.top) W=$($r.right - $r.left) H=$($r.bottom - $r.top)"
}

$edgeConst = switch ($Edge) {
    'Left'   { [AppBarInterop]::ABE_LEFT }
    'Top'    { [AppBarInterop]::ABE_TOP }
    'Right'  { [AppBarInterop]::ABE_RIGHT }
    'Bottom' { [AppBarInterop]::ABE_BOTTOM }
}

# A visible marker window so you can see the reserved strip. Topmost + no taskbar
# button, matching how Perch's overlay presents.
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.TopMost         = $true
$form.ShowInTaskbar   = $false
$form.StartPosition   = 'Manual'
$form.BackColor       = [System.Drawing.Color]::FromArgb(30, 34, 42)

$label = New-Object System.Windows.Forms.Label
$label.Dock      = 'Fill'
$label.ForeColor = [System.Drawing.Color]::White
$label.TextAlign = 'MiddleCenter'
$label.Font      = New-Object System.Drawing.Font('Segoe UI', 11)
$label.Text      = "Perch column`n(reserved via AppBar)`n`nMaximize any window -`nit stops at my edge.`n`nClose me to release."
$form.Controls.Add($label)

# Force the handle to exist so hWnd is valid before ABM_NEW.
$hwnd = $form.Handle

$abd = New-Object AppBarInterop+APPBARDATA
$abd.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($abd)
$abd.hWnd   = $hwnd
$abd.uEdge  = $edgeConst

Write-Host "Work area BEFORE reserve: $(Get-WorkArea)" -ForegroundColor Cyan

# 1) Register the appbar.
[void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_NEW, [ref]$abd)

# 2) Propose the full-edge strip on the primary monitor.
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$rc = New-Object AppBarInterop+RECT
switch ($Edge) {
    'Right'  { $rc.left = $bounds.Right - $Width; $rc.top = $bounds.Top; $rc.right = $bounds.Right; $rc.bottom = $bounds.Bottom }
    'Left'   { $rc.left = $bounds.Left; $rc.top = $bounds.Top; $rc.right = $bounds.Left + $Width; $rc.bottom = $bounds.Bottom }
    'Top'    { $rc.left = $bounds.Left; $rc.top = $bounds.Top; $rc.right = $bounds.Right; $rc.bottom = $bounds.Top + $Width }
    'Bottom' { $rc.left = $bounds.Left; $rc.top = $bounds.Bottom - $Width; $rc.right = $bounds.Right; $rc.bottom = $bounds.Bottom }
}
$abd.rc = $rc

# 3) Let the shell adjust the rect around any existing appbars (taskbar, etc.).
[void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_QUERYPOS, [ref]$abd)

# 4) After QUERYPOS the edge-perpendicular extent may have moved; re-pin our thickness.
switch ($Edge) {
    'Right'  { $abd.rc.left   = $abd.rc.right - $Width }
    'Left'   { $abd.rc.right  = $abd.rc.left + $Width }
    'Top'    { $abd.rc.bottom = $abd.rc.top + $Width }
    'Bottom' { $abd.rc.top    = $abd.rc.bottom - $Width }
}

# 5) Commit the reservation - THIS is what shrinks the desktop work area.
[void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_SETPOS, [ref]$abd)

# Park the marker window in the reserved strip.
$form.Location = New-Object System.Drawing.Point($abd.rc.left, $abd.rc.top)
$form.Size     = New-Object System.Drawing.Size(($abd.rc.right - $abd.rc.left), ($abd.rc.bottom - $abd.rc.top))

Write-Host "Work area DURING reserve: $(Get-WorkArea)" -ForegroundColor Green
Write-Host "Reserved strip rect: L=$($abd.rc.left) T=$($abd.rc.top) R=$($abd.rc.right) B=$($abd.rc.bottom)" -ForegroundColor Green

# Clean release on close, no matter how we exit.
$release = {
    $rm = New-Object AppBarInterop+APPBARDATA
    $rm.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($rm)
    $rm.hWnd   = $hwnd
    [void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_REMOVE, [ref]$rm)
    Write-Host "Work area AFTER release:  $(Get-WorkArea)" -ForegroundColor Cyan
}
$form.Add_FormClosed($release)

if ($Seconds -gt 0) {
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = $Seconds * 1000
    $timer.Add_Tick({ $timer.Stop(); $form.Close() })
    $timer.Start()
    Write-Host "Auto-releasing in $Seconds s..." -ForegroundColor Yellow
}

$form.Show()
[System.Windows.Forms.Application]::Run($form)
