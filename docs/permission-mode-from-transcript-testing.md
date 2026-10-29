# Permission-mode badge from the transcript — manual testing

The overlay's permission-mode badge (plan / accept edits / auto / bypass) used to come from a `{sid}.mode` sidecar that `perch-hook mode` wrote on every PreToolUse, PostToolUse and Stop. It now reads the `permission-mode` records Claude Code writes into the transcript itself, and the three per-tool-call hook registrations are gone. A transcript with no recorded mode (older Claude Code) shows no badge.

## Setup

1. Build and run the tray: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`.
2. Let it reconcile hooks on launch, then open `~/.claude/settings.json`.

## Checks

| # | Do | Before | After |
|---|---|---|---|
| 1 | Look at the `hooks` block in `~/.claude/settings.json` | `perch-hook` entries with `"args": ["mode"]` under PreToolUse, PostToolUse and Stop | No `mode` entries; PreToolUse/PostToolUse/Stop are gone unless you have your own hooks there (which survive). SubagentStop, TeammateIdle, SessionStart and SessionEnd remain |
| 2 | Start `claude` in a terminal, prompt "say hi" | Row shows no badge (default mode) | Same |
| 3 | Press Shift+Tab to plan mode, then prompt "list the files here" | Badge flipped at the first tool call | Badge shows plan once the turn's records land (at the latest by the 30s reconcile scan if idle) |
| 4 | Shift+Tab to accept edits, prompt "create a file hello.txt containing hi" | Accept-edits badge | Same |
| 5 | Start `claude --permission-mode bypassPermissions`, prompt "say hi" | Bypass badge | Same |
| 6 | Ask for a sub-agent: "use a sub-agent to list the files here" while in plan mode | Parent badge stays plan | Same — sub-agent records don't override the parent's mode |
| 7 | `dir ~/.claude/sessions/*.mode` after a new session starts | A fresh `{sid}.mode` appears | No new `.mode` file is written (old ones are swept on SessionEnd) |
| 8 | Create a work item (`perch work new "Test"`), then in PowerShell `$env:PERCH_WORK = "<id>"; claude` | Session joins the item ("Joined from a terminal") | Session does not join; `perch work new` and the empty-item hint now suggest `perch --work <id>` only |
| 9 | `perch --work <id>` | Session opens in the item | Same |
| 10 | Check `settings.json` hooks again | A `SubagentStop` entry running `perch-hook agentstop` | No `SubagentStop` entry (unless it's your own); TeammateIdle, SessionStart, SessionEnd remain |
| 11 | Prompt "use two sub-agents in parallel to summarise README.md and CHANGELOG.md" | Sub-agent rows/chips appear, then vanish as each finishes | Same; `subagents/agent-*.meta.json` gains `"finished":true` and no new `.stopped` file appears |
| 12 | Count `~/.claude/projects/<this project>/*/subagents/agent-*.stopped`, then start and `/exit` a `claude` in that project | Count unchanged (only the ended session's own markers were removed) | Zero; `.idle` markers survive only beside an agent transcript that still exists |
| 13 | Prompt "use a sub-agent to run `ping -n 60 127.0.0.1`", then press Esc to stop it | Row lingered until the staleness window (~90s+) or the hook fired | Row drops once the meta gains `"stoppedByUser":true` |
