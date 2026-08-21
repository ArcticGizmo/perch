# Perch sample plugins

Runnable example plugins that exercise the Perch extension model described in
[`docs/pluggability-plan.md`](../../docs/pluggability-plan.md). Each is an out-of-process plugin:
a `perch-plugin.json` manifest plus an entry script Perch launches, exchanging newline-delimited
JSON over stdio. These are the local-folder trial vehicles for the milestones; the eventual
distribution story is one GitHub repo per plugin (topic `perch-plugin`, a tagged release + a
`SHA256SUMS.txt`).

## The protocol in one breath

Perch writes **one request line** to the plugin's stdin and reads response lines from its stdout:

```
→  {"type":"poll","perch":"0.9.0","grants":["read.cwd"],"context":{"cwd":"C:\\proj"}}
←  {"type":"render","glyph":{"glyph":"","text":"3","tooltip":"3 uncommitted changes"}}
```

- `context` only carries what the plugin's **granted capabilities** allow (no `read.cwd` grant →
  no `cwd` key).
- Always emit **valid JSON** — build it with your language's JSON serialiser
  (`ConvertTo-Json` in PowerShell), never string concatenation, or a Windows path's backslashes
  will produce invalid escapes and the line is silently dropped.
- A `notify` message is only acted on if the manifest declared (and the user granted) the
  `notify` capability; a `render` only if the manifest lists the `overlay.glyph` extension point.

## Trying one locally (M1)

Copy a plugin's folder into Perch's plugin directory and restart Perch:

```
~/.claude/perch/plugins/<plugin-folder>/
```

(`~/.claude` follows `CLAUDE_CONFIG_DIR` if you've set it.) In M2 this manual copy is replaced by
**Add from GitHub…**, which downloads a release, verifies its SHA-256, and shows a consent dialog
before enabling.

## Samples

| Folder | Extension points | Capabilities | Milestone |
|---|---|---|---|
| [`git-dirty`](git-dirty/) | `poll`, `overlay.glyph` | `read.cwd` | M1 — polls the active session's repo, badges the uncommitted-change count |

More arrive with later milestones (a Pomodoro `command` badge, an event-driven webhook notifier —
see the trial-plugin table in the plan) as the extension points they need land.
