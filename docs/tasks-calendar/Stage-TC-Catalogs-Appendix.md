# Stage-TC — Catalogs Appendix (generated)

> **Generated file — do not edit by hand.**
> Source of truth: `docs/tasks-calendar/_generator/generate_tc_catalogs.py`.
> Regenerate with `python docs/tasks-calendar/_generator/generate_tc_catalogs.py`.
> The paired CSVs in this folder are emitted from the same dataset in the same run.

**Product:** CrossBusiness Platform · **Increment:** Tasks & Calendar Integration (R1–R5) · **Tab:** FOURTH

These four catalogs are the data behind R1–R5. Each is also emitted as a CSV in this folder.

## 1. Ownership matrix (17 areas) — R2

| Area | BuiltBy | ProposedOwner | Status | Evidence | HandoverNote |
|---|---|---|---|---|---|
| Task entity + lifecycle | TM series (this tab's lineage) | TAB 4 — Tasks & Calendar | Confirmed | Models/Context/Tasks/TaskItem.cs; BL/TaskService.cs (302 lines) | No handover needed — already this tab's lineage (TM-1..TM-9). |
| Task timesheet + hours | TM-3 | TAB 4 | Confirmed | Models/Context/Tasks/TimesheetEntry.cs; BL/TimesheetService.cs | — |
| Task labor posting to work orders | TM-4 | TAB 4 (orchestrates), Manufacturing (writer) | Confirmed | BL/TaskCostService.cs; ManufService.AddLaborAsync | Posting stays with the existing writer; Tasks only marks LaborPostedAt. |
| Task billing | TM-5 | TAB 4 (orchestrates), Accounting (writer) | Confirmed | BL/TaskBillingService.cs → ReceivableService | Posting stays in ReceivableService. No new writer. |
| Task auto-generation rules | TM-7 | TAB 4 | Confirmed | Models/Context/Tasks/TaskAutomation.cs; BL/TaskGeneratorService.cs; BL/TaskGeneratorHostedService.cs | — |
| Scheduled-task movement matching | TM-9 | TAB 4 | Confirmed | BL/TaskScheduleMatcher.cs; BL/TaskScheduleMatchHostedService.cs; Models/Context/Tasks/TaskMatchSuggestion.cs | — |
| Task authorization | Stage 1 Batch C (FIRST TAB) | FIRST TAB | Unchanged — do not modify | BL/TasksAccessService.cs (239 lines, 8 actions, 3 roles) | TAB 4 consumes it; any new action is a REQUEST to the first tab. |
| Calendar entity + service | Parallel feature-module team (Comm/Calendar/Library/Announcements set) | TAB 4 — Tasks & Calendar | **HANDOVER REQUIRED** | Models/Context/Calendar/CalendarEvent.cs; BL/CalendarService.cs (131 lines); Controllers/CalendarController.cs | Built under the feature-module conventions (co-located interface+impl, bilingual literals). Taking it over needs an explicit owner decision — recorded as TC-D-01. |
| Calendar reminders surface | Parallel feature-module team | TAB 4 | **HANDOVER REQUIRED** | Views/Shared/_CalendarReminders.cshtml embedded in _LayoutAccounting/_LayoutBackend/_LayoutInventory/_LayoutManufacturing | Renders on four shared layouts. Any change is visible platform-wide — coordinate before touching. |
| Entity registry (the link target table) | Platform Kernel | Platform Kernel | Unchanged — request only | BL/Platform/EntityRegistry.cs | Registering Task/CalendarEvent is a kernel change TAB 4 must REQUEST, not make. |
| Business event kernel + outbox | Platform Kernel | Platform Kernel | Unchanged | BL/Platform/BusinessEventService.cs; BusinessEventDispatchWorker.cs | TAB 4 defines events; the kernel transports them. |
| Timeline projection | THIRD TAB — Communication | THIRD TAB | Unchanged — request only | BL/Platform/TimelineProjectionConsumer.cs; TimelineProjectionService.cs | TAB 4 supplies payloads; the third tab renders and consumes. |
| Comments / mentions / notifications infrastructure | THIRD TAB — Communication | THIRD TAB | Unchanged — request only | BL/Communication/*; BL/Comm/*; BL/NotificationService.cs | Direct NotifyAsync stays permitted while Task/CalendarEvent are NOT onboarded (ADR-006 retained producers). |
| Workspace shell | ANOTHER TAB (created 2026-08-06 10:30, during this increment) | Its author | Exists — do not modify | BL/Workspace/WorkspaceContracts.cs (260), WorkspaceService.cs (587), WorkspaceNavigation.cs (151), WorkspaceRegistration.cs (34), Controllers/WorkspaceController.cs (61) | Already consumes ITaskService for the My Work panel and reads Notifications/mentions. TAB 4 does NOT rebuild it; TAB 4 contributes a Calendar/agenda source to it. |
| Workspace: Tasks contribution | Workspace author (consuming ITaskService) | Shared — Workspace shell calls TAB 4's service | Working today | BL/Workspace/WorkspaceService.cs:43,164 — 'MY WORK - consumes ITaskService. No task rule is re-implemented here.' | The correct pattern and already in place: the shell calls the module's service instead of re-querying TaskItems. |
| Workspace: Calendar contribution | nobody | TAB 4 | **MISSING** | BL/Workspace/WorkspaceService.cs injects ITaskService but NOT ICalendarService; there is no agenda or events panel | This is the real remaining workspace gap and it is TAB 4's to supply. |
| Notification delivery + hub | Platform / THIRD TAB | unchanged | Unchanged | BL/NotificationService.cs; Hubs/NotificationsHub.cs | Calendar already pushes through it from the controller. |

## 2. Integration gap catalog (16 gaps) — R1

| Severity | Count |
|---|---|
| Blocker | 3 |
| High | 5 |
| Medium | 5 |
| Low | 3 |

Severity here means **integration risk**, not module quality. Tasks in particular is a mature module
(TM-1 through TM-9, 11 SQL scripts, 11 services, two hosted workers); its gaps are gaps *as an
integration target*, not defects in what it does today.

| ID | Severity | Gap | Evidence | WhyItBlocksIntegration | Phase | Owner |
|---|---|---|---|---|---|---|
| TC-G-01 | Blocker | Neither TaskItem nor CalendarEvent is registered in EntityRegistry | BL/Platform/EntityRegistry.cs — the codes are SalesInvoice, PurchaseInvoice, Quotation, JournalEntry, Customer, Supplier, ManufWorkOrder, PosOrder, Employee, Project, Item, PlatformRoleAssignment. No Task, no CalendarEvent. | No timeline, no comments, no files, no followers, and no other record can link TO a task. Tasks CONSUME the registry (BL/TaskLinkResolver.cs is a wrapper over IEntityRegistry) but are absent FROM it — the asymmetry is the single biggest blocker. | R4 | Kernel change — REQUEST to Platform |
| TC-G-02 | Blocker | Neither module raises a business event | grep for RecordAsync / IBusinessEventService across BL/Task*.cs and BL/CalendarService.cs returns nothing. | Nothing from Tasks or Calendar reaches the outbox, so the timeline and the notification projection can never see them however well they are registered. | R3 | TAB 4 defines; kernel transports |
| TC-G-03 | Blocker | Tasks and Calendar are completely disconnected from each other | TaskItem has DueDate but no calendar reference; CalendarEvent has no EntityType/EntityId and no TaskId. Neither service references the other. | A task's due date never appears on a calendar, and an event cannot carry or create a task. This is the integration the increment exists to enable. | R5 | TAB 4 |
| TC-G-04 | High | Tasks raise NO notifications at all | grep NotifyAsync/NotifyRoleAsync across Controllers/TasksController.cs, BL/TaskService.cs, BL/TaskGeneratorService.cs, BL/TaskScheduleMatcher.cs — no match. | Assignment, reassignment, due-soon, overdue, completion and auto-generation are all silent. A task assigned to you today notifies you never. | R3 | TAB 4 |
| TC-G-05 | High | Calendar notifies only on invite, and only from the controller | Controllers/CalendarController.cs:104 NotifyAsync on invite. BL/CalendarService.cs contains no notification at all. | Reschedule, cancellation and reminders are silent, and the one notification that exists sits in the controller, so any non-controller caller skips it. | R3 | TAB 4 |
| TC-G-06 | High | No time-zone model; the two modules disagree about time | BL/CalendarService.cs writes DateTime.Now and emits ISO WITHOUT an offset (Iso() at line 129, deliberately, so FullCalendar reads it as entered). TaskItem.CreatedAt is DateTime.UtcNow while DueDate is a bare local value. | Merging a task due date and a calendar event onto one surface will place them at different real instants for anyone not in the server's zone. | R5 | TAB 4 — needs decision TC-D-03 |
| TC-G-07 | High | No recurrence anywhere in either module | TaskAutoRule.RuleType is a FIXED set of five system-state rules (LowStock, OverdueInvoice, WorkOrderQc, DeliveryReady, NewEmployeeOnboard, BL/TaskGeneratorService.cs:29-33). CalendarEvent has no RRULE, no series and no exception dates. | TM-7 is rule-driven creation and TM-9 is movement matching; NEITHER is a schedule. 'Every Sunday' cannot be expressed for a task or an event. | R5 | TAB 4 |
| TC-G-08 | Medium | Calendar attendees are replaced by delete-and-reinsert on every save | BL/CalendarService.cs:110-113 — RemoveRange(existing) then re-add. | Attendee rows have no stable identity, so a response status (accepted/declined/tentative) cannot be added later without losing it on the next save. The same identity anti-pattern this tab already remediated as CR-01 in the construction track. | R5 | TAB 4 |
| TC-G-09 | Medium | Calendar has no attendee response status and no organiser delegation | Models/Context/Calendar/CalendarEvent.cs — CalendarEventAttendee carries only EventId and EmployeeId. BL/CalendarService.cs:92,122 — only the owner may edit or delete. | An invitation cannot be accepted or declined, and an assistant cannot manage an executive's calendar. | R5 | TAB 4 |
| TC-G-10 | Medium | No external calendar synchronisation of any kind | No ICS/iCal export, no CalDAV, no Exchange/Graph or Google client anywhere in BL/. | 'Calendar synchronization' has no existing mechanism to extend — it is new work, and its direction (one-way publish vs two-way sync) is an unmade decision. | R5 | TAB 4 — needs decision TC-D-04 |
| TC-G-11 | Medium | Tasks and Calendar SQL carries almost no database integrity | Across the 11 tasks_*.sql scripts and calendar.sql: 0 FOREIGN KEY, 0 CHECK, 0 ROWVERSION, 3 UNIQUE in total. | Integration multiplies the cost of a bad row: a task linked to a deleted entity, or an attendee row pointing at nobody, becomes visible on the timeline and the workspace. | R4 | TAB 4 |
| TC-G-12 | Medium | TasksController is largely ungated and hardcodes company 1 | Controllers/TasksController.cs:17 DefaultCompanyId = 1; only 3 references to TasksActions in the whole controller, while TasksAccessService defines 8 actions. | A workspace that surfaces tasks across modules would widen the blast radius of an unenforced check. | R4 | FIRST TAB — referred, not fixed here |
| TC-G-13 | Low | Task status is a free string with no state machine | TaskItem.Status — New \| InProgress \| Done, with 'Overdue' derived. No transition table; ChangeStatusAsync validates the target value only. | A timeline that shows 'status changed' needs to know which transitions are legal to render them meaningfully. | R3 | TAB 4 |
| TC-G-14 | Low | No task dependencies, no subtasks, no checklist | TaskItem has no ParentId and no predecessor/successor relation. | A workspace cannot show a blocked task, and construction's future WBS activities cannot hang scheduling off tasks. | R5 | TAB 4 — deferred beyond R5 |
| TC-G-16 | High | Calendar is absent from the Workspace shell that now exists | BL/Workspace/WorkspaceService.cs injects ITaskService (line 43) and states 'MY WORK - consumes ITaskService. No task rule is re-implemented here.' (line 164). It injects no ICalendarService and exposes no agenda panel. | The workspace surface this increment was preparing contracts for already exists and already shows tasks. Calendar is the missing half, so 'my day' shows work but not meetings. | R4 | TAB 4 supplies; shell author calls |
| TC-G-15 | Low | Calendar event has no category, colour or entity link for the workspace | CalendarEvent carries Scope (Personal\|Company) and nothing else classifiable. | The workspace cannot filter 'show me only site inspections' without a classification. | R4 | TAB 4 |

## 3. Proposed event catalog (20 events) — R3

**Nothing here is raised in this increment.** These are proposals that already obey the kernel's
mandatory contract, so adopting them later is a wiring change rather than a redesign.

| EventName | Version | Aggregate | EntityId | Actor | Payload | SensitiveFields | ProposedConsumers |
|---|---|---|---|---|---|---|---|
| Task.Created | v1 | TaskItem | TaskId | creator | title, assignee, priority, dueDate, linkedEntity | - | Timeline, Notification(assignee) |
| Task.Assigned | v1 | TaskItem | TaskId | assigner | assignee, previousAssignee, dueDate | - | Notification(new assignee), Timeline |
| Task.Reassigned | v1 | TaskItem | TaskId | assigner | assignee, previousAssignee, reason | - | Notification(both), Timeline |
| Task.StatusChanged | v1 | TaskItem | TaskId | actor | fromStatus, toStatus, progressPct | - | Timeline |
| Task.Completed | v1 | TaskItem | TaskId | actor | completedAt, actualHours | billRate | Notification(creator), Timeline |
| Task.Reopened | v1 | TaskItem | TaskId | actor | reason | - | Notification(assignee), Timeline |
| Task.DueSoon | v1 | TaskItem | TaskId | system | dueDate, hoursRemaining | - | Notification(assignee) |
| Task.Overdue | v1 | TaskItem | TaskId | system | dueDate, daysOverdue | - | Notification(assignee + supervisor) |
| Task.AutoGenerated | v1 | TaskItem | TaskId | system | ruleType, sourceEntity, ruleKey | - | Timeline, Notification(default assignee) |
| Task.LinkMatched | v1 | TaskItem | TaskId | system | expectedType, matchedEntity, matchedAt | - | Timeline, Notification(assignee) |
| Task.LinkSuggested | v1 | TaskItem | TaskId | system | candidateCount | - | Notification(supervisor) |
| Task.TimeLogged | v1 | TimesheetEntry | TimesheetEntryId | actor | taskId, hours, workDate | costRate | Timeline |
| CalendarEvent.Created | v1 | CalendarEvent | EventId | owner | title, startAt, endAt, scope, attendeeCount | - | Timeline, Notification(attendees) |
| CalendarEvent.Updated | v1 | CalendarEvent | EventId | owner | changedFields, startAt, endAt | - | Notification(attendees), Timeline |
| CalendarEvent.Rescheduled | v1 | CalendarEvent | EventId | owner | previousStartAt, startAt, previousEndAt, endAt | - | Notification(attendees), Timeline |
| CalendarEvent.Cancelled | v1 | CalendarEvent | EventId | owner | reason | - | Notification(attendees), Timeline |
| CalendarEvent.AttendeeInvited | v1 | CalendarEvent | EventId | owner | attendeeId | - | Notification(attendee) |
| CalendarEvent.AttendeeResponded | v1 | CalendarEvent | EventId | attendee | response (Accepted\|Declined\|Tentative) | - | Notification(owner), Timeline |
| CalendarEvent.ReminderDue | v1 | CalendarEvent | EventId | system | minutesBefore, startAt | - | Notification(attendees) |
| TaskCalendarLink.Created | v1 | TaskItem | TaskId | actor | eventId, linkKind (DueDate\|Scheduled\|Milestone) | - | Timeline |

## 4. Decisions (5) — R2 / R5

| ID | Decision | CurrentEvidence | Options | Recommendation | Consequence |
|---|---|---|---|---|---|
| TC-D-01 | Does TAB 4 take ownership of the Calendar module from the feature-module team? | Calendar was built under the parallel feature-module conventions (co-located interface+impl, bilingual literals, one controller per module) — different from the Tasks/TM conventions. | (a) full handover to TAB 4; (b) TAB 4 integrates only, the feature team keeps the module; (c) split — entity stays theirs, integration surface is TAB 4's | (a) full handover. Calendar synchronisation, recurrence and task linkage are all TAB 4 scope, and split ownership of one 131-line service would cost more to coordinate than to own. | TAB 4 inherits the convention mismatch and must decide whether to normalise the strings to Resources. |
| TC-D-02 | How does Calendar reach the Workspace, now that the shell exists and already consumes ITaskService? | A Workspace shell was created by another tab DURING this increment (BL/Workspace/*, Controllers/WorkspaceController.cs, 2026-08-06 10:30). It injects ITaskService for My Work but NOT ICalendarService, and has no agenda panel. | (a) TAB 4 exposes ICalendarService/agenda methods and the shell author calls them, mirroring the Tasks pattern; (b) TAB 4 implements a workspace source interface the shell discovers; (c) the shell queries CalendarEvents directly | (a) — it is exactly the pattern already working for Tasks, needs no new abstraction, and keeps the calendar visibility rule (owner/company/attendee) inside CalendarService where it already is. | (c) must be refused: it would re-implement the visibility rule in a second place and drift from CalendarService.Visible(). |
| TC-D-03 | What is the time model for Tasks and Calendar? | Calendar writes DateTime.Now and emits ISO without an offset; Tasks writes CreatedAt in UTC and DueDate as a bare local value. | (a) store UTC everywhere + render in the user's zone; (b) store local + a named zone per event; (c) keep as-is for single-zone companies | (a) UTC + a company/user zone for rendering. (c) is only honest while every company is single-zone, and it is the assumption that breaks silently. | A migration of existing rows is required, and it must be measured before it is run. |
| TC-D-04 | One-way publish or two-way external calendar sync? | No sync of any kind exists. | (a) ICS feed (read-only publish); (b) one-way push to Exchange/Google; (c) two-way sync with conflict resolution | (a) first — an authenticated ICS feed is small, reversible and covers most of the value. (c) is a project, not a feature, and needs a conflict policy before any code. | (c) would make an external system a writer into CrossBusiness data — a boundary decision, not a technical one. |
| TC-D-05 | Does a task's due date become a calendar event, or a calendar VIEW over tasks? | Neither exists today. | (a) materialise a CalendarEvent per task due date; (b) a read-only calendar view that unions tasks and events; (c) both, with the materialised event owned by the task | (b) a union VIEW. Materialising duplicates state and creates a second thing to keep in step — the defect class this tab has already had to remediate twice. | (b) means the calendar surface reads from two sources and must not let a user 'drag' a task due date unless the task itself is updated. |
