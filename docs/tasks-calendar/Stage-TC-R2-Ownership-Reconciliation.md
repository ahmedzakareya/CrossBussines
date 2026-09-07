# Stage-TC-R2 — Ownership Reconciliation

Full matrix (15 areas): `Stage-TC-Catalogs-Appendix.md` §1 and `tasks-calendar-ownership-matrix.csv`.

---

## 1. Why this phase exists before any design

Tasks and Calendar were built by **different teams under different conventions**, and the integration touches three
other tabs' property. Reconciling that first is what stops R3–R5 from quietly proposing changes to code this tab may
not touch.

## 2. The four ownership classes

| Class | Meaning | Areas |
|---|---|---|
| **Owned — no handover** | Already this tab's lineage | Task entity, timesheets, TM-4 labour marker, TM-5 billing orchestration, TM-7 rules, TM-9 matching |
| **Handover required** | Built by the parallel feature-module team; this increment proposes TAB 4 takes it | Calendar entity + service + controller; the calendar reminders strip |
| **Consumed — request only** | Another tab's; TAB 4 may depend on it and must ask for changes | `EntityRegistry`, business-event kernel, timeline projection, comments/mentions, notification delivery, `TasksAccessService` |
| **Undecided** | No owner exists yet | The Workspace shell |

## 3. Handover: the Calendar module (TC-D-01)

**What is being handed over:** `Models/Context/Calendar/CalendarEvent.cs`, `BL/CalendarService.cs`,
`Controllers/CalendarController.cs`, `Views/Calendar/Index.cshtml`, `Views/Shared/_CalendarReminders.cshtml`,
`deploy/sql/calendar.sql`.

**What comes with it — stated so it is accepted knowingly, not discovered later:**

1. **A convention mismatch.** Calendar follows the feature-module conventions: interface + implementation + DTOs
   co-located in one file, one controller per module, and — across that module set — bilingual literals rather than
   Resources. `CalendarController` does use `IStringLocalizer`, so the controller is already closer to our
   convention than its siblings. Normalising the remaining strings is a TAB 4 decision, not an obligation.
2. **A platform-wide surface.** `_CalendarReminders.cshtml` is embedded in **four** shared layouts
   (`_LayoutAccounting`, `_LayoutBackend`, `_LayoutInventory`, `_LayoutManufacturing`). Any change to it is visible
   in every module at once. It must be treated as a shared-file change, with the same care as `Program.cs`.
3. **Two behaviours that need fixing before features are added**: attendee delete-and-reinsert (TC-G-08) and
   owner-only edit with no delegation (TC-G-09).

**Recommendation:** full handover (option a). Calendar synchronisation, recurrence and task linkage are all TAB 4
scope; splitting a 131-line service across two owners would cost more in coordination than the service itself is
worth.

**This document does not execute the handover.** It requires the owner's decision, and until then this tab changes
no Calendar file.

## 4. What TAB 4 must NOT touch — and what it asks for instead

| Area | Owner | What TAB 4 does |
|---|---|---|
| `BL/Platform/EntityRegistry.cs` | Platform Kernel | **Requests** two new entity codes (R4 §2). Does not edit. |
| `BusinessEventService`, dispatch worker, outbox | Platform Kernel | Defines event names and payloads (R3). Does not modify the kernel. |
| `TimelineProjectionConsumer`, `TimelineProjectionService` | **THIRD TAB** | Supplies payloads and a presenter contract (R4 §3). Writes no consumer. |
| Comments, mentions, `NotificationService` internals | **THIRD TAB** | Uses `NotifyAsync` as a retained direct producer until onboarding, then moves to the projection (R3 §4). |
| `BL/TasksAccessService.cs` | **FIRST TAB** | Consumes the existing 8 actions. Any new action (e.g. a calendar action set) is a **request**. |
| `TasksController` gating + `DefaultCompanyId = 1` | **FIRST TAB** | Reported as TC-G-12. Not fixed here. |
| Reporting datasets for tasks/calendar | **SECOND TAB** | Would supply read-only data-source contracts in a later phase. Out of R1–R5 scope. |

## 5. Boundary rules carried into R3–R5

These are the rules the rest of the increment is written against:

1. **Tasks and Calendar write no GL and no stock.** TM-4 and TM-5 already orchestrate through
   `ManufService.AddLaborAsync` and `ReceivableService`; that stays. No new writer.
2. **The registry is the only entity-code authority.** `TaskLinkResolver` already defers to it; nothing in this
   increment reintroduces a local type table.
3. **A business event is raised only inside the caller's ambient transaction, immediately before commit, with no
   swallowing catch** — the kernel's mandatory contract. This is precisely why R3 proposes events rather than
   raising them (see R3 §2).
4. **Direct `NotifyAsync` remains legitimate** while an entity is not onboarded, and must move to the projection
   when it is — otherwise the entity double-notifies.
5. **Nothing about another tab's file is changed to make this increment work.** Where a change there is required, it
   is written as a request with the exact shape needed.

## 6. Decisions this phase raises

| ID | Decision | Blocks |
|---|---|---|
| **TC-D-01** | Does TAB 4 take ownership of Calendar? | R5 (sync, recurrence, attendee model) |
| **TC-D-02** | Who builds and owns the Workspace shell? | R4 delivery of a shell — contracts are delivered regardless |

Both are in `tasks-calendar-decision-register.csv` with options, recommendation and consequence.
