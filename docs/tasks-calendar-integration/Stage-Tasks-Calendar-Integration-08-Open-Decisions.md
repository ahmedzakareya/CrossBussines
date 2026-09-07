# Stage-Tasks-Calendar-Integration-08 — Open Decisions

Every one of these is **explicitly unresolved**. None was silently assumed, and the code either avoids the question
or fails closed on it.

---

## TCI-D-01 — Registry implementation owner

**Question.** Who adds `Task` and `CalendarEvent` to `BL/Platform/EntityRegistry.cs`?

**State.** The onboarding contracts are built and tested (`TaskCalendarRegistryOnboarding.All()`), expressed in the
kernel's own `EntityDefinition` type, and marked `ExternallyPending = true`.

**Blocked by it:** timeline, comments, mentions, files, followers, record-picker membership, **and every business
event** — an event name is validated against the registry.

**Recommendation.** Platform Kernel adopts both, keeping `Task.PermissionScope = "Tasks"` and leaving Comments off
until the read resolvers are wired.

---

## TCI-D-02 — Calendar code handover owner

**Question.** Does TAB 4 take ownership of `CalendarEvent`, `CalendarService`, `CalendarController`,
`Views/Calendar/*` and `_CalendarReminders.cshtml` from the feature-module team?

**State.** **No Calendar file was modified in this increment.** The agenda reads Calendar through `ICalendarService`
only.

**What comes with the handover:** a convention mismatch (co-located interface+impl, bilingual literals), a
platform-wide surface (`_CalendarReminders` renders in four shared layouts), and two behaviours that need fixing
before features are added — attendee delete-and-reinsert, and owner-only edit with no delegation.

**Blocked by it:** attendee response status, reminders, recurrence, external sync.

---

## TCI-D-03 — Workspace agenda route owner

**Question.** Who calls `IWorkspaceAgendaService` and renders it?

**State.** The service is registered and tested. `BL/Workspace/*` and `Controllers/WorkspaceController.cs` are
TAB 3's and were **not modified**. The shell already consumes `ITaskService`, so the pattern exists.

**Recommendation.** TAB 3 resolves `IWorkspaceAgendaService` and calls `GetAgendaAsync`. It must **not** query
`CalendarEvents` directly, or the visibility rule would exist in two places.

---

## TCI-D-04 — External attendee model

**Question.** How are attendees who are not platform employees represented?

**State.** `CalendarEventAttendee` carries `EmployeeId` only. The registry request marks external principal access
**Forbidden** for this increment.

**Position.** **An attendee email is not an authenticated principal.** Treating one as such would let an address
become an authorization subject. Deferred pending an `ExternalPrincipalContext` owned by the platform.

---

## TCI-D-05 — Recurrence ownership

**Question.** Where does recurrence live — Tasks, Calendar, or one shared model?

**State.** Neither module has any recurrence today. TM-7 is rule-driven; TM-9 waits for a movement. Neither is a
schedule. Not implemented here, by instruction.

**Recommendation.** **One** shared model owned by Calendar (owner decision 2), with a rolling-horizon
materialisation and a duplicate guard keyed `(RuleId, OccurrenceDate)` — the pattern `TaskAutoLog` already proved.
Two implementations would diverge the moment a recurring task had to appear on a calendar.

---

## TCI-D-06 — External synchronisation direction

**Question.** ICS feed, one-way push, or two-way sync?

**State.** Nothing exists. Not implemented here, by instruction.

**Recommendation.** An authenticated read-only ICS feed first — low risk, reversible, no external writer. Two-way
sync makes an external system a **writer** into CrossBusiness data and needs a conflict policy before any code.

**Prerequisite for any option:** the time model (doc 05) applied to stored Calendar rows, or every phone outside the
server's zone shows the wrong hour.

---

## TCI-D-07 — Legacy timestamp migration strategy

**Question.** When and how are existing `CalendarEvent` rows converted to UTC?

**State.** **Not done.** The time model is applied only at the new seam; `CalendarService` still writes
`DateTime.Now`.

**Required order** (doc 05 §5.1): measure → add `TimeZoneId` → convert non-all-day rows → *then* change `SaveAsync`.
Steps 3 and 4 reversed corrupt every event between them. All-day rows keep their date and are never converted.

Also open under this heading: **retention and audit policy** for `Task` and `CalendarEvent`, carried as non-null
placeholders in both registry requests so a null cannot read as "no policy needed".

---

## Carried, not decided: two Phase-10 test items

*"Events emitted only after successful state change"* and *"no event on denied/failed mutation"* describe a
**raiser**. No event is raised in this increment, so neither is assertable yet. They become required tests in the
increment that raises them — recorded here so they are not lost.

---

## Also open (reported, not this tab's to fix)

- **`TasksController` gating** — 3 `TasksActions` references against 8 actions, and `DefaultCompanyId = 1`. TAB 1.
- **Tasks/Calendar SQL integrity** — 0 FKs, 0 CHECKs, 0 concurrency tokens across 12 scripts.
- **`Task.Cancelled` has no status to fire on** — `TaskItem.Status` is `New | InProgress | Done`. The contract and
  the notification path exist; raising it needs the status vocabulary extended first.
