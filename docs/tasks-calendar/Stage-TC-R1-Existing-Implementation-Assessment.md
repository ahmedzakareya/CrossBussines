# Stage-TC-R1 — Existing Implementation Assessment

**Product:** CrossBusiness Platform · **Increment:** Tasks & Calendar Integration · **Tab:** FOURTH
**Rule for this increment:** Tasks and Calendar are **not greenfield**. Nothing is rebuilt. This document
establishes what exists before anything is designed on top of it.

---

## 1. Headline

**Tasks is a mature module. Calendar is a small one. Neither is integrated with the platform, and they are not
integrated with each other.**

Tasks has been built out across nine phases (TM-1 … TM-9): a core entity, a polymorphic link resolved through the
platform's own entity registry, timesheets, labour posting, billing, rule-driven auto-generation, and a scheduled-task
matcher with two background workers. Calendar is a single entity with attendees, a 131-line service, one screen and a
reminders strip embedded in four shared layouts.

What neither has: **a registry entry, a business event, or any knowledge of the other.**

## 2. Tasks — what exists

### 2.1 Entities

| Entity | File | Notes |
|---|---|---|
| `TaskItem` | `Models/Context/Tasks/TaskItem.cs` (47 lines) | Title(+En), assignee, creator, priority, `DueDate`, status, estimated/actual hours, `ProgressPct`, free-text `Category`; **TM-2** polymorphic link `EntityType`/`EntityId`; **TM-4** `LaborPostedAt`; **TM-5** `IsBillable`/`BillRate`/`CustomerId`; **TM-9** `IsScheduled`, `ExpectedEntityType`, `ExpectedPartyType/Id`, `ExpectedFrom/To`, `MatchedAt` |
| `TimesheetEntry` | `…/TimesheetEntry.cs` | hours per employee per task |
| `TaskAutoRule` / `TaskAutoLog` | `…/TaskAutomation.cs` | TM-7 rules + the per-`(rule, source)` duplicate guard |
| `TaskMatchSuggestion` | `…/TaskMatchSuggestion.cs` | TM-9-ج candidates awaiting a manager |

**No `ProjectId` column.** A task reaches a project through the generic `EntityType = "Project"` link
(`TaskItem.cs:24-25`) — which is how `ProjectLaborService` finds project labour today.

### 2.2 Services (11)

`TaskService` (302) · `TasksAccessService` (239) · `TaskReportService` (145) · `TaskScheduleMatcher` (150) ·
`TaskGeneratorService` (142) · `TaskBillingService` (83) · `TaskCostService` (84) · `TaskLinkResolver` (76) ·
`TimesheetService` (170) · plus two hosted workers: `TaskGeneratorHostedService` (50) and
`TaskScheduleMatchHostedService` (49), both registered in `Program.cs:388-389`.

**Already platform-integrated in one direction.** `TaskLinkResolver` is explicitly *"a COMPATIBILITY WRAPPER"* over
`IEntityRegistry` (`BL/TaskLinkResolver.cs:10-13`): the type table and every search/resolve query moved to the
kernel. So Tasks already **consumes** the registry — it simply is not **in** it.

### 2.3 Automation, and what it is not

- **TM-7 `TaskGeneratorService`** scans system state against five **fixed** rule types —
  `LowStock`, `OverdueInvoice`, `WorkOrderQc`, `DeliveryReady`, `NewEmployeeOnboard`
  (`BL/TaskGeneratorService.cs:29-33`) — and creates a task, guarded against duplicates by a `RuleKey`.
- **TM-9 `TaskScheduleMatcher`** takes tasks flagged `IsScheduled` with expected movement criteria and, when a
  matching purchase/sales invoice appears, either auto-links it (exactly one candidate) or records suggestions for a
  manager (more than one).

Both are valuable and both are **event/state-driven, not time-driven**. Despite the name, a "scheduled task" in TM-9
is a task *waiting for a movement*, not a task on a schedule. **There is no recurrence anywhere in Tasks.**

### 2.4 Authorization

`TasksAccessService` (FIRST TAB, Stage 1 Batch C) — 8 actions (`read`, `create`, `edit`, `assign`, `reassign`,
`complete`, `reopen`, `manage`) and 3 roles (`TasksAdministrator`, `TasksSupervisor`, `TasksViewer`).

**Enforcement is thin at the controller.** `Controllers/TasksController.cs` references `TasksActions` **3 times**
against those 8 actions, and carries `private const int DefaultCompanyId = 1` (line 17). Same pattern this tab
already reported for `ProjectController` (CR-09) — **referred to the first tab, not fixed here** (gap TC-G-12).

## 3. Calendar — what exists

| Element | File | Notes |
|---|---|---|
| `CalendarEvent` | `Models/Context/Calendar/CalendarEvent.cs` (26 lines) | Title, description, location, `AllDay`, `StartAt`/`EndAt`, `Scope` (`Personal`\|`Company`), `OwnerEmpId`, soft-delete `DeletedAt` |
| `CalendarEventAttendee` | same file | `EventId` + `EmployeeId` — **and nothing else** |
| `CalendarService` | `BL/CalendarService.cs` (131 lines) | list/get/save/delete + a FullCalendar DTO |
| `CalendarController` | `Controllers/CalendarController.cs` | `[SessionValidation]`, uses `IStringLocalizer` and `INotificationService` |
| `_CalendarReminders.cshtml` | `Views/Shared/` | embedded in `_LayoutAccounting`, `_LayoutBackend`, `_LayoutInventory`, `_LayoutManufacturing` |

**Visibility model** (`CalendarService.cs:39-45`): I see an event if I own it, or its scope is `Company`, or I am an
attendee. Clean and correct.

**Three behaviours that matter for integration:**

1. **Only the owner may edit or delete** (`:92`, `:122`). No delegation, no organiser rights for an assistant.
2. **Attendees are replaced by delete-and-reinsert on every save** (`:110-113`). Attendee rows therefore have no
   stable identity — so an accept/decline response cannot be added later without being wiped by the next save. This
   is the same identity anti-pattern this tab remediated as CR-01 in the construction track, and it must be fixed
   **before** any response status is introduced, not after (gap TC-G-08).
3. **Time is stored and emitted local, deliberately.** `SaveAsync` writes `DateTime.Now`, and `Iso()` at line 129
   formats without a zone suffix with the comment *"FullCalendar reads it as-is (matches the browser-entered
   value)"*. That is a coherent choice for a single-timezone company and an incorrect one the moment a task due date
   (written in UTC) is merged onto the same surface (gap TC-G-06).

**Ownership note.** Calendar belongs to the parallel team's feature-module set (Comm / Calendar / Library /
Announcements / DocComments / FileManager) and follows their conventions, not the TM conventions. Taking it over is
decision **TC-D-01**.

## 4. SQL — reviewed

| Script | Lines | FK | CHECK | ROWVERSION | UNIQUE |
|---|---|---|---|---|---|
| `tasks_tm1.sql` | 24 | 0 | 0 | 0 | 0 |
| `tasks_tm2_link.sql` | 8 | 0 | 0 | 0 | 0 |
| `tasks_tm3_timesheet.sql` | 24 | 0 | 0 | 0 | 0 |
| `tasks_tm4_labor.sql` | 3 | 0 | 0 | 0 | 0 |
| `tasks_tm5_billing.sql` | 9 | 0 | 0 | 0 | 0 |
| `tasks_tm7_automation.sql` | 26 | 0 | 0 | 0 | 2 |
| `tasks_tm9_scheduled.sql` | 17 | 0 | 0 | 0 | 0 |
| `tasks_tm9b_suggestions.sql` | 17 | 0 | 0 | 0 | 1 |
| `tasks_category.sql` / `tasks_progress.sql` | 5 / 3 | 0 | 0 | 0 | 0 |
| `calendar.sql` | — | 0 | 0 | 0 | 0 |

**Totals: 0 foreign keys, 0 CHECK constraints, 0 concurrency tokens, 3 unique indexes.** Company/entity isolation is
enforced entirely in service predicates. Identical to the pre-C1 construction schema, and the same remedy applies
(gap TC-G-11).

## 5. Platform integration — the actual state

| Integration point | Tasks | Calendar |
|---|---|---|
| Registered in `EntityRegistry` | **No** | **No** |
| Consumes `EntityRegistry` | **Yes** (`TaskLinkResolver`) | No |
| Raises business events | **No** | **No** |
| Timeline | No | No |
| Comments / mentions | No | No |
| Files | No | No |
| Notifications | **None at all** | invite only, from the controller (`CalendarController.cs:104`) |
| Background workers | 2 (generator, matcher) | none |
| Cross-module link | via `EntityType`/`EntityId` | **none — no entity link exists** |

Direct `NotifyAsync` is **permitted** here: ADR-006 retains direct producers precisely for entities that are not
onboarded. So Calendar's invite notification is not a rule breach — it is the correct pattern for today's state, and
it becomes a projection only when the entity is onboarded (R3).

## 6. The 15 integration gaps

Full catalog with evidence, severity and owning phase:
`Stage-TC-Catalogs-Appendix.md` §2 and `tasks-calendar-gap-catalog.csv`.

Distribution: **3 Blockers · 4 High · 6 Medium · 3 Low** (16 gaps; TC-G-16 was added after the Workspace
shell appeared mid-increment).

The three blockers are the whole of the integration problem:

1. **TC-G-01** — neither entity is in the registry, so there is nothing for the timeline, comments or files to attach
   to, and no other record can link *to* a task.
2. **TC-G-02** — neither module raises an event, so nothing reaches the outbox however well it is registered.
3. **TC-G-03** — the two modules do not know each other exists.

Everything else is either a consequence of those three or a quality issue that integration would amplify.

## 7. What this assessment explicitly does not conclude

- It does **not** conclude that Tasks needs rebuilding. TM-1…TM-9 is coherent, and its polymorphic link is already
  routed through the kernel — better integrated than most of the modules around it.
- It does **not** treat Calendar's local-time choice as a bug in Calendar. It is a correct choice for a
  single-timezone company that becomes wrong only when merged with UTC-stamped task data — which is what this
  increment proposes to do, so the decision belongs to this increment (TC-D-03).
- It does **not** propose a Workspace shell. **One now exists**: another tab created `BL/Workspace/*` and
  `Controllers/WorkspaceController.cs` at 2026-08-06 10:30, *during* this increment, and it already consumes
  `ITaskService` for its My Work panel. R4 §5 was corrected to describe that, and the remaining workspace gap is
  Calendar, not the shell (TC-G-16, TC-D-02).
