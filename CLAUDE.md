# Perch

A Windows system-tray app (.NET 10 / WinForms, C#) that monitors active Claude Code sessions and
surfaces their status as desktop overlays, notifications, and stats. Source lives in `src/`; a
companion Claude Code plugin lives in `plugins/perch/`.

## Build & run

- Build: `dotnet build src/Perch.csproj`
- Run (dev): `dotnet run --project src`
- Release artifacts are produced via Velopack (`vpk`) / `publish.bat` — see `README.md`.

Target is `net10.0-windows`, `Nullable` and `ImplicitUsings` enabled. There is no test project; verify
logic-heavy changes with a quick throwaway script against real data in `~/.claude/` before relying on
the UI, since the UI itself can only be eyeballed by running the tray app.

## Conventions & gotchas

- **Owner-drawn text must size its rectangle from the font's line height, never a hard-coded pixel
  value.** When painting into a bounded `Rectangle` with `TextRenderer.DrawText` — especially centered
  text, large fonts, or anything that has to survive a DPI change — derive the height from `font.Height`
  (plus padding), not a magic number like `34`. A rectangle shorter than the font's line height clips
  the **bottoms** of the glyphs. This has bitten the stat cards in `StatsForm` more than once; watch for
  it in any new card/badge/number rendering. (Drawing at a `Point` instead of a `Rectangle` doesn't
  clip, but loses centering.)
- **Dashboards are owner-drawn through a single measure-or-paint routine.** e.g. `StatsForm.DrawDashboard(Graphics?, width)`
  returns the content height when `Graphics` is null (measure pass) and paints when it isn't. Keep the
  two in one method so the measured height and the painted layout can never drift apart.
- **IO / heavy work runs off the UI thread**, then marshals back: `Task.Run(...).ContinueWith(t => BeginInvoke(...))`.
  Guard the callback with `IsHandleCreated && !IsDisposed` and swallow `ObjectDisposedException` /
  `InvalidOperationException` (the window may have closed mid-flight). See `HistoryViewerForm`,
  `StatsForm`, and `OverlayApplicationContext.RefreshUsage` for the pattern.
- **Use the shared `Theme` palette** (`SettingsControls.cs`) for colours — don't hand-code `Color.FromArgb`
  in new UI; the overlay, settings, history, and stats windows are meant to read as one app.
- **Data sources are files under `~/.claude/`**, read best-effort:
  - Live session state: `~/.claude/sessions/{sessionId}.json` plus sidecars (`.mode`, `.notify`, `.history`).
  - Transcripts: `~/.claude/projects/{enc-cwd}/{sessionId}.jsonl` (append-only, one JSON record per line,
    each with a `timestamp`; assistant records carry `message.usage` and `message.model`).
  Open with `FileShare.ReadWrite` (files are written live) and tolerate malformed/partial trailing lines —
  parse defensively and never throw out of a scan.
- **Single reused window instances.** Settings / history / stats windows are created lazily and reused;
  they're closed and disposed together in `OverlayApplicationContext` (Exit / Dispose / update flow).
  Wire any new top-level window into all three.
