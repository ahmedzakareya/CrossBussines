# Stage-Workspace-Integration — 03 · Agenda (Phase 3)

**Product:** CrossBusiness Workspace · **Tab:** TAB 3
**Consumed contract:** `CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService` — **owned by TAB 4**

---

## 1. A duplicate was built and deleted before it shipped

This is the most important thing in this document.

The phase reads *"Consume: IWorkspaceAgendaService"*. My first pass read that as *define and consume*, and I
wrote a complete parallel implementation in `BL/Workspace/`: an `IWorkspaceAgendaService`, a
`WorkspaceAgendaItem`, an `IWorkspaceAgendaSource` seam, plus `TaskAgendaSource` and `CalendarAgendaSource`
reading `ITaskService` and `ICalendarService`.

**TAB 4 had already built and registered it.** `Program.cs` already contained:

```csharp
builder.Services.AddScoped<CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService,
                           CrossBuy.BL.TasksCalendar.WorkspaceAgendaService>();
```

That is precisely the duplication the brief forbids. **The duplicate was deleted** — file removed, contracts
removed, sources removed, registrations removed — and the Workspace now consumes TAB 4's service.

Recorded rather than quietly corrected, because "consume X" can always be misread as "build X", and the cost
of the misreading is a second implementation that drifts.

---

## 2. What the Workspace does, and does not, do

| The Workspace | TAB 4's service |
| --- | --- |
| Calls `GetAgendaAsync(query)` | Reads `ITaskService` + `ICalendarService` |
| Projects rows for display | Unions the two sources |
| Groups by day in the view | Sorts deterministically (exposes its own sort key) |
| Maps flags to colours | Decides overdue / completed / redacted |
| Renders `DegradedSources` as a partial state | Reports degraded sources |

**The Workspace composes nothing.** It does not read tasks, does not read calendar events, does not merge,
does not sort, does not decide overdue, and does not convert time zones.

**It queries no table.** Not `TaskItems`, not `CalendarEvents`.

**It materialises nothing.** No agenda table exists. A task is projected into an agenda row for the duration
of one render and is never written into the calendar — the explicit "do not materialize tasks as Calendar
rows" requirement.

---

## 3. Required display fields — all present

| Required | Source field | Rendering |
| --- | --- | --- |
| **Item type** | `AgendaItemType` (Task \| CalendarEvent) | chip + distinct icon |
| **Title** | `Title` | row title |
| **Local time** | `AgendaInstant` | `HH:mm`, or `HH:mm–HH:mm` when there is an end |
| **All-day state** | `IsAllDay` | "All day" / "طوال اليوم" in the time slot, italic |
| **Overdue state** | `IsOverdue` | red chip + critical tone |
| **Completion state** | `IsCompleted` | struck-through title + green chip |
| **Source module** | `SourceModule` | chip |
| **Permission-safe deep link** | `DeepLink` | row href |

### 3.1 Time is read, never converted

`AgendaInstant` is a union: **either** a UTC instant carrying the viewer's offset, **or** a bare all-day local
date that has no zone and must never be shifted. The Workspace reads whichever the type says it is:

```csharp
instant.Kind == AgendaTimeKind.AllDayDate && instant.Date.HasValue
    ? instant.Date.Value.ToDateTime(TimeOnly.MinValue)     // never shifted
    : (instant.Local?.DateTime ?? instant.Utc ?? DateTime.MinValue)
```

**No second conversion happens here.** A conversion in the Workspace would silently disagree with the module
screen the user clicks through to — the same event would show two times.

The service **refuses** an unresolved zone rather than falling back to server-local (its owner's decision), so
the Workspace supplies `TimeZoneId` explicitly.

### 3.2 Deep links are permission-safe by construction

`DeepLink` points at the **owning module's** screen, which performs its own authorization on arrival. The
agenda therefore cannot become a way to reach a record the module would refuse to open.

`IsRedacted` is honoured as the service intends: the row keeps its **slot** and loses its **title** ("Busy" /
"مشغول" + a "details hidden" chip). "Busy at 14:00" is the useful half and is not private.

---

## 4. The five required states

| Required | Panel state | When |
| --- | --- | --- |
| **has data** | `Ready` | rows returned |
| **empty** | `Empty` | service answered, no rows in range |
| **unavailable** | `Unavailable` | `IWorkspaceAgendaService` not registered → *"The Tasks & Calendar agenda service is not registered in this environment."* |
| **partially available** | `PartiallyAvailable` | `result.DegradedSources` non-empty → banner naming the missing module, **above real rows** |
| **error isolated to one source** | `PartiallyAvailable` + `MissingSources` | one source failed inside TAB 4's service; the other's rows still render |

Two more states exist and are used:

* **`AccessDenied`** — no company, or no employee (*"An agenda is always somebody's agenda"* — the service's
  own words, and it throws rather than guessing).
* **`TemporaryFailure`** — the service threw, including its own refusals. Surfaced **with its message**,
  never swallowed into an empty agenda.

### 4.1 Partial is rendered above the rows, never instead of them

```
┌─ ⓘ This list is incomplete ─────────────────┐
│  Showing the agenda without Calendar …      │  ← banner
│                            [ Calendar ]     │
├─────────────────────────────────────────────┤
│  09:00  ▣ Task    Review invoice   [Tasks]  │  ← real rows, still shown
└─────────────────────────────────────────────┘
```

The rows are real and the list is incomplete. The reader needs **both** facts; showing only the banner would
hide real work, and showing only the rows would overstate completeness.

---

## 5. Screen

`/Workspace/Agenda?days=7`, with **Today / 7 days / 30 days** as links (state in the URL, bookmarkable, works
without script). `days` is clamped **1–60** inside the service.

Rows are grouped under sticky day headers — **Today** and **Tomorrow** named rather than dated. The grouping
walks an already-ordered list; the **order is the service's**, which exposes a deterministic sort key
precisely so a caller cannot invent a different one and get a different order for the same data.

The dashboard shows the same data capped at 8 rows, with `Trim` **preserving the panel state** — a truncated
partially-available agenda is still partially available, and flattening it to `Ready` would silently upgrade
the promise.
