# Stage-Tasks-Calendar-02 — Time Evidence (Phase 5)

**Applied at the new integration boundaries only.** `CrossBuy/BL/TasksCalendar/TaskCalendarTime.cs`

---

## 1. Status

The model was implemented in the previous increment and is now *used* by everything this increment added: every
event payload timestamp, every notification stamp and the overdue sweep go through it. **13 time tests, 0 failures.**

## 2. The rules, and where each is enforced now

| Rule | Enforcement |
|---|---|
| New persisted instants use UTC | `TaskCalendarTime.UtcNow()` in the publisher, the notification service and the sweep |
| APIs use `DateTimeOffset` or explicit UTC | `AgendaInstant.ToApiString()` emits `+HH:mm`; event payloads carry UTC `DateTime` |
| All-day remains a **local date** | `AgendaTimeKind.AllDayDate`; the `CalendarEvent.Created` contract carries `StartLocalDate` (`DateOnly?`) separately from `StartUtc` (`DateTime?`) |
| Missing timezone fails explicitly | `TimeZoneUnresolvedException` — no server-local fallback anywhere |
| Background workers compare UTC | `TaskOverdueSweepService` compares `TaskCalendarTime.UtcNow()` |
| **No `DateTime.Now` in new integration code** | a test scans every file under `BL/TasksCalendar/` |

## 3. The `DateTime.Now` guard

`No_new_integration_source_file_uses_DateTime_Now` reads every `.cs` file under `BL/TasksCalendar/`, **strips
comments**, then fails on any `DateTime.Now` / `DateTimeOffset.Now`. Comments are stripped because these files
document the defect they exist to prevent — a guard that could not tell prose from a call would forbid the code from
explaining itself. `DateTime.UtcNow` is deliberately not matched.

The guard now covers six files rather than five: `TaskCalendarEventPublisher.cs` was added this increment and is
scanned automatically, because the test walks the directory rather than a hard-coded list.

## 4. DST — decided, not discovered

**Spring-forward gap** (a wall clock that never existed): moved forward by the DST delta — 02:30 on a night with no
02:30 means 03:30.

**Autumn ambiguity** (a wall clock that happened twice): resolved deterministically to the **first** (daylight)
occurrence. `TimeZoneInfo.ConvertTimeToUtc` would silently pick standard time; being explicit makes the choice ours,
and testable.

Both are asserted against a zone with a real DST rule, resolved by trying the Windows and IANA ids in turn so the
tests run on either host instead of skipping.

## 5. No legacy migration was performed

`CalendarService` still writes `DateTime.Now`; `TaskItem.DueDate` is untouched. The instruction forbids a broad
legacy migration, and a silent re-stamp of existing rows would be worse than the inconsistency it replaced.

### 5.1 The later migration sequence — unchanged and still required

1. **Measure first**: how many calendar events exist, whether any company operates in more than one zone, and what
   the server zone was when the rows were written.
2. Add `TimeZoneId` to `CalendarEvent`, defaulted to the measured company zone.
3. Convert stored values to UTC **only** for non-all-day rows. All-day rows keep their date.
4. Change `SaveAsync` to `UtcNow` + an explicit zone **after** the data is converted.

Steps 3 and 4 in the wrong order corrupt every event in the window between them. Open decision **TCI-D-07**.
