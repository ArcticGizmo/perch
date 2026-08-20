<#
.SYNOPSIS
    Spike 2: test the *interactions* on a reserved edge column -
      1. drag the inner edge to expand/shrink the reserved width (work area follows live)
      2. collapse into a narrow strip and expand back
      3. drag the header onto another monitor / the other side to re-dock

    All of it is just re-issuing ABM_QUERYPOS/ABM_SETPOS with a new rect + uEdge on the
    same registered appbar - no re-register needed. This proves the reservation is fully
    mutable at runtime.

.PARAMETER AutoDemo
    Drive all four mutations programmatically (resize -> collapse -> flip side ->
    next monitor -> release) with short pauses, printing the work area at each step.
    Unattended proof that doesn't need hand-dragging.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge-Interactive.ps1
    # Interactive: drag the inner grip to resize, buttons to collapse/flip/move,
    # drag the dark header onto another monitor or side to re-dock.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Reserve-Edge-Interactive.ps1 -AutoDemo
#>
[CmdletBinding()]
param([switch]$AutoDemo)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class AppBarInterop {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA {
        public int cbSize; public IntPtr hWnd; public uint uCallbackMessage;
        public uint uEdge; public RECT rc; public IntPtr lParam;
    }
    public const uint ABM_NEW=0x0, ABM_REMOVE=0x1, ABM_QUERYPOS=0x2, ABM_SETPOS=0x3;
    public const uint ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3;
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint m, ref APPBARDATA d);
    [DllImport("user32.dll")]  public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]  public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    public const uint SPI_GETWORKAREA=0x0030;
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SystemParametersInfo(uint a, uint p, ref RECT v, uint w);
}
"@

# Per-monitor-v2 so multi-monitor bounds come back in true physical pixels (matters for
# the monitor-drag test). Fall back to system-aware on older shells.
try { [void][AppBarInterop]::SetProcessDpiAwarenessContext([IntPtr](-4)) }
catch { [void][AppBarInterop]::SetProcessDPIAware() }

# ── State ────────────────────────────────────────────────────────────────────
$script:edge      = 'Right'          # Left | Right (this spike does the vertical edges)
$script:mon       = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$script:thickWide = 320
$script:thickNarrow = 56
$script:thick     = $script:thickWide
$script:collapsed = $false

function Get-WorkArea {
    $r = New-Object AppBarInterop+RECT
    [void][AppBarInterop]::SystemParametersInfo([AppBarInterop]::SPI_GETWORKAREA, 0, [ref]$r, 0)
    "X=$($r.left) Y=$($r.top) W=$($r.right-$r.left) H=$($r.bottom-$r.top)"
}

# ── The reservation window ────────────────────────────────────────────────────
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle='None'; $form.TopMost=$true; $form.ShowInTaskbar=$false
$form.StartPosition='Manual'; $form.BackColor=[System.Drawing.Color]::FromArgb(30,34,42)

# Header - drag this to re-dock (monitor / side).
$header = New-Object System.Windows.Forms.Panel
$header.Dock='Top'; $header.Height=88; $header.BackColor=[System.Drawing.Color]::FromArgb(44,49,60)
$header.Cursor=[System.Windows.Forms.Cursors]::SizeAll
$hlabel = New-Object System.Windows.Forms.Label
$hlabel.Dock='Fill'; $hlabel.ForeColor='White'; $hlabel.TextAlign='MiddleCenter'
$hlabel.Font=New-Object System.Drawing.Font('Segoe UI',9)
$hlabel.Text="drag me to another`nmonitor / side"
$header.Controls.Add($hlabel)
$form.Controls.Add($header)

# Buttons - deterministic versions of the same mutations.
function New-Btn($text,$top){
    $b=New-Object System.Windows.Forms.Button
    $b.Text=$text; $b.SetBounds(8,$top,150,30); $b.FlatStyle='Flat'
    $b.ForeColor='White'; $b.BackColor=[System.Drawing.Color]::FromArgb(60,66,80)
    $form.Controls.Add($b); $b
}
$btnCollapse = New-Btn 'Collapse / Expand' 100
$btnFlip     = New-Btn 'Flip side'         138
$btnMonitor  = New-Btn 'Next monitor'      176

$status = New-Object System.Windows.Forms.Label
$status.SetBounds(8,220,300,120); $status.ForeColor='Gainsboro'
$status.Font=New-Object System.Drawing.Font('Consolas',8)
$form.Controls.Add($status)

# Inner-edge resize grip - drag to change width. Re-docked to the inner edge in Commit.
$grip = New-Object System.Windows.Forms.Panel
$grip.Width=6; $grip.BackColor=[System.Drawing.Color]::FromArgb(90,140,220)
$grip.Cursor=[System.Windows.Forms.Cursors]::SizeWE
$form.Controls.Add($grip)

$script:hwnd = $form.Handle
$abd = New-Object AppBarInterop+APPBARDATA
$abd.cbSize=[System.Runtime.InteropServices.Marshal]::SizeOf($abd)
$abd.hWnd=$script:hwnd
[void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_NEW,[ref]$abd)

# ── Commit: (re)reserve for the current edge/monitor/thickness. The whole point. ──
function Commit-Reservation {
    $edgeConst = if ($script:edge -eq 'Left') { [AppBarInterop]::ABE_LEFT } else { [AppBarInterop]::ABE_RIGHT }
    $a = New-Object AppBarInterop+APPBARDATA
    $a.cbSize=[System.Runtime.InteropServices.Marshal]::SizeOf($a)
    $a.hWnd=$script:hwnd; $a.uEdge=$edgeConst
    $b=$script:mon; $t=$script:thick
    $rc=New-Object AppBarInterop+RECT
    if ($script:edge -eq 'Right') { $rc.left=$b.Right-$t; $rc.right=$b.Right }
    else                          { $rc.left=$b.Left;     $rc.right=$b.Left+$t }
    $rc.top=$b.Top; $rc.bottom=$b.Bottom
    $a.rc=$rc
    [void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_QUERYPOS,[ref]$a)
    if ($script:edge -eq 'Right') { $a.rc.left=$a.rc.right-$t } else { $a.rc.right=$a.rc.left+$t }
    [void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_SETPOS,[ref]$a)

    $form.Location=New-Object System.Drawing.Point($a.rc.left,$a.rc.top)
    $form.Size=New-Object System.Drawing.Size(($a.rc.right-$a.rc.left),($a.rc.bottom-$a.rc.top))
    if ($script:edge -eq 'Right') { $grip.Dock='Left' } else { $grip.Dock='Right' }
    $grip.BringToFront()

    $wide = -not $script:collapsed
    $btnCollapse.Text = if ($script:collapsed) {'Expand'} else {'Collapse'}
    foreach($c in @($btnFlip,$btnMonitor,$status,$hlabel)){ $c.Visible=$wide }
    $status.Text = "edge   : $($script:edge)`nwidth  : $t px`nmonitor: $($b.Width)x$($b.Height) @ $($b.Left),$($b.Top)`n`nwork area:`n$(Get-WorkArea)"
    Write-Host ("[{0,-5}] width={1,3}  workarea={2}" -f $script:edge,$t,(Get-WorkArea)) -ForegroundColor Green
}

# ── Interaction 1: drag inner grip to resize ──────────────────────────────────
$script:resizing=$false
$grip.Add_MouseDown({ $script:resizing=$true; $grip.Capture=$true })
$grip.Add_MouseUp({ $script:resizing=$false; $grip.Capture=$false })
$grip.Add_MouseMove({
    if(-not $script:resizing){return}
    $x=[System.Windows.Forms.Cursor]::Position.X
    $b=$script:mon
    $t = if($script:edge -eq 'Right'){ $b.Right-$x } else { $x-$b.Left }
    $script:thick=[Math]::Max(48,[Math]::Min(700,$t))
    $script:collapsed=$false
    Commit-Reservation
})

# ── Interaction 2: collapse / expand ──────────────────────────────────────────
$btnCollapse.Add_Click({
    $script:collapsed = -not $script:collapsed
    $script:thick = if($script:collapsed){$script:thickNarrow}else{$script:thickWide}
    Commit-Reservation
})

# ── Interaction 3a: flip side (button) ────────────────────────────────────────
$btnFlip.Add_Click({
    $script:edge = if($script:edge -eq 'Right'){'Left'}else{'Right'}
    Commit-Reservation
})

# ── Interaction 3b: next monitor (button) ─────────────────────────────────────
$btnMonitor.Add_Click({
    $all=[System.Windows.Forms.Screen]::AllScreens
    $i=0; for($k=0;$k -lt $all.Count;$k++){ if($all[$k].Bounds.Equals($script:mon)){$i=$k;break} }
    $script:mon=$all[($i+1)%$all.Count].Bounds
    Commit-Reservation
})

# ── Interaction 3c: DRAG header onto a monitor/side to re-dock ─────────────────
$script:dragging=$false
$header.Add_MouseDown({ $script:dragging=$true; $header.Capture=$true })
$header.Add_MouseUp({
    if(-not $script:dragging){return}
    $script:dragging=$false; $header.Capture=$false
    $p=[System.Windows.Forms.Cursor]::Position
    $scr=[System.Windows.Forms.Screen]::FromPoint($p)
    $script:mon=$scr.Bounds
    $mid=$scr.Bounds.Left + [int]($scr.Bounds.Width/2)
    $script:edge = if($p.X -lt $mid){'Left'}else{'Right'}
    Commit-Reservation
})

$release = {
    $rm=New-Object AppBarInterop+APPBARDATA
    $rm.cbSize=[System.Runtime.InteropServices.Marshal]::SizeOf($rm); $rm.hWnd=$script:hwnd
    [void][AppBarInterop]::SHAppBarMessage([AppBarInterop]::ABM_REMOVE,[ref]$rm)
    Write-Host "released. work area = $(Get-WorkArea)" -ForegroundColor Cyan
}
$form.Add_FormClosed($release)

$form.Show()
Commit-Reservation

if($AutoDemo){
    $screens=[System.Windows.Forms.Screen]::AllScreens
    Write-Host "`n== AutoDemo ==" -ForegroundColor Yellow
    Write-Host "1) resize 320 -> 480 (drag-expand equivalent)" -ForegroundColor Yellow
    $script:thick=480; Commit-Reservation; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 900
    Write-Host "2) collapse -> 56 (narrow mode)" -ForegroundColor Yellow
    $script:collapsed=$true; $script:thick=$script:thickNarrow; Commit-Reservation; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 900
    Write-Host "3) expand -> 320" -ForegroundColor Yellow
    $script:collapsed=$false; $script:thick=$script:thickWide; Commit-Reservation; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 900
    Write-Host "4) flip side Right -> Left" -ForegroundColor Yellow
    $script:edge='Left'; Commit-Reservation; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 900
    if($screens.Count -gt 1){
        Write-Host "5) move to next monitor ($($screens.Count) detected)" -ForegroundColor Yellow
        $script:mon=$screens[1].Bounds; Commit-Reservation; [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 900
    } else {
        Write-Host "5) SKIP move-to-next-monitor: only one monitor detected" -ForegroundColor DarkYellow
    }
    Write-Host "6) release" -ForegroundColor Yellow
    $form.Close()
    return
}

[System.Windows.Forms.Application]::Run($form)
