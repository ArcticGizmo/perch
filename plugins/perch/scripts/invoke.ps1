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
#
# Per-agent turn markers, dropped beside an agent's transcript in
# {projects}/{enc-cwd}/{session}/subagents/ (not in the sessions dir above):
#   agent-{id}.stopped — SubagentStop: a sub-agent finished, or a teammate ended a turn.
#   agent-{id}.idle    — TeammateIdle: a teammate went idle waiting for the lead.
# The tray reads the marker's mtime to retire the row immediately, rather than waiting out its
# staleness window. Contents are just a timestamp; no tool-call data is recorded. SessionEnd sweeps
# them. A later transcript write (a re-tasked teammate) ages the marker out, so the row self-heals.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$HandleArgs)
$ErrorActionPreference = 'SilentlyContinue'

$action = if ($HandleArgs.Count -ge 1) { $HandleArgs[0] } else { '' }
$dir = Join-Path $env:USERPROFILE '.claude\sessions'

# A session's per-agent transcripts (and the markers we drop beside them) live at
# {projects}/{enc-cwd}/{session}/subagents/. The parent transcript_path the hooks receive is
# {projects}/{enc-cwd}/{session}.jsonl, so the subagents dir is that path minus its extension plus
# /subagents — exactly the directory SubAgentReader scans. Returns $null if we can't derive it.
# NB: avoid [Path]::ChangeExtension($p, $null) here — Windows PowerShell coerces $null to "" and leaves
# a trailing dot, which corrupts marker filenames. Strip the extension with GetFileNameWithoutExtension.
function Get-SubagentsDir([string]$transcriptPath) {
  if (-not $transcriptPath) { return $null }
  $d = [System.IO.Path]::GetDirectoryName($transcriptPath)
  $n = [System.IO.Path]::GetFileNameWithoutExtension($transcriptPath)  # {session}
  if (-not $n) { return $null }
  return Join-Path (Join-Path $d $n) 'subagents'
}

# Read this hook's own stdin as UTF-8, independent of the console input encoding.
$reader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$payload = $reader.ReadToEnd()
$reader.Dispose()

try { $j = $payload | ConvertFrom-Json } catch { exit 0 }
$sid = $j.session_id

# SubagentStop: a sub-agent finished, or a teammate ended a turn. Drop a marker beside that agent's
# transcript so the tray retires the row at once instead of waiting out its staleness window. We use
# the marker's mtime as the event time (the reader compares it to the transcript's), so the body is
# just a timestamp — no tool-call data is read or recorded. Handled before the .mode write below so a
# sub-agent's permission_mode never overwrites the parent session's mode sidecar.
if ($action -eq 'agentstop') {
  $atp = $j.agent_transcript_path
  if (-not $atp) {
    # Older builds may omit it: rebuild …/subagents/agent-{id}.jsonl from the parent path + agent_id.
    $subdir = Get-SubagentsDir $j.transcript_path
    if ($subdir -and $j.agent_id) { $atp = Join-Path $subdir ("agent-" + $j.agent_id + ".jsonl") }
  }
  if ($atp) {
    $d = [System.IO.Path]::GetDirectoryName($atp)
    $n = [System.IO.Path]::GetFileNameWithoutExtension($atp)  # agent-{id}
    if ($n) {
      Set-Content -Path (Join-Path $d "$n.stopped") -Value ((Get-Date).ToUniversalTime().ToString('o')) -NoNewline -Encoding ASCII
    }
  }
  exit 0
}

# TeammateIdle: a teammate went idle waiting for the lead. It carries teammate_name (== the agent's
# type) but no agent_id, so resolve the transcript by matching the meta sidecars in the subagents dir,
# then drop an .idle marker beside it. Belt-and-braces alongside agentstop (which usually fires first).
if ($action -eq 'teammateidle') {
  $name = $j.teammate_name
  $subdir = Get-SubagentsDir $j.transcript_path
  if ($name -and $subdir -and (Test-Path $subdir)) {
    foreach ($meta in Get-ChildItem -Path $subdir -Filter 'agent-*.meta.json' -ErrorAction SilentlyContinue) {
      try {
        $m = Get-Content -Raw -Path $meta.FullName | ConvertFrom-Json
        if ($m.agentType -eq $name -or $m.name -eq $name) {
          $base = $meta.FullName.Substring(0, $meta.FullName.Length - '.meta.json'.Length)
          Set-Content -Path "$base.idle" -Value ((Get-Date).ToUniversalTime().ToString('o')) -NoNewline -Encoding ASCII
        }
      } catch { }
    }
  }
  exit 0
}

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

# SessionEnd: remove this session's sidecars, and sweep any agent stop/idle markers it left behind
# (a hard kill can end the session before an in-flight sub-agent ever fires SubagentStop).
if ($action -eq 'cleanup') {
  if ($sid) {
    Remove-Item (Join-Path $dir "$sid.mode")    -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.notify")  -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.history") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $dir "$sid.afk")     -Force -ErrorAction SilentlyContinue  # legacy marker
  }
  $subdir = Get-SubagentsDir $j.transcript_path
  if ($subdir -and (Test-Path $subdir)) {
    Remove-Item (Join-Path $subdir 'agent-*.stopped') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $subdir 'agent-*.idle')    -Force -ErrorAction SilentlyContinue
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
