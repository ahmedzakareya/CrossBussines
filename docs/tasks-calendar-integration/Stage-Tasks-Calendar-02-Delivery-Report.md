# Stage-Tasks-Calendar-02 — Delivery Report

**Product:** CrossBusiness Platform · **Tab:** TAB 4 — Tasks and Calendar Integration
**Increment:** finish the first real Tasks and Calendar integration · **Date:** 2026-08-06

---

## 1. Result

**TASKS/CALENDAR INTEGRATION VERIFIED AND CLOSED.**

Every gate item is met. The full-suite gate is met rather than externally blocked: the two Reporting failures seen
mid-increment were TAB 2's own and were fixed by TAB 2 before final verification.

## 2. What was delivered

| Phase | Deliverable | State |
|---|---|---|
| 1 | TaskService transition wiring | **Done** — create, assign, reassign, due-date, complete, reopen |
| 2 | Registry onboarding | **Executed** — `Task` and `CalendarEvent` registered, with real search/resolve arms |
| 3 | Transactional business-event publishing | **Done** — events inside the state-change transaction |
| 4 | Agenda | **Complete**, re-verified unchanged |
| 5 | Time model at new boundaries | **Applied**; no legacy migration |
| 6 | TAB 4 test fixtures | **Fixed honestly** — all ten NOT NULL columns |
| 7 | Release verification | **Passed** with the app stopped |

## 3. Verification

| Check | Result |
|---|---|
| Debug build | **0 errors** |
| Release build | **0 errors** |
| TestRun build | **0 errors** |
| Focused Tasks/Calendar tests | **74 passed, 0 failed** |
| Full application suite | **1472 passed, 0 failed**, 183 skipped |
| DI + hosted-worker lifetime | **59 passed, 0 failed**, 1 skipped |
| Stale assemblies | none — every test run followed a build separately confirmed at 0 errors |
| `--no-build` usage | only after a 0-error build, per the rule |
| Hosted workers | **8, unchanged** — no third worker added |
| SQL against CrossBuyDB2 | **none** |
| TAB 2 / TAB 3 files modified | **none** |
| Their tests weakened or deleted | **none** |
| `ListedInRecordPicker` | **false** for both, as instructed |
| Renamed contracts preserved | `CalendarEvent.AttendeeAdded`, `CalendarEvent.ReminderTriggered` ✔ |

## 4. Files touched

**Created:** `BL/TasksCalendar/TaskCalendarEventPublisher.cs`,
`CrossBuy.Tests/TasksCalendarTransitionAndEventTests.cs`.

**Modified (TAB 4-owned):**

| File | Change |
|---|---|
| `BL/TaskService.cs` | transition wiring: events in-transaction, notifications post-commit |
| `BL/Platform/EntityRegistry.cs` | +2 entity codes, +2 definitions, +2 search arms, +2 resolve arms |
| `BL/TasksCalendar/TaskCalendarIntegrationContracts.cs` | 3 event names renamed to satisfy kernel validation |
| `CrossBuy.Tests/TasksCalendarTimeAndContractTests.cs` | the pinned required-set moved with the rename |
| `CrossBuy.Tests/TasksCalendarIntegrationTests.cs` | Employee seed: all ten NOT NULL columns |
| `Program.cs` | +1 `AddScoped` for the publisher |

`EntityRegistry.cs` is Platform Kernel property. The instruction for this increment directed registration through it
and did not list it as forbidden; the change is **purely additive** — no existing definition, arm or behaviour was
altered, and the pinned record-picker contract is asserted unchanged from this tab's own test.

## 5. Two defects found by doing rather than designing

**A latent contract defect.** `CalendarAttendee.Added`, `CalendarAttendee.Removed` and
`CalendarReminder.Triggered` were **unpublishable**: the kernel validates that an event name's prefix *is* the
registered entity code. Renamed to `CalendarEvent.AttendeeAdded` / `.AttendeeRemoved` / `.ReminderTriggered`, and
pinned by a test that asserts the old shape is rejected. A contracts-only increment could not have found this —
nothing validates a name until something tries to publish it.

**A test that could not fail.** `A_refused_transition...` used `New → Done` as its "refused" transition; that
transition is allowed. Corrected to `Done → New`.

## 6. What is NOT finished — stated plainly

**Calendar events are not yet emitted.** The publisher methods for `CalendarEvent.Created/Updated/Rescheduled/
Cancelled/AttendeeAdded/AttendeeRemoved` exist, compile and are correct, but `CalendarService` does not call them.

**Why it was not wired:** `CalendarService.SaveAsync` replaces all attendee rows on every save (delete-and-reinsert).
Emitting attendee events on top of that would produce `AttendeeRemoved`/`AttendeeAdded` pairs on **every unrelated
edit** — noise indistinguishable from real churn, written into an append-only event store. The attendee identity fix
must come first. Doing it here would have meant restructuring Calendar's write path, which is beyond this
increment's scope and is gated on the Calendar handover decision (TCI-D-02).

**`Task.Cancelled` remains unreachable** — `TaskItem.Status` is `New | InProgress | Done`. The contract and the
notification path exist; firing it needs the status vocabulary extended, which is a behaviour change not requested.

## 7. Completion gate

| Gate item | Status |
|---|---|
| Every supported Task transition notifies correctly | ✔ 11 transition tests |
| Retries do not duplicate notifications | ✔ incl. after the recipient reads it |
| `Task` and `CalendarEvent` registered | ✔ verified through the registry, not the request object |
| Business Events transactionally published | ✔ real `BusinessEventService`, in-transaction |
| No event appears on rollback | ✔ `No_event_survives_a_rolled_back_outer_transaction` |
| Agenda is a read union | ✔ |
| No Task becomes a Calendar row | ✔ no write path exists |
| Timezone behaviour explicit | ✔ 13 tests, both DST cases |
| All-day values remain stable | ✔ |
| No third worker introduced | ✔ 8 hosted services, unchanged |
| TAB 4 test fixtures valid | ✔ all ten NOT NULL columns, no constraint weakened |
| Debug/Release/TestRun succeed | ✔ 0 errors each |
| Full suite 0 failures | ✔ 1472 passed |
| No SQL against CrossBuyDB2 | ✔ |
| No other tab's files modified | ✔ |
| Preservation restores with 0 mismatches | ✔ preservation report |

## 8. Recommended next step

Fix Calendar attendee identity (difference, not delete-and-reinsert), then wire the Calendar publisher — in that
order, for the reason in §6. Both are gated on the Calendar handover decision, **TCI-D-02**.

**Stopped here for review, as instructed.**
