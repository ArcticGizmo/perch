# To-dos & Reminders

A first-class personal to-do / reminder feature: the user writes their own tasks (not the
Claude-derived per-session `TaskItem` checklist), Perch surfaces the pressing ones on the overlay,
and nudges when one falls due.

## Scope (as shipped)

- **Global** list (not per-project).
- Each item: **title** (required) + **description** + **optional due date/time**. No recurrence.
- Overlay **"To do" strip**: the top 3 outstanding items with relative due times ("in 2h",
  "overdue 5m"); right-click a line to **Complete**, left-click to open the window.
- Dedicated **Todos window** (add / edit / complete / delete, show-completed toggle), opened from
  the tray menu or an overlay line.
- **Desktop reminder** toast when an item's due time arrives, once per item (survives restart).

## Architecture

Core data layer (`src/Perch.Core/Data/`, UI-free):

- `Todo.cs` — mutable model (`Id`, `Title`, `Description`, `DueUtc?`, `Completed`, `CompletedUtc?`,
  `ReminderFiredUtc?`, `CreatedUtc`). Timestamps stored in **UTC**. Mirrors `QuickLink`.
- `TodoStore.cs` — owned JSON store at `%APPDATA%\Perch\todos.json` (per-profile via
  `AppProfile.DataFolderName`), modelled on `AchievementStore`: `Load()`/`LoadFrom(path)`/`Save()`,
  best-effort IO. CRUD + `TopOutstanding(n)` (overdue/soonest first, undated last) + the static
  `DueForReminder(todos, nowUtc)` predicate (past-due, unfired, incomplete) used by the poller and
  unit-tested directly.
- `RelativeTime.cs` — pure two-directional `DueLabel(nowUtc, dueUtc)`.

Two `AppSettings` toggles (`ShowTodos`, `TodoRemindersEnabled`, both default on) with matching
`SettingsRegistry` descriptors and a `PreviewTarget.Todos`.

App head (`src/Perch.App/`):

- `Views/OverlayCanvas.Todos.cs` — the owner-drawn strip (new partial). Fields/gates, `TodoLine`
  DTO, `SetShowTodos`, `SetTopTodos`, `DrawTodosStrip`, `HitTestTodoRow`, `ShowTodoMenu`, and the
  `TodosRequested` / `TodoCompleteRequested` events. Wired into `OverlayCanvas.cs` at
  `PanelBodyHeight`, `Draw`, `RouteClick`, `ShowContextMenuAt`, and the hover handlers — modelled on
  the daemon strip. Gated through `OverlaySettingsGates.Apply`.
- `Services/TodoMonitorHost.cs` — a `DispatcherTimer` (60s) that feeds the strip and fires due
  reminders via `NotificationService.ShowInfo(...)`, stamping `ReminderFiredUtc`. Modelled on
  `UsageMonitorHost`. `RefreshNow()` re-runs the pass after an overlay Complete.
- `Windows/TodoWindow.cs` — the editor window (single reused instance via `WindowHost.ShowOrFocus`),
  styled off `DaemonListWindow`. Add/edit form (`CalendarDatePicker` + `TimePicker`; a bare date
  defaults to 9am local) over a scrollable list with per-row Complete/Delete.
- `App.axaml.cs` — one shared `TodoStore` instance across the window, the host, and the overlay
  Complete handler, so an edit anywhere is seen everywhere without reloading. Host started/stopped
  in `ApplyDisplaySettings`; window closed in `CloseAuxWindows`; tray "To-dos…" item; host disposed
  on shutdown.

## Tests

`tests/Perch.Tests/TodoStoreTests.cs` (CRUD round-trip, `TopOutstanding` ordering, `DueForReminder`
selection + dedupe) and `RelativeTimeTests.cs`. `SettingsRegistryTests` covers the new toggles.

## Verification

- `dotnet build perch.slnx`; `dotnet test tests/Perch.Tests/Perch.Tests.csproj`.
- Headless render of the strip: `dotnet run --project src/Perch.App -f net10.0-windows10.0.19041.0 -- render <dir>`
  → `overlay_todos_*.png`. (The Todos window is Fluent-templated, so it isn't rendered by the
  owner-drawn harness — eyeball it by running the tray app.)

## Possible follow-ups

- Recurrence (daily/weekly).
- A per-session menu entry / project association.
- Sub-minute reminder precision via a one-shot deadline timer (as `SessionMonitorHost` does).
