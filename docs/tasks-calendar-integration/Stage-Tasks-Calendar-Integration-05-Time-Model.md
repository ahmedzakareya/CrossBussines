# Stage-Tasks-Calendar-Integration-05 — Shared Time Model

**Implemented.** `CrossBuy/BL/TasksCalendar/TaskCalendarTime.cs`

---

## 1. The disagreement this resolves

- `CalendarService.SaveAsync` writes `DateTime.Now` (server local) and `Iso()` emits ISO **without an offset** —
  deliberately, so FullCalendar renders exactly what was typed.
- `TaskItem.CreatedAt` is `DateTime.UtcNow`; `DueDate` is a bare value.

Each module is self-consistent. Merged onto one agenda they are not: a task due "09:00" and an event at "09:00" are
different instants for anyone outside the server's zone.

## 2. The twelve rules, and where each is enforced

| # | Rule | Enforcement |
|---|---|---|
| 1 | Stored instants are UTC | `AssumeUtc`, `LocalWallClockToUtc` |
| 2 | Timezone resolved explicitly | `ResolveZone(zoneId, companyDefaultZoneId)` |
| 3 | No CompanyID fallback | the agenda refuses `CompanyId <= 0` |
| 4 | No server-local `DateTime.Now` for persisted timed events | `UtcNow` only — guarded by a test that scans the source |
| 5 | Boundary values carry an offset | `AgendaInstant.Local` is a `DateTimeOffset`; `ToApiString()` emits `+HH:mm` |
| 6 | **All-day values are local dates, not midnight UTC** | `AgendaTimeKind.AllDayDate` carries a `DateOnly` and is never converted |
| 7 | API payloads carry an explicit offset, or none at all for a date | `ToApiString()` |
| 8 | Recurrence expansion uses the event timezone | `TimeZoneId` carried on the instant; recurrence itself is out of scope |
| 9 | Workers compare UTC instants | the overdue sweep compares `TaskCalendarTime.UtcNow()` |
| 10 | Display conversion at the boundary | `UtcToOffset`, `LocalDateOf` |
| 11 | Missing/invalid timezone fails explicitly | `TimeZoneUnresolvedException` — no fallback |
| 12 | DST transitions covered by tests | spring-gap and autumn-ambiguity tests |

## 3. The two DST cases, decided rather than discovered

**Spring-forward gap** (a wall clock that never existed): moved forward by the DST delta. A user who typed 02:30 on
a night with no 02:30 meant 03:30.

**Autumn ambiguity** (a wall clock that happened twice): resolved deterministically to the **first** (daylight)
occurrence. `TimeZoneInfo.ConvertTimeToUtc` would silently pick standard time; being explicit means the choice is
ours, and it is asserted.

## 4. All-day — the rule that matters most

An all-day value is a **local date**. It has no zone, no instant, and is never converted. Writing it as midnight UTC
is precisely what moves a public holiday to the previous evening for anyone west of the server.

`ToApiString()` for an all-day value emits `2026-05-04` — no `T`, no offset, no `Z`.

## 5. Scope — what was deliberately NOT done

**No legacy timestamp was rewritten.** `CalendarService` still writes `DateTime.Now`; `TaskItem.DueDate` is
untouched. The brief forbids a broad rewrite, and a silent re-stamp of existing rows would be worse than the
inconsistency it replaced.

The model is applied **only at the new seam** — the agenda and the notification service.

### 5.1 Legacy remediation, when it is scheduled

1. **Measure first**: how many calendar events exist, whether any company operates in more than one zone, and what
   the server zone was when the rows were written. Without that, a conversion is a guess applied to real data.
2. Add `TimeZoneId` to `CalendarEvent`, defaulted to the measured company zone.
3. Convert stored values to UTC **only** for non-all-day rows. All-day rows keep their date.
4. Change `SaveAsync` to `UtcNow` + explicit zone **after** the data is converted, not before.

Steps 3 and 4 in the wrong order corrupt every event in the window between them.

## 6. Tests

`TasksCalendarTimeModelTests` — 13 tests: explicit failure for a missing and for an unknown zone; the company
default used only when no user zone is given; UTC round-trip; one instant in two zones; all-day preserved; the
all-day API shape; the timed API offset shape; the spring gap; autumn-ambiguity determinism; a range that covers the
whole last day; an unspecified value labelled rather than shifted; all-day sorting at the start of its local day;
and the `DateTime.Now` source guard.

**The guard strips comments before matching.** These files document the `DateTime.Now` defect they exist to prevent,
and a guard that could not tell prose from a call would forbid the code from explaining itself.
