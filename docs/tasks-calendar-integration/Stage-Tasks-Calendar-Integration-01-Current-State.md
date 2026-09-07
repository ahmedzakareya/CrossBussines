# Stage-Tasks-Calendar-Integration-01 — Current State

**Product:** CrossBusiness Platform · **Tab:** TAB 4 — Tasks & Calendar Integration
**Increment:** first safe integration increment · **Date:** 2026-08-06

---

## 1. Neither module is greenfield, and neither was rebuilt

| | Tasks | Calendar |
|---|---|---|
| Phases built | TM-1 … TM-9 | one slice |
| Entities | `TaskItem`, `TimesheetEntry`, `TaskAutoRule`/`TaskAutoLog`, `TaskMatchSuggestion` | `CalendarEvent`, `CalendarEventAttendee` |
| Services | 11 (`TaskService` 302 lines, `TasksAccessService` 239, `TaskScheduleMatcher` 150, `TaskGeneratorService` 142, …) | 1 (`CalendarService`, 131 lines) |
| Hosted workers | 2 — `TaskGeneratorHostedService`, `TaskScheduleMatchHostedService` | none |
| SQL scripts | 11 | 1 |
| Screens | 5 views | 1 view + `_CalendarReminders` in 4 shared layouts |
| Access service | `TasksAccessService` — 8 actions, 3 roles | **none** (record-level rule inside `CalendarService.Visible()`) |

**Nothing in either module was rebuilt, replaced or redesigned in this increment.** `TaskService`,
`CalendarService`, `TaskScheduleMatcher`, `TaskGeneratorService`, `TasksAccessService` and every view are byte-identical
to before it. The only pre-existing file changed is `TaskGeneratorHostedService`, and only to host the overdue sweep
(§4 of the delivery report explains why that file and no other).

## 2. The blockers named in the brief, and where each now stands

| # | Blocker | State after this increment |
|---|---|---|
| 1 | `Task` not registered in `IEntityRegistry` | **Contract delivered, externally pending.** `TaskCalendarRegistryOnboarding.TaskRequest()` builds the kernel's own `EntityDefinition` plus the policy metadata. The registry is Platform property; this tab did not edit it. |
| 2 | `CalendarEvent` not registered | **Contract delivered, externally pending** — same mechanism. |
| 3 | Task raises no business events | **9 versioned contracts defined**, none raised. Reason in §3. |
| 4 | Calendar raises no business events | **9 versioned contracts defined**, none raised. |
| 5 | No shared integration contract | **Delivered**: `IWorkspaceAgendaService` (read union) + the shared time model. |
| 6 | Tasks use UTC | Preserved. The seam treats a task due value as UTC and says so. |
| 7 | Calendar uses `DateTime.Now`, ISO without offset | **Unchanged in Calendar** — deliberately. The new seam converts; the legacy re-stamp is a measured migration, not a silent rewrite. |
| 8 | Workspace consumes `ITaskService` but not `ICalendarService` | **Agenda service delivered** and ready for the Workspace owner to call. No Workspace file was touched. |
| 9 | Shared build may be blocked by another tab | **It was, twice, and both times it was theirs.** See the delivery report §2. |

## 3. Why events are defined and not raised

`RecordAsync` must run inside the caller's ambient transaction immediately before commit **with no swallowing
catch**, and it throws without one. Raising `Task.Created` would therefore make the event kernel a hard runtime
dependency of creating a task: a missing or changed `BusinessEvents` table would stop people creating tasks.

A task with no timeline entry is a much smaller problem than a task that cannot be created.

There is also a hard ordering constraint: an event name is validated against the registry, and neither entity code is
registered yet (blockers 1 and 2). **Contracts first, raising after onboarding** is not caution — it is the only
order that works.

## 4. What was NOT touched, and who owns it

| Area | Owner | This increment |
|---|---|---|
| `BL/Platform/EntityRegistry.cs` | Platform Kernel | onboarding **request** only |
| `BL/Workspace/*`, `Controllers/WorkspaceController.cs`, `Views/Workspace/*` | TAB 3 | **not modified**; the agenda service is offered to it |
| `BL/Communication/*`, `BL/Comm/*`, `NotificationService` internals | TAB 3 | consumed through `INotificationService` only |
| `BL/Reporting/*`, `Controllers/Api/ReportsCenterApiController.cs` | TAB 2 | **not modified** |
| `BL/TasksAccessService.cs` | TAB 1 | consumed, not modified |
| `BL/CalendarService.cs`, `Controllers/CalendarController.cs`, `Views/Calendar/*` | pending handover (TCI-D-02) | **not modified** |
| Construction, Accounting, Inventory, CRM | other tracks | untouched |

## 5. Known conditions carried forward

- **`TasksController` gating.** 3 references to `TasksActions` against 8 defined actions, and
  `private const int DefaultCompanyId = 1`. TAB 1's area; reported, not changed.
- **Calendar attendee delete-and-reinsert.** `CalendarService.SaveAsync` replaces all attendee rows on every save,
  so attendee rows have no stable identity. Any accept/decline response must wait for that to be fixed first —
  otherwise the first save after a response destroys it.
- **Tasks/Calendar SQL integrity.** Across 11 task scripts and `calendar.sql`: 0 foreign keys, 0 CHECK constraints,
  0 concurrency tokens, 3 unique indexes.
- **`Task.Cancelled` has no status to fire on.** `TaskItem.Status` is `New | InProgress | Done`. The event contract
  and the notification path exist; raising it needs the status vocabulary to gain the value first, which is a
  behaviour change this increment did not make.
