# IDE-host detection & origin glyph

Marks each session with the editor/IDE hosting it — VS Code, Cursor, Windsurf, a JetBrains IDE, … —
alongside the existing Claude Desktop (monitor) and background/SDK (bot) origin marks.

## Why it needs process ancestry

Nothing in a session's on-disk state distinguishes an IDE-hosted session. Claude Code records
`entrypoint: "cli"` for a bare terminal **and** for an IDE's integrated terminal, and the session JSON
carries no IDE field. (The IDE integration *does* write `~/.claude/ide/{port}.lock` files with an `ideName`
and `workspaceFolders`, but those can only be correlated to a session by matching the workspace folder to
the session `cwd` — which false-positives when a plain terminal shares a repo with an open IDE window, and
is empty for folderless windows.)

The reliable, per-session signal is **process ancestry**: the session's `claude` process is a descendant of
the IDE process. Observed chains:

```
VS Code:  claude.exe <- powershell.exe <- Code.exe <- Code.exe <- explorer.exe
Terminal: claude.exe <- cmd.exe <- WindowsTerminal.exe <- explorer.exe
```

An ancestor named `Code.exe` / `Cursor.exe` / `Windsurf.exe` / `idea64.exe` / … identifies the host (and,
for JetBrains, the specific product) directly.

## Design

Mirrors the existing `IProcessProbe` seam.

- **`Perch.Core/Data/IdeHost.cs`** — `IdeHostKind` enum + `IdeHost` record. `IdeHost.FromExecutable(name)` is
  the pure, shared executable→host table (VS Code family, Cursor, Windsurf, Zed, Visual Studio, the JetBrains
  launchers by prefix). Unit-tested in `IdeHostTests`.
- **`Perch.Core/Platform/IIdeHostDetector.cs`** — `Detect(int pid) -> IdeHost?`. `NullIdeHostDetector` is the
  default (no glyph) for the mac head, replay and tests.
- **`Perch.Platform.Windows/IdeHostDetector.cs`** — walks the pid's ancestry over a Toolhelp process snapshot
  (cached ~1.5 s so a whole scan is one enumeration), returns the first ancestor `FromExecutable` recognises.
  Best-effort, never throws, cycle-guarded.
- **`Perch.Platform.Mac/IdeHostDetector.cs`** — stub returning null (a real one would use
  `proc_listpids`/`sysctl`; see macos-port-plan).
- Wired `PlatformServices.IdeHostDetector` → `SessionMonitorHost` → `SessionMonitor`, which stamps
  `ClaudeSession.IdeHost` (+ `IsIde`) at build time.

## Rendering — the host icon *replaces the leftmost status dot*

Rather than an extra glyph, a hosted session's **leftmost status dot is replaced by the host app's own icon**
(in colour): recognisable at a glance, no extra width, and a bit of fun. Plain-terminal and background/SDK
sessions keep the coloured status dot (background also keeps its bot glyph in the name cluster). Status stays
legible in the row's right-hand status text; the dot's status *colour* is the deliberate trade for host rows.

**Primary: the real host-app icon, extracted from its executable, in colour.** For an **IDE** the detector
resolves the ancestor's full image path (`QueryFullProcessImageName`) into `IdeHost.Executable`, and the app
renders that exe's icon through the **same `IAppIconProvider` the quick-links strip uses**
(`GetIconFile("", null, exePath, 32)` — the empty name skips the slow Start-Menu enumeration and renders
straight off the binary). For a **Claude Desktop** session (known from the `entrypoint`, not ancestry — its
process is the CLI `claude.exe`, indistinguishable by name from any other) the app resolves the Claude Desktop
logo by Start-Menu name (`GetIconFile("Claude", null, null, 32)` — it's a Store/MSIX app whose real logo only
comes back via the AppsFolder).

Resolution runs off the UI thread, deduped by cache key — exe path for IDEs, `OverlayCanvas.DesktopOriginKey`
for Claude Desktop (`App.RefreshOriginIcons` + `_requestedIdeIcons`). The PNG is decoded (`DecodeIcon`) and
cached on the canvas (`SetOriginIcon` / `_originIcons`), then drawn by `DrawOriginBitmap` (aspect-fit into a
13px box in the dot slot). Shell-extracted icons come back **vertically flipped**, so the draw mirrors about
the horizontal axis to right them — the same correction the quick-links strip applies.

**Fallback: owner-drawn marks in the dot slot** — the monitor glyph for Claude Desktop, the brand marks below
for IDEs — shown until the real icon lands and whenever it can't be resolved (mac, a bare base name, an
inaccessible process, or a Start-Menu name that doesn't match):

| Host      | Vector mark                        | Colour |
|-----------|------------------------------------|--------|
| VS Code   | folded ribbon (spine + two tips)   | `FixedColors.VsCode` (azure) |
| Cursor    | isometric cube                     | neutral origin gray |
| Windsurf  | sail on a mast + board             | `FixedColors.Windsurf` (teal-600) |
| JetBrains | rounded square w/ corner notch     | `FixedColors.JetBrains` (magenta) |
| other     | `</>`                              | neutral origin gray |

Brand hues live in `FixedColors` (fixed identities, like `Jira`) with `Palette` accessors/brushes and CVD
`Simulate` coverage; they clear the WCAG 3:1 non-text floor on every preset (gated by `PresetContrastTests`).
A dwell tooltip on the dot names the host ("Visual Studio Code", "PyCharm", …). `HeadlessRenderer.ResolveIdeIcons`
wires the same resolution into `render` mode so it's a faithful preview.

No user setting — always on. Detection cost is one cached process snapshot per scan on Windows (plus a one-off
icon render per distinct host); nothing on other heads.

## Not done / possible follow-ups

- **macOS** detector (currently a stub).
- **Focus/activation**: an IDE-hosted session's terminal is a child of the IDE; check `WindowActivator`
  raises the right window (the console-attach path may already cover it).
- Could enrich the label from the `~/.claude/ide/*.lock` `ideName` if a host's exe isn't in the table.
