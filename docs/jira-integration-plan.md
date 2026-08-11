# Jira ticket deep-link — implementation plan

## Context

Branches in many teams encode a Jira key (e.g. `SFTY-1234-add-audit-log`). Today Perch shows a
per-session PR glyph but nothing that jumps to the tracked ticket. We want a small, opt-in overlay
glyph that, for any session whose branch contains a Jira key, opens
`https://{subdomain}.atlassian.net/browse/{KEY}` in the browser.

Crucially this is **pure and offline**: the key is extracted from the branch name and the URL is
string-built — **no network, no `gh`/CLI, no OAuth, no background service or cache**. That makes it
far simpler than the PR feature it otherwise mirrors. (A richer "fetch ticket summary/status" tier
would need REST + credential storage; explicitly out of scope — the branch->key->URL parsing here is
the foundation for it later, so nothing done now is throwaway.)

Confirmed decisions:
- **Key matching:** default Jira pattern `[A-Z][A-Z0-9]+-\d+`, first match in the branch, with an
  optional comma-separated **project-key filter** (e.g. `SFTY, PROJ`) to suppress false matches.
- **Surface:** a small ticket glyph in the row's left-of-name glyph cluster (like the PR glyph);
  hover shows the key; **left-click opens**, **middle-click opens in a new window**. No flyout
  (single URL).

## Design

The overlay-facing model (`ClaudeSession`) has **no branch field today** — PR status uses the branch
only as an internal cache key. The cheapest existing branch source on the hot scan is
`PrStatusService.ReadHeadRef(cwd)` (`src/Perch.Core/Data/PrStatusService.cs:483`), a plain read of
`.git/HEAD` returning `ref: refs/heads/<branch>`. We reuse it, parse the leaf, run the key regex, and
attach a resolved `JiraTicketInfo?` to `ClaudeSession` at the existing `BuildSession` site — exactly
where `PullRequest`/`GitStats` are attached. No new service, no `Updated` event, no TTL.

### 1. Core logic (pure, unit-tested) — new `src/Perch.Core/Data/JiraTicket.cs`
- `internal readonly record struct JiraTicketInfo(string Key, string Url)`.
- `internal static class JiraLink` with:
  - `string? BranchFromHeadRef(string? headRef)` — returns the branch leaf from
    `ref: refs/heads/<branch>`; `null` for detached HEAD (raw sha) or null input.
  - `string? NormalizeSubdomain(string? raw)` — accept `mycompany`, `mycompany.atlassian.net`, or
    `https://mycompany.atlassian.net/`; strip scheme, trailing `/`, and a trailing `.atlassian.net`;
    return the bare slug or `null` if empty.
  - `JiraTicketInfo? Resolve(string? branch, string? subdomain, string? projectFilter)` — first
    regex match of `[A-Z][A-Z0-9]+-\d+`; if `projectFilter` is non-empty, only accept keys whose
    project (case-insensitive) is in the comma-split set; build
    `https://{slug}.atlassian.net/browse/{KEY}`. Returns `null` when subdomain is empty or no key
    matches. Regex is a cached `static readonly`.

### 2. Session model — `src/Perch.Core/Data/ClaudeSession.cs`
- Add ctor param + property `JiraTicketInfo? JiraTicket` and `bool HasJiraTicket => JiraTicket != null`,
  mirroring `PullRequest` / `HasPullRequest`.

### 3. Population — `src/Perch.Core/Data/SessionMonitor.cs`
- Add config fields set from the app: `bool JiraEnabled`, `string? JiraSubdomain`,
  `string? JiraProjectFilter` (pass-through setters, like `PrEnabled`).
- In `BuildSession` (near the `_pr.Get(cwd)` call): when `JiraEnabled` and subdomain present, compute
  `JiraLink.Resolve(JiraLink.BranchFromHeadRef(PrStatusService.ReadHeadRef(cwd)), JiraSubdomain, JiraProjectFilter)`
  and pass it as the new `ClaudeSession` arg. Else pass `null`.

### 4. Overlay glyph — `src/Perch.App/Views/OverlayCanvas.cs`
- Field `_showJiraTicket` + `SetShowJiraTicket(bool)` (early-return-if-unchanged -> `InvalidateVisual`),
  mirroring `SetShowPullRequests` / `SetShowArtifacts`.
- Width constant `JiraIconWidth = 16`; slot in the left-of-name glyph cluster alongside `showPr`:
  `bool showJira = _showJiraTicket && session.JiraTicket is not null;`.
- `DrawJiraIcon(...)` — hand-drawn ticket/tag `StreamGeometry`, tinted from an existing theme role
  (`Palette.Accent`/link brush — no hard-coded `Color.FromArgb`, no new role unless none fits).
- Hit-map `_jiraRects[rowIndex]` + `_hoveredJiraRow` + `ShowJiraTooltip` (shows the key), mirroring
  `_prRects` / `_hoveredPrRow` / `ShowPrTooltip`.
- Click in the pointer-released handler: `HitRect(_jiraRects, p)` -> left-click
  `PlatformServices.UrlOpener.Open(...)`; middle-click -> `OpenInNewWindow(...)`. No flyout.

### 5. Settings plumbing
- **`AppSettings.cs`:** `bool ShowJiraTicket` (default `false`), `string? JiraSubdomain`,
  `string? JiraProjectFilter` (both `[JsonIgnore(WhenWritingNull)]`, stored raw).
- **`SettingDescriptor.cs`:** add `PreviewTarget.JiraTicket`.
- **`SettingsRegistry.cs`:** a `Toggle("jira-ticket", ... SessionRow, PreviewTarget.JiraTicket,
  nameof(AppSettings.ShowJiraTicket) ...)` plus two `Info(... SettingKind.Field ...)` entries for
  `jira-subdomain` and `jira-project-filter` (surface `Integrations`).
- **`SettingsCatalogView.cs` -> `FieldEditor`:** branches for the two new Field ids, mirroring
  `ntfy-host`/`ntfy-topic`.
- **`OverlaySettingsGates.cs`:** `c.SetShowJiraTicket(s.ShowJiraTicket);`.
- **`App.axaml.cs` -> `ApplyDisplaySettings`:** push config to the monitor alongside `PrEnabled`.
  No `SettingsLiveApply` case needed — default `DisplayChanged` re-runs `ApplyDisplaySettings`.

### 6. Preview / sample — `src/Perch.App/Rendering/SampleData.cs`
- Seed `JiraTicket` on one sample session so it lights up in the preview + `render` PNGs.

### 7. Tests — new `tests/Perch.Tests/JiraTicketTests.cs`
- Key extraction, project filter, subdomain normalization, detached HEAD, multiple keys.
- `SettingsRegistryTests` passes once every new `AppSettings` property is covered by a descriptor.

## Verification
1. `dotnet test tests/Perch.Tests/Perch.Tests.csproj`.
2. `dotnet build perch.slnx` (both heads).
3. `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <outDir>` — glyph paints, no clip.
4. Run the tray app: set subdomain + toggle; confirm glyph, tooltip, left/middle click, and that clearing the subdomain hides it.

Scope note: the glyph follows the same overlay surfaces as the PR/artifacts glyphs. Dense-strip parity
and the later "fetch ticket summary" REST tier are deliberate follow-ups.
