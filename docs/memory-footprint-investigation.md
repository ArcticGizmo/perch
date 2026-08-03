# Perch memory-footprint investigation

_Date: 2026-08-03. Investigating whether Perch's RAM usage can be reduced, and where._

## TL;DR

The headline number (~350–400 MB working set) is **not** managed .NET objects. The GC heap is
tiny; the footprint is dominated by the **native GPU rendering stack + the self-contained runtime**.
So the highest-leverage lever is rendering/runtime configuration, **not** trimming the data-layer
caches (those are worth fixing for long-uptime hygiene, but they are a rounding error today).

## Measured baseline (live process, this machine)

Captured with `dotnet-counters` against the running `perch.exe` (16-core box, 3× 1920×1080):

| Metric | Value |
|---|---|
| Working set | **~348 MB** (seen up to 402 MB) |
| Private bytes | ~230 MB |
| Threads | 58 |
| **GC committed heap** | **~57 MB** |
| GC heap size (gen0+1+2+LOH+POH) | ~40 MB |
| — of which LOH | 30.2 MB, **27.5 MB is fragmentation (free)** |
| **Live managed objects (est.)** | **~12 MB** |
| Loaded assemblies | 87 |
| Install on disk | 67 MB (`perch.exe` 47.6 MB single-file, Skia 11 MB) |

**Interpretation:** managed committed (~57 MB) vs working set (~348 MB) ⇒ **~290 MB is
native**: the runtime (coreclr/JIT/R2R images), 87 mapped assemblies, thread stacks (58×),
and the graphics stack. Loaded graphics/runtime module images: `libSkiaSharp` 11 MB,
`av_libGLESv2` (ANGLE) 5.2 MB, `d3dcompiler_47` 4.5 MB, `d3d11` 2.4 MB, `icu.dll` 2.6 MB,
`libHarfBuzzSharp` 1.75 MB — and the GPU/D3D runtime commits substantially more than these
image sizes.

The **27.5 MB LOH fragmentation** is the fingerprint of a transient spike that was collected but
never decommitted — most likely the all-time Stats/Flight scan over ~910 MB of transcripts, or
opening a large transcript (up to 21.6 MB) in the History window.

## Where the runtime memory actually goes

### Native / runtime (~290 MB — the bulk)
- **GPU rendering stack** — Avalonia renders via Skia → ANGLE (`av_libGLESv2`) → D3D11. For a
  mostly-static tray overlay this is the largest addressable native consumer (swapchains, D3D
  device, shader compiler). No `SkiaSharp`/`SKBitmap` is used directly in `src` — all imaging goes
  through Avalonia (Skia is internal).
- **Self-contained runtime + 87 assemblies**, no trimming, no ReadyToRun.
- **ICU** (`icu.dll`, ~2.6 MB image + ICU data) — the app does not set `InvariantGlobalization`
  (only `Perch.Hook` does).
- **58 threads** — thread stacks. No `while(true)`/`new Thread` loops; threads come from the
  thread pool, GC, graphics, WinRT, and per-hotkey message-loop threads.

### Managed heap (~57 MB committed, ~12 MB live — small)
- **LOH fragmentation ~27.5 MB** — recoverable via LOH compaction; transient-peak leftover.
- **Unbounded, never-evicted caches** (grow with distinct transcripts seen over app lifetime, but
  hold small values — strings/numbers/short lists, so bytes are negligible today):
  - 11× `MtimeCache` in `TranscriptReader` (`TranscriptReader.cs:24-34`) — no eviction.
  - 3× cache in `SubAgentReader` (`SubAgentReader.cs:35-41`) — `_meta` cached "forever" by design.
  - static `SessionHistory._projectCache` (`SessionHistory.cs:374`) — no eviction.
  - `WindowsAppIconProvider._resolved` (`WindowsAppIconProvider.cs:26`) and `ShellIcon._appMap`
    (`ShellIcon.cs:63`) — unbounded path/string maps (bounded in practice by installed apps).
- **Transient peaks** (collected after use, but inflate the LOH high-water mark):
  - History viewer parses a whole transcript into memory: whole-file `byte[]` + full `string` +
    `Split('\n')` array, then `List<HistoryEvent>` with per-event strings
    (`TranscriptParser.cs:92-122`, `:73`). Gated behind the 10 MB confirmation.
  - All-time Stats/Flight scans hold per-day aggregates + per-session timestamp lists for the
    entire history at once (`SessionStatsService.cs:258,564`; `FlightPathService.cs:84,111`).
    Not incremental — re-scanned from disk each open (mtime pre-filter only).

### Retained bitmaps (small on this display)
- `GlowWindow` full-screen `WriteableBitmap` (`GlowWindow.cs:131`) — kept alive after `Hide()` for
  quick re-show; **7.9 MB** at 1080p (would be ~33 MB at 4K).
- App-lifetime static decodes of `icon.png`: `OverlayCanvas.Brand` (`OverlayCanvas.cs:151`),
  `StatsWindow._appIcon` (`StatsWindow.cs:184`), tray `WindowIcon` (`App.axaml.cs:983`) — three
  separate decodes of the same asset.
- `OverlayCanvas._quickLinkIcons` (`OverlayCanvas.cs:619-624`) — decoded per quick link; the
  **previous list's bitmaps are not disposed** when rebuilt (`OverlayCanvas.cs:622`), so repeated
  quick-link edits churn undisposed full-decode bitmaps.
- Reused windows (Settings/Stats/History/Flight/Wrapped/QR) are genuinely `Close()`d and nulled, so
  their bitmaps are freed on close — not a resident concern.

### Timers/polling (CPU/GC churn, not footprint — but keep in mind)
Fixed-cadence, no idle backoff even with zero active sessions: SessionMonitor reconcile 30 s +
FS watcher, Usage HTTP 5 min, Status HTTP 2 min, Daemon roster 5 s + FS watcher, UpdateService
hourly (metadata only, downloads nothing until user acts). Metrics/mic/hypertree timers are off by
default.

## Recommendations, by leverage

### Tier 1 — confirm & attack the native bulk (biggest potential win)
1. **Try software rendering.** Set Avalonia `Win32PlatformOptions.RenderingMode` to prefer
   `Software` (drop ANGLE/D3D). For a small, rarely-repainted overlay the CPU cost is likely
   negligible and this could remove the entire D3D/ANGLE/d3dcompiler commit (tens to >100 MB).
   **This is an A/B experiment: build both ways, measure working set.** Highest expected impact,
   low code cost — but must eyeball overlay/animation smoothness (confetti, timelines).

   **DONE (2026-08-03).** `Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }`
   added in `Program.BuildAvaloniaApp`. Measured on the same machine:

   | | GPU (installed Release, prior) | Software (Debug dev build) |
   |---|---|---|
   | Working set | ~348–402 MB | **172 MB** |
   | Private | ~230 MB | **79 MB** |
   | Threads | 58 | 34 |
   | ANGLE/D3D modules | `av_libGLESv2`, `d3d11`, `d3dcompiler_47` loaded | **none loaded** |

   ~50% working-set cut, and the entire GPU stack is gone (only the Skia software rasterizer +
   HarfBuzz + ICU remain). Caveat: this is Debug-dev vs Release, not a clean A/B — Debug carries
   extra diagnostics, so a Release software build should be **at least** this lean. Still needs a
   visual smoothness check on the overlay/confetti/timeline animations.

   The **screen-edge glow feature was also removed** (unused/experimental): `GlowWindow.cs` deleted
   and unwired from `App`, `SettingsWindow`, `HeadlessRenderer`, `AppSettings.ScreenEdgeGlow`, plus
   the now-dead `DragCompleted` event on the overlay.

### Tier 2 — cheap runtime config (working set)
2. **Add `runtimeconfig.template.json`**: explicit Workstation GC (`System.GC.Server=false`),
   keep DATAS (default on in .NET 9/10 — good), set `System.GC.ConserveMemory` (e.g. 5) and a low
   `System.GC.RetainVMLimit` to return memory to the OS more aggressively. Trades a little CPU for
   lower footprint — ideal for a background tray app.
3. **`GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect()`** after closing the
   Stats/History/Flight windows (or on an idle tick). Returns the ~27 MB LOH fragmentation.
4. **`<InvariantGlobalization>true</InvariantGlobalization>`** on `Perch.App` (as the hook already
   does) — drops ICU load. Verify nothing relies on culture-sensitive formatting/sorting first.

### Tier 3 — code hygiene (small bytes now, prevents long-uptime growth)
5. Dispose the previous quick-link bitmaps in `SetQuickLinks` (`OverlayCanvas.cs:622`).
6. Bound the never-evicted caches: prune `TranscriptReader`/`SubAgentReader` mtime-cache and
   `SessionHistory._projectCache` entries when a session dies, or LRU-cap them.
7. Dispose `GlowWindow`'s bitmap on hide (rebuild on show) — 8 MB here, ~33 MB on 4K displays.
8. Share a single decoded `icon.png` instead of three static decodes.

### Tier 4 — build options (mostly disk + startup; higher effort/risk)
9. **ReadyToRun** — cuts JIT working set and startup; increases file size.
10. **Trimming** (`PublishTrimmed`) — fewer/smaller of the 87 assemblies. Meaningful, but Avalonia
    uses reflection/XAML; needs careful testing (trim warnings, runtime XAML). Do last, with the
    `render` harness + a full app smoke test.

## Honest caveats
- Attribution of the ~290 MB native portion is inferred from module images + the managed/native
  gap, not a full native breakdown. To pin it precisely, capture a VMMap snapshot (committed by
  region: Private / Mapped / Shareable / Heap) or a `dotnet-dump` + native analysis.
- The single biggest uncertainty-resolver is the **software-rendering A/B** in Tier 1 — one build,
  one measurement, tells us how much of the footprint is the GPU stack.

## How this was measured
- `Get-Process perch` for working set / private / threads.
- `dotnet-counters collect --counters System.Runtime` for GC heap/committed/LOH/fragmentation.
- `Process.Modules` for loaded graphics/runtime module images.
- Three code-mapping passes over the data layer, rendering/imaging, and service/timer wiring.
