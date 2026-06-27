# Launcher for the perch plugin hooks. Almost everything is done here in PowerShell by writing
# the session sidecar files the running tray app observes (it watches ~/.claude/sessions/). The one
# exception is the `start` action (SessionStart), which may launch the installed tray when the user
# has opted into auto-start — see below.
#
# Sidecars, all keyed by session id:
#   {sid}.mode    — permission mode (every hook). Tool-call data is never read beyond session_id +
#                   permission_mode, and is never logged or persisted.
#   {sid}.notify  — external-notification opt-in marker (toggled by /afk; its mere presence = on).
#                   The same marker the tray's right-click "external notifications" toggle writes, so
#                   the two share one source of truth.
#   {sid}.history — one-shot trigger the tray consumes to open its history viewer (/history).
#
# `mode` events only touch {sid}.mode and return immediately, keeping the per-tool-call hot path
# cheap. Everything no-ops gracefully when the sessions dir is missing.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$HandleArgs)
$ErrorActionPreference = 'SilentlyContinue'

$action = if ($HandleArgs.Count -ge 1) { $HandleArgs[0] } else { '' }
$dir = Join-Path $env:USERPROFILE '.claude\sessions'

# Read this hook's own stdin as UTF-8, independent of the console input encoding.
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$payload = $reader.ReadToEnd()
$reader.Dispose()

try { $j = $payload | ConvertFrom-Json } catch { exit 0 }
$sid = $j.session_id

# Permission mode: read just the two fields we need and write the sidecar. Nothing else from the
# payload is touched, so tool-call inputs/outputs are never recorded.
if ($sid -and $j.permission_mode -and (Test-Path $dir)) {
  Set-Content -Path (Join-Path $dir "$sid.mode") -Value $j.permission_mode -NoNewline -Encoding ASCII
}

# Mode-only events (PreToolUse / PostToolUse / Stop) are done.
if ($action -eq 'mode') { exit 0 }

# SessionStart: if the user opted into auto-start, launch the installed tray when one isn't already
# running. The tray usually isn't up when a session opens, so the on/off state lives in its
# settings.json (which we read here) rather than in the running app. Only "startup"/"resume" sources
# represent a session actually opening; "clear"/"compact" happen mid-session, when the tray is
# already up. Relies on `perch` being on PATH (the installer registers it) — for a dev build
# run via `dotnet run` it simply won't resolve and this no-ops.
if ($action -eq 'start') {
  $source = $j.source
  if ($source -and $source -ne 'startup' -and $source -ne 'resume') { exit 0 }

  $settingsPath = Join-Path $env:APPDATA 'Perch\settings.json'
  if (-not (Test-Path $settingsPath)) { exit 0 }
  try { $cfg = Get-Content -Raw -Path $settingsPath | ConvertFrom-Json } catch { exit 0 }
  if (-not $cfg.AutoStartOnFirstSession) { exit 0 }

  # Already running on this desktop? The tray's single-instance guard would no-op a second launch
  # anyway, so skip it — this is also our "no other session has it open yet" check.
  if (Get-Process perch -ErrorAction SilentlyContinue) { exit 0 }

  # --autostarted tells the tray it was launched by this hook, so it knows it's allowed to auto-close
  # itself after the last session ends (a manually-opened tray never self-closes).
  Start-Process 'perch' -ArgumentList '--autostarted' -ErrorAction SilentlyContinue
  exit 0
}

# SessionEnd: remove this session's sidecars.
if ($action -eq 'cleanup') {
  if ($sid) {
    Remove-Item (Join-Path $dir "$sid.mode")    -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.notify")  -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.history") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.afk")     -Force -ErrorAction SilentlyContinue  # legacy marker
  }
  exit 0
}

# prompt: recognise /afk and /history (optionally namespaced, e.g. /perch:afk), start-anchored
# so a prompt that merely mentions one in prose is left alone. Anything else passes through to Claude.
if ($action -eq 'prompt') {
  $prompt = $j.prompt
  if (-not $sid -or -not $prompt) { exit 0 }

  $m = [regex]::Match($prompt.Trim(), '^/(?:[\w-]+:)?(?<cmd>afk|history)\b',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
  if (-not $m.Success) { exit 0 }
  $cmd = $m.Groups['cmd'].Value.ToLowerInvariant()

  # External notifications only fire while the tray is running; report that rather than silently
  # writing a marker that nothing will act on.
  $running = [bool](Get-Process perch -ErrorAction SilentlyContinue)

  if ($cmd -eq 'afk') {
    if (-not $running) {
      $reason = "Perch isn't running, so external notifications can't be toggled right now."
    } else {
      $marker = Join-Path $dir "$sid.notify"
      if (Test-Path $marker) {
        Remove-Item $marker -Force -ErrorAction SilentlyContinue
        $reason = "External (ntfy) notifications are now OFF for this session."
      } else {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Set-Content -Path $marker -Value $sid -NoNewline -Encoding ASCII
        $reason = "External (ntfy) notifications are now ON for this session " +
                  "(requires external notifications enabled in Perch settings)."
      }
    }
  } else {
    if (-not $running) {
      $reason = "Perch isn't running, so the history panel can't be opened."
    } else {
      if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
      Set-Content -Path (Join-Path $dir "$sid.history") -Value $sid -NoNewline -Encoding ASCII
      $reason = "Opening the Perch history panel for this session."
    }
  }

  # A "block" decision erases the prompt before the model sees it; the reason is shown to the user.
  $out = @{ decision = 'block'; reason = $reason } | ConvertTo-Json -Compress
  [Console]::Out.Write($out)
  exit 0
}

exit 0
