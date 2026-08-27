# Correlating a Claude session to its terminal window (Windows)

How Perch decides *which* window to raise when you click a session row, why the
process-ancestry approach silently breaks for some Windows Terminal launches, and the
authoritative fix that needs **no** hook and **no** persisted state.

## The problem

`WindowActivator.FocusTerminalForProcess(pid)` finds a session's terminal by walking the
**process ancestry** of `claude.exe` and looking for a window one of those ancestors owns —
directly (an IDE's own top-level window) or, under ConPTY, via `GA_ROOTOWNER` of the 0x0
`PseudoConsoleWindow` the shell owns, whose owner is the hosting terminal window.

That assumption — *the terminal is a process ancestor of the shell* — holds for most launches
(a tab started inside Windows Terminal, an IDE integrated terminal, mintty/WezTerm/Alacritty),
but **not** for the Windows Terminal default-terminal (DefTerm) handoff.

### The DefTerm COM-handoff case

On Win11 with "Default terminal application" set to Windows Terminal (or "Let Windows
decide"), launching a bare console app (powershell.exe / cmd.exe) from *outside* WT — Start
menu, taskbar, Win+R, a `.lnk`, "Open in Terminal" — can be adopted by WT one of two ways,
chosen non-deterministically at console-allocation time:

1. **In-proc conhost bridge** — the shell itself owns the `PseudoConsoleWindow`. The shell is
   a depth-1 ancestor of claude, so traversal finds it. **Focus works.**
2. **COM-activated `OpenConsole.exe -Embedding`** — a broker under `svchost.exe`
   (`DcomLaunch`) spawns the ConPTY host, and *it* owns the `PseudoConsoleWindow`. That process
   is in a completely separate subtree from the shell, so nothing in claude's ancestry owns a
   window that resolves to the terminal. **Traversal finds nothing** — and before the
   explorer-exclusion fix it wrongly focused the desktop (`explorer.exe`, an ancestor of
   everything), a dead click.

This is why the *same* PowerShell shortcut "sometimes works": it is a coin-flip between the two
paths. Verified live: three identical launches, two took path 1 and focused, one took path 2
and did not.

```
explorer.exe (common ancestor of both subtrees; never a valid host -> excluded)
|
+- powershell.exe (shell)
|  +- conhost.exe        windowless ConPTY stub
|  |     ....ConPTY pipe.... (no Win32 parent/child edge across this)
|  +- claude.exe         <== THE SESSION (the pid in {pid}.json)
|
+- WindowsTerminal.exe
   +- [window]  <== THE ACTUAL TERMINAL (what a click must raise)
         ^ GA_ROOTOWNER
         |
svchost.exe (DcomLaunch)
   +- OpenConsole.exe "-Embedding"
        +- creates PseudoConsoleWindow ....(owned by the terminal window)
```

The severed edge is the ConPTY pipe between the shell's conhost stub and the COM `OpenConsole`.
Process ancestry cannot cross it, so no amount of parent-walking will make the link.

## The fix: read the link from the console object, not the process tree

The kernel's console object *is* the authoritative association between a session and its
terminal, and you can read it from outside the session with `AttachConsole`:

```
FreeConsole();                       // detach our own console first (required)
AttachConsole(shellOrClaudePid);     // attach to the target session's console
hwnd = GetConsoleWindow();           // -> that session's PseudoConsoleWindow (0x0)
terminal = GetAncestor(hwnd, GA_ROOTOWNER);   // -> the real terminal top-level window
FreeConsole();
```

`GetConsoleWindow()` after the attach returns *that session's* pseudo-console window regardless
of which handoff path allocated it, because it comes from the console the process is actually
bound to — not from where the host process happens to sit in the tree. `GA_ROOTOWNER` then
climbs to the terminal window exactly as the traversal path already does.

Verified against the failing session (claude pid 25544, shell 27448):

```
AttachConsole(25544) -> consoleWnd=3146146 (PseudoConsoleWindow) -> termWin=920408 (WindowsTerminal.exe) "Claude Code"
AttachConsole(27448) -> consoleWnd=3146146                        -> termWin=920408 (WindowsTerminal.exe) "Claude Code"
```

Both the claude process and its shell resolve to the same pseudo-console and the same terminal
window — the correlation traversal could not make.

> Note: this is also how you *verify* a correlation from the outside. An earlier attempt to
> identify the window by "elimination" (the one leftover `PseudoConsoleWindow` not owned by a
> known shell) happened to be right but was not proof; `AttachConsole` is the proof.

## Why this beats the hook approach

An earlier idea was to have `perch-hook` capture `GetConsoleWindow()` at session start and
persist the terminal HWND in a sidecar. `AttachConsole` supersedes it for the fallback:

- **Zero staleness.** Computed at the moment of the click, not persisted from session start, so
  no need to validate a possibly-recycled HWND.
- **No hook plumbing.** The whole thing lives in `WindowActivator`; nothing to write, watch, or
  reconcile.
- **Likely fixes classic conhost too.** With the default terminal set to *Windows Console Host*,
  the console window belongs to a `conhost.exe` that is a *child* of the shell (not an
  ancestor), so traversal misses it — but `AttachConsole` -> `GetConsoleWindow()` returns that
  conhost window directly.

## Intended strategy

1. **Traversal (primary).** Unchanged, and now excludes `explorer.exe` so an unresolved session
   fails honestly instead of focusing the desktop. This is the common, cheap path and it covers
   IDE terminals, in-proc conhost bridges, mintty, etc.
2. **`AttachConsole` correlation (fallback).** Only when traversal yields no non-explorer host.
   On-demand, no persistence.
3. **Hook sidecar (last resort, maybe never).** The only thing left that `AttachConsole` cannot
   see is a target whose console it is *denied* — which is the elevation case, and that case
   also blocks `SetForegroundWindow`, so a persisted HWND would not help focus anyway. In
   practice there is little left for the hook to add.

## Gotcha: the exe-name marshaling trap

The explorer exclusion compares `PROCESSENTRY32.szExeFile` against `"explorer.exe"`. That struct is
declared `CharSet.Auto` (Unicode on modern Windows), so `Process32First`/`Process32Next` **must**
also be declared `CharSet = CharSet.Auto` — otherwise they default to `CharSet.Ansi`, call the `…A`
entry points, and fill `szExeFile` with ANSI bytes that the Unicode struct reads back as garbage.
The comparison then never matches, the exclusion silently no-ops, and traversal focuses an
`explorer.exe` window instead of falling through to the console resolver. The pre-existing code was
immune only because it read numeric fields (`GetParentPid`); the host exclusion was the first thing
to consume `szExeFile`. When testing, verify the *owner of the focused hwnd*, not just the returned
bool — and reproduce Perch's runtime with a **WinExe/STA** harness, not a console app.

## Caveats

- **Process-global console state.** A process can be attached to only one console at a time, so
  the attach briefly mutates Perch's console state. Perch is a GUI app with no console of its
  own, so this is normally safe, but it must be: serialised (never concurrent), kept brief,
  always paired with `FreeConsole()` in a `finally`, and ideally run off any thread that could
  touch console APIs.
- **Elevation / integrity.** `AttachConsole` to a higher-integrity process fails with
  access-denied (and `SetForegroundWindow` across integrity levels is blocked by UIPI anyway).
  This axis is currently unhandled end-to-end; detect the mismatch and tell the user rather than
  leaving a dead click. A non-elevated Perch cannot focus an elevated terminal.
- **Git Bash / mintty.** `GetConsoleWindow()` returns 0 there (mintty is not a Win32 console),
  so `AttachConsole` yields nothing — but traversal already handles mintty (the `mintty.exe`
  window is a real ancestor), so the fallback is not needed.
- **Background tab.** Both paths resolve the terminal *window*; Windows Terminal still shows
  only its active tab and cannot be steered to a background tab via Win32. Known ceiling.

## Trying it

`tools/focus.ps1 <pid>` reproduces the whole decision for any live session pid: it prints the
ancestry, runs the traversal resolver (with explorer exclusion), runs the `AttachConsole`
resolver, reports whether the two agree, flags an integrity mismatch, and (unless `-NoFocus`)
actually raises the resolved window so you can confirm it works. Use it to sweep the launch
matrix in `docs/` and see which conditions each path clears.

## Related

- `src/Perch.Platform.Windows/WindowActivator.cs` - the live implementation. Both pieces have
  landed: traversal excludes `explorer.exe`, and `ResolveTerminalViaConsole` is the `AttachConsole`
  fallback taken when the ancestry walk finds nothing. No hook, no persisted state; an unresolved
  session returns `false` and the caller (`App.FocusSession`) shows the "no window to focus" toast.
- `tools/focus.ps1` - the standalone diagnostic / manual focus tester.
