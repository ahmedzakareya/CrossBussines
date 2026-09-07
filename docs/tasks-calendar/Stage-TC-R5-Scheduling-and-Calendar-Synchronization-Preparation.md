# Stage-TC-R5 — Task Scheduling and Calendar Synchronisation: Preparation

**This is the stop point.** R5 prepares; it does not implement. Nothing in this document has been built.

---

## 1. What "task scheduling" means today — and what it does not

Two mechanisms exist, and neither is a schedule:

| Mechanism | What it actually does | Evidence |
|---|---|---|
| **TM-7 auto-generation** | Scans **system state** against five fixed rule types — `LowStock`, `OverdueInvoice`, `WorkOrderQc`, `DeliveryReady`, `NewEmployeeOnboard` — and creates a task, guarded by a per-`(rule, source)` `RuleKey` | `BL/TaskGeneratorService.cs:29-33`, `Models/Context/Tasks/TaskAutomation.cs` |
| **TM-9 "scheduled" tasks** | A task **waiting for a movement**: expected entity type + party + optional window; when a matching invoice appears it is auto-linked, or suggestions are recorded for a manager | `BL/TaskScheduleMatcher.cs:21-24,36` |

Both are **state-driven**. Despite the field name `IsScheduled`, **there is no time-driven recurrence anywhere in
Tasks**, and none in Calendar either (no RRULE, no series, no exception dates). "Every Sunday, inspect the site" is
not expressible today for either a task or an event.

That is the gap R5 prepares — not a redesign of TM-7 or TM-9, both of which stay exactly as they are.

## 2. Recurrence — the proposed model

**One recurrence model, shared by both modules.** Two separate implementations would diverge, and the moment a
recurring task needs to appear on a calendar the two would have to agree anyway.

### 2.1 `RecurrenceRule` (proposed, new)

| Field | Note |
|---|---|
| `CompanyID` | required |
| `OwnerEntityCode` / `OwnerEntityId` | `Task` or `CalendarEvent` — the series head |
| `Frequency` | `Daily` \| `Weekly` \| `Monthly` \| `Yearly` |
| `Interval` | every N periods |
| `ByWeekday`, `ByMonthDay`, `BySetPos` | the common iCalendar subset — **not** full RFC 5545 |
| `StartDate`, `Until`, `Count` | bounded: a series must end by date or by count |
| `TimeZoneId` | IANA id — a recurrence without a zone is wrong twice a year (see §3) |
| `ExceptionDates` | child table: dates skipped |
| `IsActive`, `RowVersion` | |

### 2.2 Materialisation policy — the decision that matters

| Option | Consequence |
|---|---|
| **Materialise every occurrence up front** | An unbounded series produces unbounded rows. Rejected. |
| **Compute occurrences on read, never store** | Cannot assign, complete or comment on a single occurrence — fatal for tasks. |
| **Rolling horizon (recommended)** | Materialise occurrences within a bounded window (e.g. 90 days) via the existing worker pattern; extend the window as time passes. A materialised occurrence is a real task with a real identity. |

Rolling horizon is the only option under which "mark this Sunday's inspection done" and "skip next Sunday" both work.
The series head keeps the rule; each occurrence is an ordinary `TaskItem` carrying `RecurrenceRuleId` + `OccurrenceDate`.

**Duplicate guard:** the same `TaskAutoLog` pattern TM-7 already proved — a unique key per
`(RecurrenceRuleId, OccurrenceDate)` — so a worker that runs twice creates one task.

### 2.3 Editing a series

Three intents, and a UI that does not ask is a UI that loses data: **this occurrence** · **this and future** ·
**the whole series**. "This and future" splits the rule at the occurrence date and leaves the past untouched — past
occurrences are history and are never rewritten.

## 3. Time zones — the prerequisite, not a refinement (TC-D-03)

Today the two modules disagree about what a stored time means:

- `CalendarService.SaveAsync` writes `DateTime.Now` (server local) and `Iso()` (`:129`) emits **no offset**,
  deliberately, so FullCalendar shows exactly what was typed.
- `TaskItem.CreatedAt` is `DateTime.UtcNow`; `DueDate` is a bare local value.

For a single-timezone company both are self-consistent. **Merged onto one agenda (R4 §5.1) they are not**: a task
due "09:00" and an event at "09:00" are different real instants, and recurrence makes it worse — a weekly 09:00
series must stay 09:00 **local** across a DST boundary, which is only computable from a named zone.

**Recommendation (TC-D-03 option a):** store UTC, carry `TimeZoneId` on the recurrence and on the event, render in
the user's zone.

**This requires a migration of existing rows, and the migration must be measured before it is run** — the same
discipline this tab applied to the construction contract mapping. Specifically: how many events exist, whether any
company operates in more than one zone, and what the server zone was when the rows were written. **No migration is
proposed here without that measurement.**

## 4. Calendar attendee model — fix before extending (TC-G-08 / TC-G-09)

`CalendarService.SaveAsync:110-113` deletes all attendee rows and re-inserts them on every save. So attendee rows
have **no stable identity**, and any state attached to an attendee — an accept/decline response, a per-attendee
reminder lead time, a delegation — is destroyed by the next save of the event.

**Therefore the order is fixed:** attendee identity must be made stable **before** a response status is added, not
after. Adding the response first guarantees a data-loss bug.

The remedy is the one this tab already validated as CR-01 in the construction track: **difference, not replace** —
match existing rows, add the new, remove only those actually dropped, and never lose a row that carries state.

Proposed attendee fields once identity is stable: `Response` (`NeedsAction` \| `Accepted` \| `Declined` \|
`Tentative`), `RespondedAt`, `IsOptional`, `ReminderMinutes`, `RowVersion`.

Delegation (TC-G-09) is a separate, smaller change: an `Organiser` concept distinct from `OwnerEmpId`, so an
assistant can manage an executive's calendar without owning the row.

## 5. Calendar synchronisation (TC-D-04)

**Nothing exists** — no ICS, no CalDAV, no Exchange/Graph, no Google client anywhere in `BL/`. So this is new work,
and its *direction* is the decision, not its implementation.

| Option | Shape | Risk |
|---|---|---|
| **(a) ICS feed — recommended first** | An authenticated, per-employee, read-only `text/calendar` feed of the events already visible to that employee, plus optionally task due dates | Low. Reversible. No external writer. The token is the whole security surface, and it must be revocable per employee. |
| **(b) One-way push to Exchange/Google** | CrossBusiness remains the source of truth; the external calendar mirrors it | Medium. Needs OAuth, per-user consent, retry/backoff, and a mapping table. |
| **(c) Two-way sync** | The external calendar becomes a **writer** into CrossBusiness data | High. Requires a conflict policy, an idempotency key per external event, and a decision about which side wins. This is a project, not a feature. |

**Recommendation: (a) first.** It delivers most of the practical value (a phone showing CrossBusiness events) at a
fraction of the risk, and it makes (b) cheaper later because the read projection is already built.

**Prerequisites for any option:** UTC + zone (§3), a stable event identity (`UID` that survives edits), and a
sequence number for updates. An ICS feed emitted from local times with no zone would show the wrong hour on every
phone outside the server's zone — which is why §3 precedes this section rather than following it.

## 6. Recommended order of work after this increment

Ordered by *value ÷ risk*, and by what unblocks what:

| # | Step | Depends on | Why here |
|---|---|---|---|
| 1 | **Task notifications, Stage 1 direct** (assigned, reassigned, completed, reopened) | **nothing** | Closes TC-G-04. No kernel change, no other tab, no registry. The single highest-value change available today. |
| 2 | Registry request for `Task` + `CalendarEvent` | Platform Kernel accepts | Unblocks timeline, comments, files, followers, picker |
| 3 | Due-soon / overdue worker + `TaskNotificationLog` | 1 | Time-driven events need a producer; idempotency is the whole design |
| 4 | Calendar attendee identity fix | TC-D-01 handover | **Must precede** any response-status work |
| 5 | Time-zone decision + measured migration | TC-D-03 | Prerequisite for agenda, recurrence and sync |
| 6 | Raise R3 events; third tab wires the projection; remove the direct calls | 2, 5 | Stage 1 → Stage 2, without double-notifying |
| 7 | Calendar agenda source on `ICalendarService`, consumed by the **existing** Workspace shell | TC-D-01 | The shell already shows tasks; Calendar is the missing half (TC-G-16). Small, and visible immediately. |
| 8 | Recurrence — rolling horizon | 5 | The largest single piece; needs the time model settled first |
| 9 | ICS feed | 5, 8 | Cheapest external win |
| 10 | DB integrity on tasks/calendar tables | — | Not a blocker; do it before the workspace widens exposure |

Steps 1, 3 and 10 need **no other tab**. Steps 2, 6 and 7 do.

## 7. Open decisions carried out of R5

| ID | Decision | Blocks |
|---|---|---|
| **TC-D-01** | Does TAB 4 take Calendar ownership? | §4, §5 |
| **TC-D-02** | How does Calendar reach the now-existing Workspace shell? | the agenda panel |
| **TC-D-03** | Time model: UTC + zone, or local? | §2, §3, §5, and the agenda in R4 |
| **TC-D-04** | ICS / one-way / two-way sync? | §5 |
| **TC-D-05** | Task due dates: materialised events or a union view? | R4 §5.2 — recommendation is a union view |

All five carry evidence, options, a recommendation and a consequence in
`tasks-calendar-decision-register.csv`.

## 8. Stop

Per instruction, work stops here — after R5 **preparation**. No recurrence entity, no ICS endpoint, no attendee
change, no notification call and no registry edit has been made. The next increment begins with §6 step 1, which is
also the one that needs nobody's permission but the owner's.
