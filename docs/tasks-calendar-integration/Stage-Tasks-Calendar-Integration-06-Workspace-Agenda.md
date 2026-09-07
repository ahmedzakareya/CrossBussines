# Stage-Tasks-Calendar-Integration-06 — Workspace Agenda

**Implemented as a service.** `CrossBuy/BL/TasksCalendar/WorkspaceAgendaService.cs`
**No Workspace file was modified.**

---

## 1. What it is

`IWorkspaceAgendaService` — a **read-only composition** over `ITaskService` and `ICalendarService`, returning one
unified, ordered agenda.

It writes nothing. There is no `Save`, no `DbSet` for an agenda, and `CrossDbContext` is not injected at all.

## 2. Owner decisions, and where each lives in the code

| Decision | Implementation |
|---|---|
| 4 — Workspace owns no Task or Calendar business data | the service returns a projection and stores nothing |
| 5 — task due dates appear as a **read union** | `AgendaItemType.Task` items carry the **task's own id** |
| 6 — **no** materialised `CalendarEvent` rows for tasks | there is no write path; asserted by two tests |
| 10, 11 — no automatic cross-module writes | nothing in this file writes to either module |
| 12 — cross-module writes are explicit user actions | none exist here |

## 3. Why it calls the services and never the DbContext

Every access rule already lives in the owning service: `TaskService` applies task scope; `CalendarService.Visible()`
applies organiser ∪ company-scope ∪ attendee. Querying `TaskItems` or `CalendarEvents` here would put both rules in
a third place, and the copy would drift the first time either changed.

The tests make that provable: they substitute both interfaces with fakes. **If the service queried the database
directly, every agenda test would return nothing and fail.**

## 4. The agenda item

`ItemType` · `SourceId` · `Title` · `Start` / `End` (`AgendaInstant`) · `IsAllDay` · `Status` · `Priority` ·
`OwnerEmployeeId` / `OwnerName` · `SourceModule` · `EntityCode` · `DeepLink` · `IsOverdue` · `IsCompleted` ·
`IsRedacted` · `TimeZoneId` · `SortKeyUtc`.

It carries **no** task description, **no** event description and **no** attendee list.

## 5. Behaviour

- **Company and employee isolation** — both refused when unresolved, with no fallback.
- **Timezone explicit** — a missing zone raises `TimeZoneUnresolvedException` rather than guessing.
- **Date-range filtering** — the UTC window covers the whole of the last local day, so an event at 23:30 on the
  final day is not silently dropped.
- **Deterministic ordering** — `SortKeyUtc`, then `ItemType`, then `SourceId`. Every tie is broken, so page 2 cannot
  repeat a row from page 1.
- **Stable paging** — capped at 200 per page.
- **Task scope is never widened** — "mine" unless an explicit assignee is requested.
- **Private events are redacted, not hidden** — somebody else's `Personal` event shows as *مشغول (Busy)* with its
  title and owner withheld. The slot is still shown: "busy at 09:00" is the useful half and is not private.
- **A failing source degrades visibly** — `DegradedSources` names it. An agenda quietly missing its tasks is worse
  than one that says so.

## 6. Handover to the Workspace owner (TAB 3)

The shell already consumes `ITaskService` for My Work. This service is the calendar half, in the same shape.

**What is asked:** resolve `IWorkspaceAgendaService` and call `GetAgendaAsync`, supplying the signed-in employee's
company, employee id and timezone.

**What must not happen:** the shell must not query `CalendarEvents` directly — that would re-implement the
visibility rule in a second place.

No route, controller action or view was added by this tab.

## 7. Tests

`TasksCalendarAgendaTests` — 16 tests: combines both sources in one ordered list; a task stays a task and is never
turned into an event; nothing appears twice; range filtering; last-day inclusion; all-day keeps its date and carries
no offset; timed items carry an offset; someone else's personal event is redacted while mine is not; task scope
never widened; deterministic ordering on an equal instant; stable paging; completed tasks excluded unless asked for;
unresolved company and unresolved employee both refused; missing timezone refused; and a failing source degrading
visibly.
