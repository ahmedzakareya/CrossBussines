# Stage-TC-R4 — Timeline, Workspace and Communication Integration

---

## 1. The one change everything else waits on

Neither `TaskItem` nor `CalendarEvent` is in `BL/Platform/EntityRegistry.cs`. The registered codes are
`SalesInvoice`, `PurchaseInvoice`, `Quotation`, `JournalEntry`, `Customer`, `Supplier`, `ManufWorkOrder`, `PosOrder`,
`Employee`, `Project`, `Item`, `PlatformRoleAssignment`.

Everything in this document — timeline, comments, files, followers, the record picker, the workspace — attaches to a
registry entry. Without it there is nothing to attach to.

**The asymmetry worth naming:** Tasks already *consumes* the registry. `BL/TaskLinkResolver.cs` is, in its own
words, *"a COMPATIBILITY WRAPPER"* whose type table and every search/resolve query moved to `IEntityRegistry`. So a
task can point at a customer, but nothing can point at a task, and a task appears on no timeline.

## 2. Registry request (to the Platform Kernel)

This tab does not edit `EntityRegistry.cs`. It requests two definitions, with the flags it needs and the reason for
each:

### 2.1 `Task`

| Field | Requested value | Why |
|---|---|---|
| `Code` | `Task` | matches `TaskItem.EntityType` conventions already in use |
| `DisplayNameAr` / `En` | `مهمة` / `Task` | |
| `Module` | `Tasks` | |
| `RouteTemplate` | `/Tasks/Index` (detail route when a per-task screen exists) | today Tasks has a list screen, no per-id detail — same situation as `PosOrder`, which is registered reference-only |
| `SupportsSearch` | **true** | so other modules can link to a task |
| `SupportsTimeline` | **true** | the point of the integration |
| `SupportsComments` | **true** | a task is the most natural comment target in the product |
| `SupportsFiles` | **true** | attachments on a task are an obvious need |
| `SupportsFollowers` | **true** | a supervisor follows without being the assignee |
| `PermissionScope` | `Tasks` | `TasksAccessService` already exists with 8 actions — the scope must not be `None` |
| `ListedInRecordPicker` | **true** | task-to-task linking, and construction activities later |

### 2.2 `CalendarEvent`

| Field | Requested value | Why |
|---|---|---|
| `Code` | `CalendarEvent` | |
| `DisplayNameAr` / `En` | `حدث` / `Calendar event` | |
| `Module` | `Calendar` | |
| `RouteTemplate` | `/Calendar/Index` | one screen today |
| `SupportsSearch` | true | |
| `SupportsTimeline` | **true** | reschedule/cancel history is exactly what people argue about |
| `SupportsComments` | true | |
| `SupportsFiles` | true | agendas and minutes |
| `SupportsFollowers` | false | attendees already are the follower set — a second concept would diverge |
| `PermissionScope` | **decision required** | Calendar has **no** access service. Either a new `Calendar` scope (a request to the FIRST TAB) or reuse of the owner/attendee visibility already in `CalendarService.Visible()`. Recommendation: keep the existing visibility rule as the record-level check and add a module scope only when a real administrative need appears. |
| `ListedInRecordPicker` | true | |

**Note on `PermissionScope`.** `Project` is currently registered with `PermissionScope = ScopeNone` even though
`ProjectsAccessService` exists — a gap this tab reported in the construction track. The `Task` request above must
not repeat it: `TasksAccessService` exists, so the scope must be `Tasks`.

## 3. Timeline integration (THIRD TAB consumes)

**What this tab supplies:** the events and payloads in R3, plus a presenter contract per event — the one-line
rendering, the icon, and which payload fields are safe to show.

**What this tab does not write:** `TimelineProjectionConsumer`, `TimelineProjectionService`, `TimelineEventPresenter`
or any adapter. Those are the third tab's.

**Two timeline directions, and both are needed:**

1. **Timeline *of* a task** — its own history: created, assigned, status changes, time logged, completed.
2. **Task activity *on another record's* timeline** — "a task was created on this invoice", visible on the invoice.
   This is what makes tasks useful across the product, and it works because `TaskItem.EntityType`/`EntityId` already
   names the other record. The projection can therefore write to **two** aggregates from one event: the task, and
   the linked entity.

That second direction is the highest-value piece of the whole integration and it costs one extra projection rule,
not a new mechanism.

**Legacy adapters exist as a precedent.** `CustomerLegacyTimelineAdapter`, `SalesInvoiceLegacyTimelineAdapter`,
`PurchaseInvoiceLegacyTimelineAdapter` and `ManufWorkOrderLegacyTimelineAdapter` already forward pre-kernel history
into the timeline. Tasks has ~9 phases of history in its own tables (status changes are **not** currently recorded —
see TC-G-13), so a Tasks adapter can only surface what exists: creation, completion, timesheet entries and TM-9
matches. **Historic status changes cannot be recovered because they were never stored.** Stated so nobody promises
a full retrospective timeline.

## 4. Communication integration

| Capability | Mechanism | Depends on |
|---|---|---|
| Comments on a task / event | third tab's `DocComments` against the registry code | registry entry (§2) |
| Mentions inside those comments | third tab's mention pipeline | registry entry |
| Notifications | R3 §4 — Stage 1 direct, Stage 2 projection | nothing for Stage 1 |
| Files on a task / event | platform file store (`LibraryItem`) + a metadata row referencing it | registry entry |
| Followers on a task | third tab's follower set | registry entry |

**No construction of a parallel mechanism.** Tasks and Calendar get comments, mentions, files and followers by being
registered — not by growing their own. That is the entire reason §2 is the first request.

## 5. Workspace integration — the shell now exists

> **Correction, recorded rather than quietly folded in.** An earlier draft of this section stated that no workspace
> surface existed. That was true when R1 was written and **is no longer true**: another tab created
> `BL/Workspace/WorkspaceContracts.cs` (260), `WorkspaceService.cs` (587), `WorkspaceNavigation.cs` (151),
> `WorkspaceRegistration.cs` (34) and `Controllers/WorkspaceController.cs` (61) at **2026-08-06 10:30**, during this
> increment. The section below describes the tree as it is now, and **TC-D-02 has been rewritten** accordingly.

### 5.1 What the shell already does

- `WorkspaceService` **already consumes `ITaskService`** for its My Work panel, with the comment
  *"MY WORK — consumes ITaskService. No task rule is re-implemented here."* (`WorkspaceService.cs:43,164`).
  That is exactly the right pattern: the shell calls the module's service instead of re-querying `TaskItems`, so no
  task rule is duplicated.
- It reads the platform `Notifications` table and mentions, and exposes panels with an explicit
  `WorkspacePanelState` (so a failing source degrades rather than breaking the page).
- `WorkspaceController` is `[SessionValidation]` and resolves identity from `BusinessContext`, **not** from a
  request value or a hardcoded company.

**So Tasks → Workspace integration already works, and TAB 4 does not rebuild any of it.**

### 5.2 What is missing — and it is Calendar

`WorkspaceService` injects `ITaskService` but **not `ICalendarService`**, and there is no agenda or events panel
(gap **TC-G-16**). "My day" therefore shows work and not meetings.

**The contribution TAB 4 owes the shell** (decision TC-D-02, option a — mirror the Tasks pattern):

**(a) An agenda read on `ICalendarService`** — a time-ordered range for the signed-in employee:

```
CalendarAgendaItem: EventId, Title, StartAt, EndAt, AllDay, Scope, Location,
                    IsOwner, AttendeeCount, Url
```

Implemented **inside `CalendarService`**, so the existing visibility rule (`Visible()` — owner ∪ company-scope ∪
attendee, `CalendarService.cs:39-45`) stays in one place. The shell calls it; the shell must **not** query
`CalendarEvents` directly, or that rule would exist twice and drift.

**(b) A unified agenda that also carries task due dates** — decision **TC-D-05**'s recommended shape: a **read
union**, not materialised events (§5.3).

**(c) A counter for the nav badge** — today's events, cheap enough for every page render, alongside the task counts
the shell already has.

### 5.3 Why a union view and not materialised events

### 5.2 Why a union view and not materialised events

Materialising a `CalendarEvent` per task due date creates a second copy of a date that must then be kept in step
forever — and the two will diverge the first time a due date is changed by a path that forgets. This tab has already
remediated that exact defect class twice in the construction track (BOQ identity, in-place rate overwrite). A read
union has no second copy to drift.

**The consequence must be honoured in the UI:** on an agenda, a task is **not draggable** as if it were an event.
Moving it must go through `TaskService` and change the task's own due date, or not be offered.

### 5.4 What the workspace must not become

- Not a second task store. It reads; it writes nothing.
- Not a permission bypass. Every source applies its own module's checks.
- Not a place where a task's status can be changed without `TasksAccessService` deciding.

## 6. Prerequisites, in order

| # | Prerequisite | Owner | Blocks |
|---|---|---|---|
| 1 | Register `Task` and `CalendarEvent` | **Platform Kernel** | timeline, comments, files, followers, picker |
| 2 | Decide `CalendarEvent.PermissionScope` | **FIRST TAB** + TAB 4 | §2.2 |
| 3 | Raise the R3 events | TAB 4, after 1 | timeline |
| 4 | Timeline projection + presenters | **THIRD TAB** | timeline rendering |
| 5 | Calendar agenda source on `ICalendarService`, called by the existing shell | TAB 4 + shell author (**TC-D-02**) | the workspace agenda panel |
| 6 | Add DB integrity to tasks/calendar tables (TC-G-11) | TAB 4 | not a blocker; raises the cost of a bad row once surfaced |

## 7. What R4 does not do

No registry edit, no consumer, no presenter, no workspace code, no controller change — the workspace shell that now exists was neither modified nor read into by this tab beyond assessing it. Every item in §6 that belongs to
another tab is written as a request with the exact shape required, so it can be accepted or refused on its merits.
