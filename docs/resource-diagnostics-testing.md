# Resource diagnostics: manual testing

Companion to `docs/resource-diagnostics-plan.md`. What's verified so far: unit tests (codec, redactor, recorder against a fake process table, every finding kind), the headless render of the timeline and breakdown, and the Windows process table's native reads against Perch's own process. **Not yet run live:** the window, a real recording, and the findings on real data.

## Builds

- **Before (main):** `git checkout main && dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`
- **After (branch):** `git checkout resource-diagnostics && dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0`

Recordings land in `%APPDATA%\Perch (Dev)\diagnostics\resources-*.jsonl` for a dev build (`%APPDATA%\Perch\diagnostics\` for an installed one).

## Before / after

| # | Do this | Before (main) | After (branch) |
|---|---|---|---|
| 1 | Right-click the overlay's CPU/RAM strip | Only "Hide system metrics" | "Analyse resource usage…" above "Hide system metrics" |
| 2 | Right-click the overlay header | Show/Hide strips, dock, placements… | Same, plus "Analyse resource usage…" (works with the strip hidden) |
| 3 | Open it with no recordings saved | n/a | "Not recording"; the breakdown explains what Start does; no findings |
| 4 | Press **Start recording** | n/a | Status turns red "● Recording · 0s · 0 samples"; the first sample lands after about 10 s (one priming tick, then one interval); the charts grow every 5 s |
| 5 | Drag across the charts while recording | n/a | The cursor follows the pointer; the breakdown shows that moment; **Follow live** appears; clicking it snaps back to the newest sample |
| 6 | Press **Stop recording**, close, reopen | n/a | Reopens on the newest recording, status shows its start time, length and sample count (no "cut off") |
| 7 | Close the window while recording, reopen | n/a | The recording was finalised (not "cut off") |
| 8 | Kill Perch from Task Manager mid-recording, relaunch, reopen | n/a | Loads the recording with "· cut off (Perch closed mid-recording)" |
| 9 | **Recordings ▾** | n/a | Newest first with sizes, the current one checked; picking one loads it; "Show folder" opens Explorer on it; disabled while recording |

## Findings to provoke

Start a recording first, then do one of these. Each takes a few minutes and costs no API usage unless noted.

| Finding | How to provoke it | Expect |
|---|---|---|
| Sustained CPU + a session as the culprit | In a Claude session, ask: `Run "dotnet build perch.slnx --no-incremental" three times in a row and tell me how long each took.` (bills the account for a short session) | "CPU saturated for …" naming `Session · <project>`; picking it opens that session's tree with `dotnet` / `VBCSCompiler` under `claude`. Defender (`MsMpEng`) often shows up alongside, scanning the build output |
| Something that isn't Claude | `docker run --rm alpine sh -c "timeout 90 sh -c 'while :; do :; done'"` (needs Docker running) | A "Docker / WSL" band rises; a runaway or a CPU peak naming it. If `vmmem` can't be opened it shows as **Unattributed** instead; that is the expected fallback, note which one you saw |
| Chrome being Chrome | Open a heavy page set (a few YouTube tabs) | A "Chrome" band; on a many-core machine a single busy renderer is an **Info** runaway, not a warning |
| Duplicated tooling | Open two or three sessions in projects that load the same MCP server (e.g. a shared `.mcp.json` server) and wait 30 s | "<tool> running ×N, one per session", with the summed memory |
| Orphan | In a session, ask: `Start "npx http-server -p 8099" in the background and then stop.` Then `/exit` that session and wait 40 s | "<tool> outlived its session"; the process is still under the ended session's group |
| Memory pressure | Hard to provoke safely; skip unless it happens naturally | Critical "Memory pressure for …" when RAM ≥ 90% or commit ≥ 90% for 15 s |

## Attribution (after the snapshot rework)

| # | Do this | Before (commit 8eae0aa) | After |
|---|---|---|---|
| A1 | Record 1 minute, look at the memory chart | Unattributed was most of the chart (74% on the first real recording) | Unattributed is a thin band (~25–30%); `Compressed memory`, `Docker / WSL` (vmmemWSL), `Windows` services and Defender appear as their own groups |
| A2 | Look at the bottom of the breakdown | Only Everything else + Unattributed | Kernel paged pool, Kernel non-paged pool & drivers, System file cache, Modified pages, Interrupts & DPCs, each with one figure |
| A3 | Points of interest | Unattributed finding blamed "protected processes / vmmem" | "Kernel pools hold …" (on this machine ~7–8 GB, non-paged ~2.6 GB) pointing at drivers; "… compressed"; Unattributed explained as shared pages |
| A4 | Expand a group (e.g. Chrome or a session) | Memory per process only | Each process also shows "(+N shared)" when it maps ≥ 16 MB of shared pages |
| A5 | Open the 9:10 recording from before the rework | n/a | Still loads; no kernel bands; its Unattributed finding says it predates kernel figures and suggests recording again |
| A6 | Compare with Task Manager → Performance → Memory "In use" | n/a | The chart's top edge matches "In use" + "Modified" within a few hundred MB |

## Causes and advice (R7)

| # | Do this | Before (commit 9b20867) | After |
|---|---|---|---|
| C1 | Start a recording, then in one session ask: `Use 6 sub-agents in parallel to each summarise a different folder of this repo.` (bills the account) | Session shows only its process tree at the cursor; CPU gap is "Unattributed" | The **Claude** lane under the CPU chart shows that session's tool calls/min climbing as CPU rises; a "ran N agents at once" finding with advice to cap parallel sub-agents |
| C2 | Expand that session in the breakdown | Process tree only | "What it launched, whole recording": hooks (named by plugin or settings file), status line, Bash/PowerShell tool commands, Claude's search, git run by Claude Code, with launches / processes / CPU-seconds; hooks with 20+ launches in orange |
| C3 | With a plugin hook on every tool call installed (any `PreToolUse` with an empty matcher) | No mention | "<hook> · the <plugin> plugin: ~N runs", the events it's on, its share of launches, advice to disable it via `/plugin` or narrow its matcher, and **Open the plugin's hooks.json** |
| C4 | Uninstall that plugin, reopen the recording | n/a | The finding still names it: the recording carries its own hook catalog |
| C5 | Look at the points of interest | Numbers only | Every card has a "What you can do" block; Runaway on System / WmiPrvSE / Defender / CrowdStrike never says to end them |
| C6 | Your user settings register `perch-hook` from both Perch and Perch (Dev) | No mention | "perch-hook is registered 2 times" with **Open the settings file** |
| C7 | Run `PERCH_RESOURCE_RECORDING=<a .jsonl> dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render captures/x` | n/a | `captures/x/resource_real_analysis.txt` lists findings, advice and causes per session for that recording |

## Hover (charts)

| # | Do this | Before (commit 515af3f) | After |
|---|---|---|---|
| H1 | Hover the CPU chart | Nothing; no way to read a band's value | A dotted guide at that moment and a tooltip listing every band's CPU there, largest first; the band under the pointer is outlined, the others dim, and its legend entry lights up |
| H2 | Hover the memory chart | Same | Same, in bytes, with RAM used of total in the header |
| H3 | Hover a session's bars in the Claude lane | Colours unexplained, no scale | Tooltip: each session's tool calls and agents in the last minute, plus new processes seen; that session's CPU and memory bands light up too |
| H4 | Look at the Claude lane | Caption on top of the bars | A caption row above it with a colour chip per session and the line's scale; the left axis gives the bars' scale (rounded, e.g. 200/min) |
| H5 | Hover a legend entry or a lane chip | Nothing | That series is highlighted in every chart |
| H6 | Move the pointer off the charts | n/a | Tooltip and highlight clear |

## Hooks page

| # | Do this | Before (commit e083e7c) | After |
|---|---|---|---|
| K1 | Open a recording, click **Hooks** in the toolbar | Only the Overview | One card per hook, busiest first and open: totals (runs, caught, failed, CPU-s caught) and a runs-per-minute strip coloured by event |
| K2 | Read the event rows | n/a | PreToolUse / PostToolUse / Stop / SubagentStart… with the matcher ("every tool" or the regex), runs, caught, average duration where Claude Code recorded one, and runs by tool |
| K3 | Expand PreToolUse | n/a | Runs by session and sub-agent, then the latest runs: time, tool, session · agent, "caught: pid N, M processes" or "not caught" |
| K4 | On the Overview, a hook finding → **See its runs by event** | n/a | Switches to Hooks, scrolled to that hook with its busiest event open |
| K5 | Record with a hook registered in your own settings on `PreToolUse` with matcher `Edit|Write`, ask a session to read files and make one edit | n/a | Its PreToolUse row counts only the Edit/Write calls; Read/Grep calls add nothing |
| K6 | Remove a plugin with hooks while a session that loaded it keeps running, record, open Hooks | n/a | Its runs show failures ("Plugin directory does not exist …") with advice to restart those sessions |
| K7 | Open the 09:43 recording (made before hooks were recorded) | n/a | The removed GitKraken hook still appears, its events marked "inferred from timing" |

## Service hosts (svchost by service)

The **Windows** group is a list of core OS executables (`ProcessFamilies`), most of whose load is `svchost`. Each `svchost` is now named by the services it runs, read from the Service Control Manager (no elevation; SYSTEM services' command lines would need it). Verified so far: unit tests, the headless render (`resource_breakdown_windows_1x.png`), and the native read in `resource_selfcheck.txt` ("svchost named 100 of 100" on the dev machine).

| # | Do this | Before (commit 04babd1) | After |
|---|---|---|---|
| S1 | Record 1 minute, expand **Windows** in the breakdown | Rows read `svchost (1234)` | Rows read `svchost · Windows Management Instrumentation (1234)`; a host running several services shows the first plus "+N" |
| S2 | While recording, run **Settings → Windows Update → Check for updates** (or wait for a scan) | A busy one is "svchost (1234) busy for …" with advice to look it up in Task Manager → Go to service(s) | "svchost · Windows Update (1234) busy for …"; the advice names wuauserv and suggests Pause updates. Only appears if it holds most of a core for a minute |
| S3 | Read any svchost runaway card for a service with no specific advice | Same generic Task Manager advice | "This svchost runs <Display name> (<name>)", then: don't end Windows services; services.msc can set an unused app's service to Manual |
| S4 | Compare a row's pid with Task Manager → Services (sort by PID) | n/a | The same service(s) for that pid |
| S5 | Open a recording made before this change | n/a | Loads; svchost rows show the plain `svchost (pid)` and the generic advice |
| S6 | Open the newest `.jsonl`, search `"svc"` | No such field | svchost `proc` lines carry `"svc":[["wuauserv","Windows Update"]]`; non-service processes have none |

## Privacy check

Open the newest `.jsonl` in an editor and search for `"cmd"`. Expect your user folder as `~`, and any `--token`, `--api-key`, `KEY=…`, `Bearer …`, URL passwords or long token-like runs as `***`. GUIDs (e.g. `--resume <session id>`) and dated folder names are kept.

## Cost

While a recording runs, the **Perch** group in the breakdown should stay around 1–2% CPU. Outside a recording nothing extra runs (the window can stay open on a saved recording at no cost).

## Not checkable on demand

- The 4-hour auto-stop (status would return to the recording's summary with reason "time limit" in the file's last line).
- The macOS head: it uses the portable fallback (no private working set, I/O or command lines yet), plan R5.
