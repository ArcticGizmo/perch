<#
.SYNOPSIS
    Installs Perch on Windows.

.DESCRIPTION
    Downloads the Velopack installer (Perch-win-Setup.exe) for a GitHub release, verifies it against the
    SHA256SUMS.txt published alongside it, and runs it. Everything after this - every subsequent update -
    is Velopack's job: right-click the tray icon -> "Check for Updates...".

    Designed to be run as a one-liner:

        irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex

    Nothing here tags a download with the mark-of-the-web (that's a browser's doing), so this route skips
    the "Windows protected your PC" SmartScreen dialog the same download through a browser walks into.

    KEEP THIS FILE PURE ASCII. It has no byte-order mark, so Windows PowerShell 5.1 decodes it as the
    system codepage (Windows-1252 here) rather than UTF-8. A UTF-8 em dash then arrives as three 1252
    characters ending in 0x94 = U+201D, a curly closing quote - and PowerShell honours curly quotes as
    string delimiters, so one em dash inside a normal "..." string silently terminates it early and the
    rest of the script mis-parses. tools/test-install.ps1 asserts the file stays ASCII.

.PARAMETER Version
    Install a specific version (e.g. 0.2.34) instead of the latest release. A leading "v" is fine.
    Also reads $env:PERCH_VERSION, which is how you pin through the piped one-liner:
        $env:PERCH_VERSION = '0.2.34'; irm .../install.ps1 | iex

.PARAMETER Repo
    owner/name of the GitHub repository to install from. Defaults to ArcticGizmo/perch; override for a
    fork. Also reads $env:PERCH_REPO.

.EXAMPLE
    irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1 | iex

.EXAMPLE
    # Pin a version, passing real parameters (needs the script as a scriptblock, not a pipe into iex):
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/ArcticGizmo/perch/main/install.ps1))) -Version 0.2.34
#>
#Requires -Version 5.1
param(
    [string] $Version = $env:PERCH_VERSION,
    [string] $Repo    = $(if ($env:PERCH_REPO) { $env:PERCH_REPO } else { 'ArcticGizmo/perch' })
)

# Failures are raised with `throw`, never `exit`: this script is normally executed by `iex` inside the
# user's own shell, and `exit` there would close their session. A terminating error prints the problem and
# still surfaces as a non-zero exit code when the one-liner runs under `powershell -Command`.
function Install-Perch {
    [CmdletBinding()]
    param([string] $Version, [string] $Repo)

    $ErrorActionPreference = 'Stop'

    $SetupAsset = 'Perch-win-Setup.exe'
    $SumsAsset  = 'SHA256SUMS.txt'

    # --- Preflight -------------------------------------------------------------------------------------
    if ([System.Environment]::OSVersion.Platform -ne 'Win32NT') {
        throw "Perch's Windows build only installs on Windows. On macOS, see $(Get-RepoUrl $Repo)#macos-apple-silicon-unsigned."
    }
    if (-not [System.Environment]::Is64BitOperatingSystem) {
        throw 'Perch ships as 64-bit only; this looks like a 32-bit Windows.'
    }
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
        Write-Warning 'ARM64 Windows detected. Perch is x64-only, so it will run under emulation.'
    }
    # Older Windows PowerShell hosts can still default to TLS 1.0, which api.github.com refuses outright.
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    # --- Resolve the release ---------------------------------------------------------------------------
    $release = Get-PerchRelease -Repo $Repo -Version $Version
    $tag     = $release.tag_name
    Write-Host "Installing Perch $tag" -ForegroundColor Cyan

    # Both assets are looked up in the release metadata rather than by guessing a URL, so a release that
    # is missing one fails here with a clear reason instead of downloading a 404 page.
    $setupUrl = Get-AssetUrl -Release $release -Name $SetupAsset
    $sumsUrl  = Get-AssetUrl -Release $release -Name $SumsAsset

    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("perch-install-" + [guid]::NewGuid().ToString('N').Substring(0, 12))
    New-Item -ItemType Directory -Path $work | Out-Null
    $keepWork = $false   # set if we bail out while the installer might still be reading from $work
    try {
        # --- Download ----------------------------------------------------------------------------------
        # GitHub serves release assets as application/octet-stream, and PowerShell 7 hands back a byte[]
        # rather than a string for content it doesn't consider text (5.1 always decodes to a string). Decode
        # explicitly so the manifest parses identically on both hosts.
        $raw   = (Invoke-WebRequest -Uri $sumsUrl -UseBasicParsing).Content
        $sums  = if ($raw -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($raw) } else { [string]$raw }
        $want  = Get-ExpectedHash -Sums $sums -Name $SetupAsset -Tag $tag

        $setup = Join-Path $work $SetupAsset
        Save-File -Uri $setupUrl -OutFile $setup -Label $SetupAsset

        # --- Verify ------------------------------------------------------------------------------------
        # A mismatch means the bytes on disk are not the bytes that were released: a truncated or corrupted
        # transfer, a proxy that rewrote the payload, or tampering. Never install either way.
        $got = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        if ($got -ne $want) {
            Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
            throw @"
Checksum mismatch for $SetupAsset - refusing to install.
  expected  $want
  actual    $got
The download has been deleted. Retry; if it keeps failing, report it at $(Get-RepoUrl $Repo)/issues.
"@
        }
        Write-Host "  SHA-256 verified  $($want.ToLowerInvariant())" -ForegroundColor DarkGray

        # --- Install -----------------------------------------------------------------------------------
        # Velopack's installer needs no admin rights: it installs to %LocalAppData%\Perch, registers the
        # uninstaller and Start Menu shortcut, and - the part that matters here - launches the tray app
        # before exiting.
        #
        # So do NOT use `Start-Process -Wait`: that waits for the started process *and all its descendants*,
        # which includes the tray app we just installed. Since Perch is meant to keep running, the wait never
        # returns and the one-liner hangs forever. Waiting on the Setup process's own handle instead returns
        # as soon as the install itself is finished.
        Write-Host 'Running the installer...'
        $psi = [System.Diagnostics.ProcessStartInfo]::new($setup)
        $psi.UseShellExecute = $true   # explicit: the default differs between Windows PowerShell and 7.x
        $proc = [System.Diagnostics.Process]::Start($psi)
        if (-not $proc) { throw "Could not start $SetupAsset." }

        # Bounded so a wedged installer (a UAC or antivirus prompt stuck behind another window) reports
        # something instead of hanging the shell indefinitely.
        if (-not $proc.WaitForExit(10 * 60 * 1000)) {
            $keepWork = $true
            Write-Warning "The installer is still running after 10 minutes - leaving it to finish on its own. Delete $work once Perch has installed."
            return
        }
        if ($proc.ExitCode -ne 0) {
            throw "The Perch installer exited with code $($proc.ExitCode)."
        }
    }
    finally {
        if (-not $keepWork) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }

    Write-Host ''
    Write-Host "Perch $tag is installed and starting in the system tray." -ForegroundColor Green
    Write-Host '  Left-click the tray icon for Settings, right-click for the full menu.' -ForegroundColor DarkGray
    Write-Host '  Updates from here on are in-app: right-click the tray icon -> Check for Updates...' -ForegroundColor DarkGray
}

function Get-RepoUrl { param([string] $Repo) "https://github.com/$Repo" }

# The GitHub release metadata for $Version, or the latest release when it's blank. Uses $env:GITHUB_TOKEN
# when present - only useful behind a shared IP that has burned through the 60 requests/hour anonymous
# limit. The token is deliberately NOT sent when downloading assets (see Save-File).
function Get-PerchRelease {
    param([string] $Repo, [string] $Version)

    $headers = @{ 'Accept' = 'application/vnd.github+json'; 'User-Agent' = 'perch-install.ps1' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $uri  = "https://api.github.com/repos/$Repo/releases/latest"
        $what = 'the latest release'
    }
    else {
        $tag  = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
        $uri  = "https://api.github.com/repos/$Repo/releases/tags/$tag"
        $what = "release $tag"
    }

    try {
        return Invoke-RestMethod -Uri $uri -Headers $headers -UseBasicParsing
    }
    catch {
        throw "Could not look up $what of $Repo. $($_.Exception.Message)"
    }
}

function Get-AssetUrl {
    param($Release, [string] $Name)

    $asset = $Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $asset) {
        throw @"
Release $($Release.tag_name) has no $Name asset, so this installer can't verify or install it.
Releases from before checksums were published predate this script - install $($Release.tag_name) by hand
from $($Release.html_url), or re-run without -Version to take the latest release.
"@
    }
    return $asset.browser_download_url
}

# Pulls the one line for $Name out of a sha256sum-format manifest ("<64 hex>  <filename>"). A manifest
# that lists other files but not this one is a build error, and treating it as "nothing to check" would
# quietly defeat the whole point - so it throws.
function Get-ExpectedHash {
    param([string] $Sums, [string] $Name, [string] $Tag)

    foreach ($line in $Sums -split "`r?`n") {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $Name) {
            return $Matches[1].ToUpperInvariant()   # Get-FileHash returns upper-case hex
        }
    }
    throw "SHA256SUMS.txt for $Tag has no entry for $Name, so the download can't be verified."
}

# Streams a URL to disk with a progress bar. Invoke-WebRequest would do, but its own progress rendering
# makes a ~90 MB download crawl on Windows PowerShell 5.1, and -OutFile there buffers the whole response
# in memory first. No Authorization header: asset URLs redirect to a pre-signed objects.githubusercontent
# .com URL that rejects requests carrying one.
function Save-File {
    param([string] $Uri, [string] $OutFile, [string] $Label)

    $req = [System.Net.WebRequest]::CreateHttp($Uri)
    $req.UserAgent = 'perch-install.ps1'
    $req.Timeout = 60000
    # Per read, not for the whole transfer - so this bounds a *stalled* connection, not a slow one. Keep it
    # short enough that a dead mirror fails in a minute instead of hanging the one-liner for several.
    $req.ReadWriteTimeout = 60000

    $resp = $req.GetResponse()
    try {
        $total = $resp.ContentLength
        $shown = -1
        $in  = $resp.GetResponseStream()
        $out = [System.IO.File]::Create($OutFile)
        try {
            $buffer = [byte[]]::new(131072)
            $read = 0
            while (($n = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $out.Write($buffer, 0, $n)
                $read += $n
                if ($total -gt 0) {
                    # Throttled to whole-percent changes: Write-Progress is expensive enough on 5.1 to
                    # matter if it's called once per 128 KB chunk.
                    $pct = [int](100 * $read / $total)
                    if ($pct -ne $shown) {
                        $shown = $pct
                        Write-Progress -Activity "Downloading $Label" `
                            -Status ("{0:N1} of {1:N1} MB" -f ($read / 1MB), ($total / 1MB)) -PercentComplete $pct
                    }
                }
            }
        }
        finally {
            $out.Dispose()
            $in.Dispose()
            Write-Progress -Activity "Downloading $Label" -Completed
        }
        if ($total -gt 0 -and (Get-Item -LiteralPath $OutFile).Length -ne $total) {
            throw "$Label downloaded incompletely ($((Get-Item -LiteralPath $OutFile).Length) of $total bytes)."
        }
    }
    finally {
        $resp.Dispose()
    }
    Write-Host ("  downloaded {0} ({1:N1} MB)" -f $Label, ((Get-Item -LiteralPath $OutFile).Length / 1MB))
}

Install-Perch -Version $Version -Repo $Repo
