# Resource diagnostics ("Analyse resource usage")

## Problem

With several Claude Code sessions running, the machine's CPU and RAM regularly max out. The overlay's system strip says *that* it is happening (two bars) and the per-session readout says how much each session's process tree uses right now, but nothing answers the real question: **what is actually using the resources, over time, and how does it relate to everything else?** The culprit is often not Claude at all: Docker's VM (`vmmem` / `VmmemWSL`), Chrome, Defender or CrowdStrike scanning the files a session just wrote, a build a session kicked off, or an MCP server that every session spawned its own copy of.

## Goal

An explicitly started, time-bounded **recording** of whole-machine resource use, with a viewer that lets the user scrub through it and drill from "the machine was pegged at 14:32" to "Docker's VM had 9 GB and the Jira MCP server was running four times, once per session".

- **Opt-in and explicit.** Nothing samples until the user presses Start in the Resource monitor window. It has its own fixed 5-second interval, independent of the overlay's 2-second `MetricsMonitor`, which stays untouched.
- **Whole machine, not just Claude.** Every readable process is sampled each tick. Claude session trees and Perch itself are always kept in full; everything else is kept when it is among the top consumers, and the long tail is summed into one "rest" figure so the file stays small.
- **Attribution.** Each process is grouped: a Claude session (by its process tree), Perch, or an application family (Chrome, Docker / WSL, VS Code, Defender, …), with the parent chain used to pull generic hosts (`node`, `python`, `cmd`, `conhost`) into the application that started them.
- **Points of interest.** The analysis finds peaks, sustained saturation, memory pressure, runaway processes, memory growth, duplicated tooling across sessions and orphans left behind by ended sessions, and the viewer jumps to each.

## Entry point

- Overlay: right-click the system metrics strip → **Analyse resource usage…** (beside "Hide system metrics"). The header's right-click menu carries the same item, so it is reachable when the strip is hidden.
- Opens `ResourceMonitorWindow` (single reused instance via `WindowHost.ShowOrFocus`, closed in `CloseAuxWindows`).
- The recorder is owned by the window: closing the window stops a running recording (the file is finalised and kept). A recording also stops itself after 4 hours.

## Architecture

```
Perch.Core/Platform/IProcessTable.cs          seam: enumerate processes, read counters, read command line, read commit
Perch.Platform.Windows/WindowsProcessTable.cs Toolhelp + OpenProcess(QUERY_LIMITED) + GetProcessTimes / K32GetProcessMemoryInfo(EX2)
                                              / GetProcessIoCounters / GetProcessHandleCount / NtQueryInformationProcess(60)
Perch.Core/Platform/ManagedProcessTable.cs    portable fallback (System.Diagnostics.Process + ISystemMetrics.ReadParentMap), used by the mac head for now
Perch.Core/Data/Resources/
  ResourceRecords.cs        the record model (header / proc / session / tick / end)
  ResourceRecordCodec.cs    JSONL write + defensive parse
  ResourceRecorder.cs       the 5s sampler: deltas, keep-set selection, writes the file, raises each record
  ResourceRecording.cs      in-memory recording built from records (live or loaded from disk)
  ProcessFamilies.cs        exe name → application family, generic-host rules
  ResourceAnalysis.cs       attribution, grouped series, findings
  CommandLineRedactor.cs    scrubs secrets and the user profile path from command lines before they are written
Perch.App/Windows/ResourceMonitorWindow.cs    toolbar (Start/Stop, status, recordings), hosts the views below
Perch.App/Views/ResourceTimelineView.cs       owner-drawn stacked CPU + memory charts, finding markers, scrub cursor
Perch.App/Views/ResourceBreakdownView.cs      owner-drawn per-group breakdown at the cursor, expandable to the process tree
```

### Platform seam

```csharp
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name, int Threads);
public readonly record struct ProcessCounters(DateTime? StartUtc, TimeSpan Cpu, long WorkingSet,
    long PrivateWorkingSet, long Commit, ulong IoReadBytes, ulong IoWriteBytes, int Handles);

public interface IProcessTable
{
    IReadOnlyList<ProcessEntry> Enumerate();          // one snapshot per tick
    ProcessCounters? Read(int pid);                   // one handle, cheap reads; null = gone / access denied
    string? ReadCommandLine(int pid);                 // read once per process identity, only for kept processes
    (long used, long limit) ReadCommit();             // system commit charge, the real "out of memory" signal
}
```

The seam also has `Snapshot()` (every process with its counters in one call), `ReadKernelMemory()` and `ReadProcessorTimes()`, each with a default so a head without them still works.

- **Every process, no handles.** On Windows `Snapshot()` is one `NtQuerySystemInformation(SystemProcessInformation)` call, the source Task Manager and `Get-Process` use. It opens no process, so SYSTEM services, protected processes (Defender, CrowdStrike), `Memory Compression` and the WSL/Docker VM (`vmmemWSL`) are all counted without elevation. The first version opened each process with `PROCESS_QUERY_LIMITED_INFORMATION` and lost ~190 of ~430 processes that way; on the user's first recording that left 74% of in-use memory unattributed.
- **Memory figure.** Charts use the *private working set* (what Task Manager's Memory column shows). Summing plain working sets double-counts shared pages and overshoots physical RAM. The full working set is recorded too, so the breakdown can show what each process maps from shared pages. Commit (private bytes) is recorded because commit exhaustion, not physical RAM, is what makes Windows stall.
- **The OS's own memory and CPU.** `ReadKernelMemory()` reads the documented performance counters (Pool Paged Resident, Pool Nonpaged, System Cache Resident, System Driver Resident, Modified Page List) through PDH; `ReadProcessorTimes()` reads per-processor interrupt and DPC time (`SystemProcessorPerformanceInformation`). These get their own chart bands.
- **Command lines** come from `NtQueryInformationProcess(ProcessCommandLineInformation)`, which works with `PROCESS_QUERY_LIMITED_INFORMATION`, so no reading of another process's memory. Read once per new process identity, and only for processes that make the keep-set. SYSTEM services can't be opened even that far, so they have no command line.
- **Unattributed** is what's left: in-use memory minus every process's private working set minus the kernel bands (and the CPU equivalent). It is now mostly pages that processes share (DLLs, memory-mapped files, a browser's shared memory), plus page tables and driver-locked memory. On the user's machine: 39 GB in use = 20 GB processes + 8.6 GB kernel + 11 GB residual.

### Sampling and the keep-set

Every 5 s: one `Enumerate()`, one `Read()` per pid, system CPU (`GetSystemTimes` delta) / RAM / commit. Process identity is `(pid, start time)` so a recycled pid is a new process. CPU% and I/O bytes/s are deltas against the previous tick for the same identity.

A tick keeps, in full:

1. every process in a live Claude session's tree (the session pids come from the monitor scan),
2. Perch's own tree,
3. the top 15 by CPU and the top 15 by private working set,

and sums everything else into a `rest` triple (CPU, memory, count). A machine with 350 processes writes ~40 samples per tick.

### File format

JSON Lines under `<settings dir>/diagnostics/resources-yyyyMMdd-HHmmss.jsonl` (the folder next to `logs/`). Append-only and flushed each tick, so a crash loses at most one tick. The newest 20 recordings are kept.

```jsonc
{"t":"header","v":1,"started":"2026-11-06T03:12:00Z","intervalMs":5000,"cores":16,"totalRam":34359738368,"self":12345}
{"t":"proc","id":7,"pid":4120,"ppid":3988,"name":"node.exe","start":"2026-11-06T02:58:11Z","cmd":"node ~\\.mcp\\jira\\server.js --token ***"}
{"t":"session","pid":3988,"sid":"6f1e…","label":"perch"}
{"t":"tick","at":"2026-11-06T03:12:05Z","cpu":71.2,"ram":30064771072,"commit":41231686041,"commitLimit":51539607552,
 "p":[[7,3.1,104857600,201326592,0,0]],"rest":[4.2,2147483648,311]}
{"t":"end","at":"2026-11-06T04:12:05Z","reason":"stopped"}
```

A `p` row is `[id, cpu%, privateWS, commit, ioReadPerSec, ioWritePerSec, workingSet]` (the last added later; a 6-column row reads as 0). A tick may also carry `"kmem":[pagedPoolResident, nonPagedPool, systemCacheResident, driverResident, modifiedList]` and `"irq":<interrupt+DPC %>`; older recordings without them chart no system bands and say so in their Unattributed finding. A `proc` record is written once, the first time its process is kept. Lines that fail to parse are skipped, so a truncated last line never breaks loading.

### Privacy

Command lines can carry tokens and the user's home path (their username). `CommandLineRedactor` runs before anything is written: values of secret-looking flags and `KEY=VALUE` pairs (`token`, `secret`, `password`, `key`, `auth`, `bearer`, `credential`, …), URL userinfo, and long base64/hex blobs become `***`; the user profile directory becomes `~`; the line is capped at 1 KB. Recordings are local files and nothing is uploaded.

### Attribution

For each sampled process, walk its parent chain (pid → the proc record with that pid whose start time is not after the child's):

1. reaches a session pid → **that session** (label = the session's display name),
2. reaches Perch's pid → **Perch**,
3. otherwise the **application family**: classify the process's own exe; if it is a generic host (`node`, `python`, `java`, `dotnet`, `cmd`, `powershell`, `pwsh`, `bash`, `conhost`, `git`, …), take the first ancestor with a known family instead. Stop-points (`explorer`, `services`, `svchost`, `wininit`, terminals) are never inherited, otherwise everything would be "Explorer". With no family found the group is the exe name.

Families include Chrome, Edge, Firefox, Docker / WSL (`vmmem`, `VmmemWSL`, `com.docker.*`, `dockerd`, `wslhost`, `wslservice`), VS Code, Visual Studio, JetBrains IDEs, Teams, Slack, Outlook, Defender (`MsMpEng`, `MpDefenderCoreService`, `NisSrv`), CrowdStrike (`CSFalcon*`), Windows Search, Windows (core system processes) and Claude desktop.

**What "Windows" covers.** It's a fixed list of OS executables, not "everything the OS does": the kernel process (`System`, whose CPU is driver work, including security agents' kernel drivers), `Registry`, `Secure System` (VBS), session plumbing (`smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`), every `svchost`, the desktop shell (`dwm`, `sihost`, `ctfmon`, Start/Shell/TextInput hosts, Widgets, LockApp), brokers that work on other programs' behalf (`RuntimeBroker`, `dllhost`, `taskhostw`, `WmiPrvSE`, `audiodg`, `spoolsv`) and servicing (`TiWorker`, `TrustedInstaller`, `msiexec`). Kernel pools, file cache, modified pages and interrupts are separate system bands; Memory Compression, Windows Search, Explorer and Windows Terminal are their own families. A non-stop-point member passes the family to unknown children, so an installer's custom actions under `msiexec` count as Windows.

**Service hosts are named by their services.** When the recorder writes a child of `services.exe`, it attaches the services that pid runs, from one `EnumServicesStatusEx(SC_ENUM_PROCESS_INFO)` per tick (only on ticks that write one). Unlike a SYSTEM service's command line, this needs no elevation. The proc record carries them as `"svc":[[name, display], …]`. A `svchost` then reads as "svchost · Windows Update" in the breakdown and in finding titles, and a runaway's advice names the service, with specific advice for the usual suspects (Windows Update, Update Orchestrator, BITS, Delivery Optimization, SysMain, telemetry, WMI, Event Log, Cryptographic Services). Hosts stay in the Windows group: splitting each service into its own group would scatter a hundred one-process groups across the chart.

The viewer charts the top 7 groups by peak share plus **Everything else** (the remaining groups plus the `rest` figure) and **Unattributed**.

### Findings

Each finding has a kind, a severity, a tick range, a peak tick and a one-line description naming the top contributor.

| Finding | Rule |
|---|---|
| CPU peak | local maxima of system CPU ≥ 70%, at least 60 s apart, top 3 |
| Sustained CPU | system CPU ≥ 85% for ≥ 30 s |
| Memory pressure | RAM ≥ 90% of physical or commit ≥ 90% of limit for ≥ 15 s |
| Runaway process | one process ≥ 90% of a core (or ≥ 25% of the machine) for ≥ 60 s |
| Memory growth | one process's private memory grows by ≥ 500 MB and ≥ 50% over the recording |
| Duplicated tooling | the same command line running under ≥ 2 sessions at once (e.g. one MCP server per session), with the summed memory |
| Orphan | a process from a session's tree still running ≥ 30 s after that session's `claude` process exited |
| Kernel pools | paged + non-paged pool ≥ max(4 GB, 10% of RAM), or non-paged ≥ 1.5 GB: a driver holding memory |
| Compressed memory | the Memory Compression process holds ≥ 1 GB: memory is tighter than "in use" suggests |
| Unattributed | the residual averages ≥ 15% of RAM, explained as shared pages / page tables |

## Viewer

- **Toolbar:** Start / Stop (accent), status ("Recording · 04:12 · 51 samples"), **Recordings** (the latest recordings to open; "Show folder"), and **Live** (re-follows the newest tick after scrubbing).
- **Timeline** (owner-drawn, one measure-or-paint `Draw` routine): a stacked CPU chart and a stacked memory chart on a shared time axis, coloured per group with a legend. Finding markers sit above the charts; the selected finding's range is shaded. Click or drag scrubs a cursor; ← / → step one tick.
- **Breakdown at the cursor** (owner-drawn): the time, system CPU / RAM / commit, then one row per group with a share bar, CPU% and memory, sortable by CPU or memory. Clicking a group expands its process tree: indented rows with name, pid, CPU, memory and the (redacted) command line.
- **Findings list:** severity-ordered; clicking one moves the cursor to its peak and selects its group.
- While recording, the charts grow live and the cursor follows the newest tick unless the user has scrubbed.

## Cost and endpoint security

One `NtQuerySystemInformation` process snapshot and one PDH read every 5 s, the same sources Task Manager uses, and only while a recording runs. No process is opened per tick. Command lines are read once per kept process. Recording is off by default and is never started automatically. The interval is fixed at 5 s; it is not user-tunable, so it can't be wound down to something that looks like aggressive enumeration.

## Explaining the load (R7)

The breakdown says *how much*; it doesn't say *why* or *what to change*. The user's 09:43 recording (12 minutes, four sessions) shows the gap:

- **The CPU nobody owns is process churn.** System CPU ran 15–25 points above the sum of all processes, and the gap tracked process launches minute by minute (150–220 new processes seen per minute → 16–24% gap; 20–40 → 3–9%). A process that starts and exits between two 5 s samples is never seen, but its CPU, and the security agents' work on every process start, still lands on the machine.
- **The churn came from one session's sub-agent fan-out, multiplied by hooks.** One session ("session A") went from ~5 to 82–94 tool calls a minute when it started 10 sub-agents (413 calls: Bash 167, Grep 117, Read 59, PowerShell 30). Its tree had 684 processes caught in one sample or more, 662 of them seen only once.
- **Most of those processes weren't Claude's work.** Of 300 `bash.exe` caught: 179 ran `gk ai hook run` (the GitKraken plugin, `gitkraken-hooks`, registered on 21 hook events with an empty matcher, so `PreToolUse` and `PostToolUse` fire on every tool call of every sub-agent), 66 were real Bash tool calls, 51 ran the status line (`node perch-statusline.mjs`). Each hook run is `bash` + `conhost` + `gk`. Estimated: ~800 GitKraken hook runs and 2,000+ processes in 12 minutes, more than the session's own commands.
- **Memory's Unattributed is not where the pain is.** It held steady at ~10.5 GB (shared pages, page tables) while CPU swung; only R6 can split it further.

None of that is visible in a per-process view. R7 adds the *cause* layer:

- **R7a Launch causes.** A Core `LaunchClassifier` labels every process in a session tree by what started it, folding plumbing (`conhost`, Git Bash's inner `bash`) into the launch that needed it:
  - **Hook: \<source\>**: the command matches a hook registered in the session's settings (user, project, `settings.local.json`) or an enabled plugin's `hooks/hooks.json`, named after that plugin or file, with its events and matchers;
  - **Status line**: matches `statusLine.command`;
  - **Bash tool** / **PowerShell tool**: a Claude shell running a command (`source …/shell-snapshots/…`);
  - **Search**: `rg` started by `claude.exe` (Grep/Glob);
  - **Build / test / package manager**: `dotnet`, `msbuild`, `npm`, `node` test runners …;
  - **MCP server**: long-lived children started at session start.
  The breakdown gets a **Causes** view per session ("GitKraken hooks 179 launches · Bash tool 66 · status line 51"), next to the process tree.
- **R7b Claude activity.** Read each recorded session's transcript and its sub-agent transcripts for the recording window: tool calls per minute by tool and by agent. Charted as a lane under the CPU chart, so "CPU jumped here" sits next to "10 sub-agents, 94 tool calls/min started here".
- **R7c Launch counting.** Each tick records how many process identities are new since the last one (the whole machine, plus per session), so churn is a series even for processes outside the keep-set, and the CPU residual is labelled **Short-lived processes** when it tracks launches.
- **R7d Advice.** Findings get a "What you can do" line and, where there's a file, an action button. Each comes from evidence in the recording plus the config, not guesswork:

| Finding | Evidence | What you can do |
|---|---|---|
| Hook fan-out | hook launches per tool call × tool calls | Disable the plugin when you don't need it (`/plugin`), or narrow its matcher; hooks written as `bash -c …` cost an extra shell; call the program directly |
| Sub-agent fan-out | N agents concurrently, tool calls/min, CPU at the time | Ask for fewer parallel agents ("use at most 3 sub-agents") on a loaded machine |
| Status line cost | runs × CPU per run | Use a cheaper status line command (no Node start-up, or cache) |
| Duplicate hooks | the same hook registered twice (e.g. the installed and dev `perch-hook`) | Remove one |
| Build churn | builds per session, compiler servers left running | Prefer incremental builds; shut down build servers (`dotnet build-server shutdown`) when done |
| Kernel pools | (exists) | Pool tags (R6) name the driver |

Order: R7a and R7d first: they work on recordings that already exist (session trees are kept in full), and would have named the GitKraken hooks in the 09:43 recording. Then R7b, then R7c.

### R7 as built

- **Hooks are recorded.** The user removed the GitKraken plugin after the 09:43 recording, and the analysis could then only call it "gk ai hook run, most likely a hook": a recording explained from today's config goes stale. So the recorder writes the hook catalog into the recording (`{"t":"hook", src, kind, plugin, file, event, matcher, cmd}`, profile masked) at the start and whenever a session in a new project or config dir appears (`ResourceRecorder.HookSource` = `ResourceContext.HooksFor`). The viewer merges those with this machine's current hooks for older recordings.
- **`HookCatalog`** reads the user's `settings.json` / `settings.local.json`, each session's `<cwd>/.claude/settings*.json`, every plugin in `plugins/installed_plugins.json`, and the newest cached copy of any plugin that isn't listed there (GitKraken's CLI installs its hooks plugin straight into the cache) plus local plugin folders. The status line is a catalog entry with event `StatusLine`.
- **Classification** (`ResourceAnalysis.Causes.cs`): every process in a session tree is charged to its *launch*, the ancestor Claude started directly. A launch that sources `shell-snapshots` is a Bash tool call (checked first, so a short hook command can't claim the user's own runs of it); else a hook or status-line command whose normalised text appears in the launch's subtree; else the PowerShell tool (`cmd /c "chcp 65001 & pwsh …"`), Claude's search (`rg`, often `claude.exe` re-executed as `rg`), git run by Claude Code itself, a shell command named by what it ran, a long-running helper, or the exe. Normalising (profile masked, escaped quotes, carets and quotes dropped, slashes forward, lower case) is what lets `bash -c "\"C:/…/gk.exe\" ai hook run"` match the hook's `"C:/…/gk.exe" ai hook run`.
- **Activity** (`SessionActivity`): tool calls per agent from the session transcript and `subagents/agent-*.jsonl`, for the recording window. Charted as the "Claude" lane under the CPU chart (bars: tool calls/min per session; line: new processes/min). Hook runs are estimated as tool calls × the hook's PreToolUse/PostToolUse registrations whose matcher takes the tool.
- **Launch counting:** each tick records `"new"`, the process identities born since the last tick; older recordings fall back to session-tree processes first sampled.
- **Findings with advice:** HookFanOut, StatusLine, DuplicateHook, SubagentFanOut, BuildChurn, ShortLivedProcesses (the CPU no process owns, correlated with launches over one-minute averages, and whether the kernel's CPU follows too), plus advice on every earlier finding. Runaway advice never says "end it" for the kernel, Windows services or security agents.
- **Breakdown:** opening a session lists what it launched over the whole recording (launches, processes, CPU-seconds, and what's alive at the cursor) above the processes alive now.
- **Checking against a real recording without the window:** `PERCH_RESOURCE_RECORDING=<file> perch render <dir>` writes `resource_real_analysis.txt` (findings, advice, causes per session) and renders its timeline and breakdown.

On the 09:43 recording it reports: 11 agents at once (up to 105 tool calls/min, CPU 60% against 33%); "gk ai hook run" 98+ launches, 42% of everything sessions started; ~12% of CPU in processes too short-lived to sample (correlation 0.77 with launches, the kernel's CPU 0.79); dotnet started 47+ times in one session; the status line; `perch-hook` registered twice; and the kernel pools.

## Hooks page

"90 launches" names the hook but not the problem. The Hooks page (toolbar: Overview | Hooks; or "See its runs by event" on a hook finding) shows each hook's runs grouped by the event that fired them.

- **Claude Code doesn't log successful hook runs.** Transcripts carry only failures (`attachment` of type `hook_non_blocking_error` and kin: event, command, stderr, exit code) and, per turn, `stop_hook_summary` (each Stop hook's command and `durationMs`). Debug logs aren't written by default.
- **So runs are reconstructed** (`HookActivity`). `SessionActivity` reads *moments* from the session and sub-agent transcripts: PreToolUse at each `tool_use`, PostToolUse (or PostToolUseFailure when `is_error`) at each `tool_result`, UserPromptSubmit at a user prompt in the main transcript, Stop at `turn_duration`, SubagentStart/SubagentStop at a sub-agent transcript's first/last record, SessionStart at the main transcript's first record, PreCompact/PostCompact at `compact_boundary`. Each moment × each hook registered for that event (whose matcher takes the tool) is an expected run.
- **Which registrations count in a session:** the user's own settings always; a project's settings only in that project; a plugin's only where its runs were caught or traced (an installed plugin may not be enabled in that project, and counting it anyway would invent runs).
- **Caught processes are pinned to runs** by start time (the hook's launch process starts 3 s before to 6 s after its event's record), nearest first, same hook and session. A hook no catalog knows (removed since) has no expected runs; its caught processes are pinned to the nearest moment of any event and marked *inferred from timing*.
- **Traces attach** to the nearest run of the same event and command (Stop durations, failure messages).
- **Per hook:** totals (runs, caught, failed, CPU-seconds caught), runs per minute stacked by event, one row per event (matchers, runs, caught, failures, average duration, runs by tool), and on expanding an event the runs by session and sub-agent and the latest runs (time, tool, session · agent, caught pid and process count, duration, failure). Advice: the share of runs from tool calls, how many were for read-only tools and what a matcher would save, the share from sub-agents, failures (a missing program or plugin keeps failing in sessions started before it was removed), and how to disable a plugin.

## Phases

- **R1 Core recorder:** `IProcessTable` + Windows implementation + managed fallback; records, codec, redactor, recorder. Tests: codec round-trip and truncated lines, redactor cases, recorder against a fake table (deltas, keep-set, rest, pid reuse, proc-once).
- **R2 Core analysis:** families, attribution, grouped series, unattributed, findings. Tests over synthetic recordings for each finding kind.
- **R3 UI:** window, timeline, breakdown, findings list, overlay menu entries, App wiring, headless render surfaces from a sample recording.
- **R4 polish:** recordings retention, "Copy summary" (Markdown of the top findings to paste into an issue), hover tooltips on the charts.
- **R5 macOS:** a libproc `IProcessTable` (`proc_pidinfo(PROC_PIDTASKINFO)` for memory/CPU, `proc_pidpath`, `KERN_PROCARGS2` for arguments) replacing the managed fallback.
- **R7 from numbers to actions: built** (R7a–R7d). See "Explaining the load" below and "R7 as built".
- **R6 elevated deep-dive (proposed, not started; needs the user's go-ahead):** what's left after R1–R3 can only be split further with admin rights. An opt-in "Look deeper (needs admin)…" would launch a separate, short-lived elevated helper (`perch.exe` with the `runas` verb, so one UAC prompt per use) that reads, once, and hands back over a pipe:
  - **Pool tags** (`SystemPoolTagInformation`): which driver tag owns the kernel pools, mapped to a driver file by searching `System32\drivers` for the tag. This is what turns "2.6 GB non-paged pool" into "the VPN filter driver".
  - **Memory lists by use** (what RAMMap's Use Counts shows: mapped file, shareable, page table, driver locked, AWE), which splits the remaining Unattributed. Needs `SeProfileSingleProcessPrivilege` and the undocumented `SystemSuperfetchInformation`, whose layout varies by Windows build, so it's the riskier half.
  - **Command lines of SYSTEM services.** No longer needed to name a `svchost`: the Service Control Manager gives every service's pid without elevation (built, see Attribution). Command lines would still add the `-k` group, which says little more.
  The tray itself never runs elevated, and the helper exits after one read.
- **Later:** per-process GPU and disk-queue data need ETW or PDH; out of scope for now.

## Status

- Attribution rework after the first real recording: whole-machine snapshot, kernel/cache/interrupt bands, kernel-pool and compressed-memory findings, shared pages per process. Self-check on the user's machine: 467/467 processes with counters.
- R1–R3 built on branch `resource-diagnostics`. Unit-tested, render-verified, and the Windows process table's native reads checked against Perch's own process. Never run live: the window, a real recording and the findings on real data. See `docs/resource-diagnostics-testing.md`.
- R4 partly in: the Recordings menu and retention (newest 20) are built; "Copy summary" and chart hover tooltips are not.
