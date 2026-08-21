# Perch sample plugin: System Info.
# Paints current CPU load and free memory as an overlay glyph. Reads only local machine metrics via CIM -
# no network, no files, no session data - so its consent prompt says it requests no special access.
#
# ASCII-only source; JSON built via ConvertTo-Json.

$ErrorActionPreference = 'SilentlyContinue'

# Drain the request line (no context needed).
[Console]::In.ReadLine() | Out-Null

$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
if ($null -eq $cpu) { $cpu = 0 }
$cpu = [int]$cpu

$os = Get-CimInstance Win32_OperatingSystem
$freeGb = if ($os) { [math]::Round($os.FreePhysicalMemory / 1MB, 1) } else { 0 }

$obj = @{
    type  = 'render'
    glyph = @{ glyph = ''; text = "CPU $cpu%"; tooltip = "CPU $cpu%  -  $freeGb GB free" }
}
[Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress))
exit 0
