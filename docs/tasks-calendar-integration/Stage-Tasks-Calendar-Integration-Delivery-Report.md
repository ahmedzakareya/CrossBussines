# Stage-Tasks-Calendar-Integration — Delivery Report

**Product:** CrossBusiness Platform · **Tab:** TAB 4 — Tasks & Calendar Integration
**Increment:** first safe Tasks and Calendar integration increment · **Date:** 2026-08-06

---

## 1. What was delivered

| Phase | Deliverable | State |
|---|---|---|
| 1 | Fresh build, all three configurations | **Debug 0 errors · TestRun 0 errors · Release 0 compiler errors** (see §2) |
| 2 | Direct Task notifications | **Implemented** — 7 kinds, idempotent, company-isolated |
| 3 | `Task` registry onboarding contract | **Delivered, externally pending** (Platform owns the registry) |
| 4 | `CalendarEvent` registry onboarding contract | **Delivered, externally pending** |
| 5 | Task business events | **9 contracts v1 defined**, none raised |
| 6 | Calendar business events | **9 contracts v1 defined**, none raised |
| 7 | Shared time model | **Implemented and enforced at the seam** |
| 8 | `IWorkspaceAgendaService` | **Implemented** — read-only union, no Workspace file touched |
| 9 | Overdue notification safety | **One owner** — the existing TM-7 worker; **no third worker** |
| 10 | Tests | **57 focused tests, 0 failures** |
| 11 | Documentation | 9 documents + manifest + preservation report |
| 12 | Verification | §2 |

**Not implemented, by instruction:** recurring tasks, external calendar sync, Outlook/Teams, new Task or Calendar
screens, timeline duplication, full Communication activation, SignalR, mobile UI, broad schema migration.
**No SQL was executed against any database. No schema change was made at all.**

## 2. Verification

| Check | Result |
|---|---|
| Debug build | **succeeded, 0 errors** |
| TestRun build | **succeeded, 0 errors** |
| Release build | **0 compiler errors**; output copy blocked — see below |
| Focused Tasks/Calendar tests | **57 passed, 0 failed** |
| Full application suite | **1408 passed, 0 failed, 183 skipped** (total 1591) |
| Stale assembly | avoided — every result taken after an explicit verified build (§5) |
| Source exclusions | none — full-solution builds throughout |
| SQL against CrossBuyDB2 | **none — zero statements** |
| Files of TABs 1/2/3, Construction, Accounting, Inventory, CRM modified | **none** |
| Duplicate hosted worker | **none added** |
| Duplicate task/calendar data | **none** — the agenda is a read union with no write path |

### 2.1 Release, stated precisely

Release **compiles with 0 compiler errors**. The build step that fails is MSBuild copying
`obj\Release\net8.0\CrossBuy.dll` to `bin\Release\net8.0\`:

```
error MSB3027: ... The file is locked by: "Microsoft Visual Studio (13396), IIS Express Worker Process (10512)"
```

That is the running application holding its own output, not a defect in the code. It was retried and persisted. I
did not kill Visual Studio or IIS Express to force it — that would stop a process the owner is using. **Re-run the
Release build with the app stopped and it will complete.**

### 2.2 The shared tree broke twice during this increment — neither time was mine

| When | File | Owner | Outcome |
|---|---|---|---|
| Phase 1, first attempt | `BL/Workspace/WorkspaceService.cs` (`CommPageRequest` missing), `Views/Workspace/Index.cshtml` | **TAB 3** | they fixed it; I changed nothing |
| Phase 1, second attempt | `Controllers/Api/ReportsCenterApiController.cs` (CS1503/CS1061, then CS1022 mid-save) | **TAB 2** | they fixed it; I changed nothing |

Both were confirmed by file mtime and by the fact that the errors named types owned by those tabs. Per the Phase 1
rule I reported and waited rather than editing another tab's files.

## 3. Files touched — complete

**Created (production):** `BL/TasksCalendar/TaskCalendarTime.cs`, `TaskCalendarIntegrationContracts.cs`,
`TaskNotificationService.cs`, `TaskOverdueSweepService.cs`, `WorkspaceAgendaService.cs`.

**Modified (production) — two files, both justified:**

| File | Change | Why it was unavoidable |
|---|---|---|
| `BL/TaskGeneratorHostedService.cs` | +the overdue sweep call inside its existing per-company loop, in its own try/catch | Phase 9 forbids a third worker. Overdue detection must live in an existing per-company timer-driven worker, and this is the task-side one. Its own try/catch means a notification failure cannot stop TM-7 rule generation. |
| `Program.cs` | +3 `AddScoped` registrations | The new services must be resolvable. All three take only existing dependencies, so the DI graph gains no new coupling. |

**Created (tests):** three files, 57 tests.

**No file** under `BL/Workspace/`, `BL/Communication/`, `BL/Comm/`, `BL/Reporting/`, `BL/Platform/`,
`TasksAccessService.cs`, `CalendarService.cs`, `CalendarController.cs` or any view was modified.

## 4. Owner decisions — where each is enforced

| # | Decision | Enforcement |
|---|---|---|
| 1, 2 | Task owns task lifecycle; Calendar owns events/attendees | no module's rules were moved or copied |
| 3 | Communication owns comments/mentions/attachments/timeline/notification delivery | notifications go through `INotificationService`; no timeline or comment code written |
| 4 | Workspace owns no Task or Calendar business data | the agenda returns a projection and stores nothing |
| 5 | Task due dates appear as a read union | `AgendaItemType.Task` carries the task's own id |
| 6 | No materialised `CalendarEvent` for a task | no write path exists; two tests assert it |
| 7 | UTC storage | `TaskCalendarTime`; guarded by a `DateTime.Now` source scan |
| 8 | All-day stays a local date | `AgendaTimeKind.AllDayDate`; three tests |
| 9 | API timestamps carry UTC or explicit offset | `ToApiString()`; regex-asserted |
| 10, 11 | No automatic cross-module writes | nothing in this increment writes across modules |
| 12 | Cross-module writes are explicit user actions with audit | none introduced; `TaskNotificationOutcome` records every decision |

## 5. Two process findings worth keeping

**A stale test assembly produced false failures.** A lingering `testhost.exe` held `CrossBuy.Tests.dll` open;
MSBuild could not copy the fresh build and `dotnet test` ran the **old** assembly, showing 12 failures the current
code did not have. Every result in this report was taken after an explicit, separately verified build. This is the
second increment in which this trap appeared — it is worth a standing rule: **never read a test result from a run
whose build output you did not see succeed.**

**Two test-side defects, neither a product defect:** the `Employee` seed omitted ten NOT NULL columns, and the
`DateTime.Now` guard matched its own documentation prose until it was taught to strip comments.

## 6. Completion gate

| # | Gate item | Status |
|---|---|---|
| 1 | Shared tree builds cleanly | ✔ Debug/TestRun 0 errors; Release 0 compiler errors, copy blocked by the running app (§2.1) |
| 2 | Direct Task notifications work | ✔ 7 kinds, 11 tests |
| 3 | No duplicate notification | ✔ incl. after the recipient reads it |
| 4 | Task registry onboarding implemented or handed over | ✔ contract delivered, `ExternallyPending` |
| 5 | Calendar registry onboarding implemented or handed over | ✔ same |
| 6 | Task business events defined and tested | ✔ 9 v1 |
| 7 | Calendar business events defined and tested | ✔ 9 v1 |
| 8 | Shared time model explicit | ✔ 13 tests |
| 9 | New integration code has no `DateTime.Now` | ✔ source-scanning guard |
| 10 | All-day events retain their date | ✔ |
| 11 | Workspace Agenda contract exists | ✔ |
| 12 | Agenda uses a read union | ✔ |
| 13 | No task due date materialised as a `CalendarEvent` | ✔ no write path |
| 14 | Module permissions preserved | ✔ agenda defers to both services; notifications restricted to task-related, same-company, active employees |
| 15 | Company isolation preserved | ✔ company read from the task row; unresolved company does nothing |
| 16 | Existing logic not duplicated | ✔ proven by the fakes in the agenda tests |
| 17 | No third overlapping hosted worker | ✔ |
| 18 | Full suite 0 failures | ✔ 1408 passed |
| 19 | No SQL against CrossBuyDB2 | ✔ zero statements |
| 20 | No other tab's files modified | ✔ |
| 21 | Documentation exists | ✔ 9 docs + manifest + preservation report |
| 22 | Preservation restore, 0 mismatches | ✔ see the preservation report |

## 7. Recommended next step

**Call the notification service from `TaskService`.** It is registered, tested and currently invoked only by the
overdue sweep — so assignment, reassignment, completion and reopening still notify nobody until those call sites are
added. That is one line per transition, and it is deliberately left for the owner to schedule because it changes
what task mutation does.

Then: the registry request (TCI-D-01), which unblocks timeline, comments, files and every business event.

**Stopped here for review, as instructed.** No recurrence, no external sync, no UI, no module redesign.
