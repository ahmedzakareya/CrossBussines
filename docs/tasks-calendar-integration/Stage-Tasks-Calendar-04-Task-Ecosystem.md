# Stage Tasks & Calendar 04 — Task Ecosystem

**Owner:** TAB 4 (التاب الرابع) · **Date:** 2026-08-09 · **Status:** delivered; open items 1-3 closed; Release gate blocked by a running IDE (see §4.3)

Implements the 14 requested ecosystem features on top of the existing Tasks and Calendar modules.
Nothing was rebuilt: the board is a third view of the same list, the timeline and comments are the
platform's, and the calendar keeps its FullCalendar screen.

---

## 1. What was built

| # | Feature | How | Where |
|---|---------|-----|-------|
| 1 | Kanban Board | New view of the SAME `GetTasksAsync`; a drag is `ChangeStatusAsync` | `Views/Tasks/Board.cshtml` |
| 2 | Task Timeline | Surfaced from the platform (`/PlatformTimeline/List?entityType=Task`) | `Views/Tasks/Detail.cshtml` |
| 3 | Task Activity History | Same endpoint — the timeline IS the history | `Views/Tasks/Detail.cshtml` |
| 4 | Task Comments | Surfaced from the Comm platform (the store that owns attachments) | `TasksController.TaskComments` |
| 5 | Task Attachments | Comm platform attachments; bytes in the EXISTING FileManager store | `TasksController.TaskCommentAdd` |
| 6 | Task Checklist | New table + service; difference-based reorder | `TaskChecklistAndTemplateService.cs` |
| 7 | Task Dependencies | New table + service; BFS cycle refusal naming the chain | `TaskDependencyService.cs` |
| 8 | Task Templates | New tables + service; applies THROUGH `ITaskService` | `TaskChecklistAndTemplateService.cs` |
| 9 | Calendar Timeline View | Inventory table, people × hours, occurrence-based | `Views/Calendar/Timeline.cshtml` |
| 10 | Calendar Resource View | Inventory table, resources × hours, double-booking marked | `Views/Calendar/ResourceView.cshtml` |
| 11 | Calendar Recurrence Editor | Read + write, gated by the new `ICalendarAccessService` | `CalendarAccessService.cs` |
| 12 | Calendar Availability | Free-slot finder over merged busy intervals | `CalendarSchedulingService.cs` |
| 13 | Calendar Conflict Detection | Occurrence-level, people and resources | `CalendarSchedulingService.cs` |
| 14 | Calendar Timezone Management | Zone id (never an offset); per-occurrence conversion | `CalendarSchedulingService.cs` |

---

## 2. The decisions that carry the risk

### 2.1 A dependency cycle is refused, and the refusal names the chain

`A → B → C → A` is not "slightly wrong": every task in the loop blocks every other, so nothing in it
can start and no screen can explain why. `WouldCreateCycleAsync` walks forward from the successor
looking for the predecessor (breadth-first, with a visited set so a *pre-existing* cycle terminates
instead of hanging), then rebuilds the path so the error says **which** chain closes.

The unique index `UX_TaskDependencies_Edge` makes the duplicate-edge refusal true under concurrency;
the service check alone would lose a race.

### 2.2 A checklist reorder is a difference, not a delete-and-reinsert

The same lesson as CR-01. Delete-and-reinsert would give every line a new id and silently discard
who ticked it and when. `ReorderAsync` renumbers the existing rows; a test asserts a done line stays
done, attributed to the same person, after being moved.

### 2.3 Checklist completion does NOT drive `TaskItem.ProgressPct`

`ProgressPct` has always been a human's judgement. Recomputing it from checklist lines would silently
change what an already-reported number means. Asserted by test.

### 2.4 A recurring event repeats in LOCAL wall-clock time

**The single most consequential line of this increment.** A 09:00 weekly meeting is at 09:00 the week
after the clocks move — which is a *different number of hours* later. The expansion walks the local
calendar and converts each occurrence to UTC individually.

Adding `7 × 24h` to a UTC instant would drift the meeting by an hour twice a year, and nobody would
report it as a bug — they would just start arriving late. The test asserts the London BST transition
explicitly: the UTC gap is **167 hours** across the boundary and 168 either side of it, while the
local time stays 09:00 throughout.

Time zones are stored as **zone ids, never offsets**. An offset is only true until the next DST change.

### 2.5 A repeating series must have an end

No end means no last occurrence, so every expansion would be truncated by a limit the user never
chose. `SaveScheduleAsync` refuses a series with neither an until-date nor a count — and refuses both
at once. `MaxOccurrencesPerEvent = 1000` is a backstop, not the answer.

### 2.6 Touching intervals do not conflict

A meeting ending at 10:00 does not conflict with one starting at 10:00. Getting this wrong makes
back-to-back scheduling impossible — the most common thing people actually do.

### 2.7 Availability merges busy blocks before subtracting them

Two overlapping meetings are ONE busy stretch. Subtracting them one at a time invents a free gap
between them, and the app then offers a slot that is not free. Asserted by test.

### 2.8 Conflicts are computed over OCCURRENCES, not rows

A weekly meeting is one row and many occurrences. Checking rows would report week 3 as free.

### 2.9 New tables, never `ALTER`

Four task tables and three calendar tables, no column added to `TaskItems` or `CalendarEvents`. This
tree is shared by four concurrent workstreams: an unapplied script must break only the new screens,
never the Tasks list or the Calendar that everyone already uses. An event with no schedule row behaves
exactly as it does today.

### 2.10 Templates create tasks THROUGH `ITaskService`

So every templated task raises the same events and sends the same notifications as a hand-made one.
There is no second task-creation path to keep in step. Asserted by a test that reads `BusinessEvents`.

---

## 3. Verification

| Gate | Result |
|------|--------|
| Debug build | **0 errors** |
| TestRun build | **0 errors** |
| Release build | **compiles with 0 errors**; output copy blocked — see §4.3 |
| Task ecosystem tests | **25 / 25** |
| Calendar recurrence tests | **10 / 10** |
| Calendar conflict / availability / validation | **17 / 17** |
| Visual conformance (11 screens × 6) | **66 / 66** |
| Tasks focused suite | **165 / 165** |
| Calendar focused suite | **181 / 181** |
| Calendar authorization | **14 / 14** |
| Full application suite | **1677 passed, 0 failed**, 183 skipped |

### Mutation proof

Three controlled mutations, built and run, then restored:

| Mutation | Caught by | Collateral |
|----------|-----------|-----------|
| Cycle guard neutered | `A_two_node_cycle_is_refused`, `A_long_cycle_is_refused_and_the_offending_chain_is_named` | none |
| Reorder → delete-and-reinsert | `Reordering_keeps_line_identity_and_its_done_state` | none |
| Duplicate-edge guard neutered | `The_same_edge_cannot_be_added_twice` | none |

4 failures, exactly the 4 predicted; 21 unrelated tests stayed green. Restored, 25/25 green, no marker
left in the tree.

**A defect the analyzer caught in my own work:** the first pass added ten mutating endpoints with no
authorization. `CBA001` refused the build. They are now gated through `ITasksAccessService` —
and for checklist lines and dependency edges the owning task is resolved **from the row**, because
authorizing against a task id the caller *handed us* would authorize the wrong task. Both ends of a
dependency edge are authorized, since an edge changes what each of them waits for.

---

## 4. Declared blocks and deviations

### 4.1 Task Attachments — CLOSED, using the existing storage infrastructure

No new storage model and no second attachment system:

- **Bytes** go to `wwwroot/uploads/library/<companyId>/<guid><ext>` — the *same* directory and the same
  naming `FileManagerController.Upload` writes to. The file is also registered through
  `IFileManagerService.AddFileAsync`, so a file attached to a task is findable in File Manager rather
  than existing only as a row in a comment table.
- **The reference** is that web path, passed as the official `CommAttachmentRequest.StorageKey`. The Comm
  platform stores no bytes and resolves no url by contract (ADR-030 §8) — the deployment's file store does,
  and this is that store.
- **The attachment rows** are `CommCommentAttachments`, written by the platform's own
  `CommAttachmentService`, which enrols in the comment's transaction. A file never survives a comment
  that failed.

**One consequence, stated plainly:** the task discussion moved from `DocComments` to the Comm platform.
`DocComments` has no attachment table, so attaching there would have meant inventing a link — exactly the
second attachment system that is forbidden. `/Tasks/TaskComments` now reads the Comm thread for
`entityType=Task`.

### 4.2 Calendar Recurrence editing — CLOSED, with a real access service

The root cause was that `CalendarEvent` had `PermissionScope = ScopeNone`, so `DefaultPermissionAdapter`
denied every action except View. The fix is the one the platform asks for — give Calendar a policy:

| Piece | What it does |
|-------|--------------|
| `ICalendarAccessService : IModuleAccessService` | The calendar's permission policy |
| `CalendarAccessService : ModuleAccessServiceBase` | Owner / attendee / company-scope / hierarchy rules |
| `EntityRegistry.ScopeCalendar` | A real scope, published in `PermissionScopes` |
| `CalendarPermissionAdapter` | Routes platform actions to the module |
| `CalendarEvent.PermissionScope` | `ScopeNone` → `ScopeCalendar` |

**The analyzer was not weakened, not suppressed, and gained no exception.** `AuthorizationResolver`
walks `AllInterfaces`, and `ICalendarAccessService` inherits `IModuleAccessService`, which is already a
declared authority — so a real policy call satisfies CBA001 with no analyzer change and no baseline growth.

**The rule that carries the risk:** an attendee may READ a meeting but may NOT EDIT it. Being invited is
not permission to move it — rescheduling would silently change the event for everyone else invited.
Asserted by `An_attendee_may_READ_the_meeting_but_may_NOT_EDIT_it`.

Two guards caught incomplete work along the way, both of them correctly: `PermissionScopeStartupValidator`
refused to boot while `ScopeCalendar` was a constant but not in `PermissionScopes`, and
`Stage1DiWiringTests` failed until Calendar was added to the real DI graph.

### 4.3 Release output copy blocked

`bin\Release\net8.0\CrossBuy.dll` is locked by *Microsoft Visual Studio (9152)* and *IIS Express
Worker Process (30216)*. Per the standing instruction I did not terminate them. **Compilation is
clean at 0 errors**; only the copy to `bin\Release` failed. Re-run after stopping both to complete
the gate.

### 4.4 UI authority baseline — CLOSED by a dedicated commit

Commit `2645d16`, containing **exactly one file** and no feature change, as the baseline's own
`_reblessing` rule requires. `tasks-views` and `calendar-views` were re-blessed because they gained five
screens. **`inventory-views`, `inventory-shell` and `brand-stylesheet` did not drift** — the authority
itself is untouched; only the modules measured against it changed.

The aggregates were recomputed with the algorithm the file documents, and verified by reproducing the
three UNCHANGED hashes byte-for-byte before writing the two new ones — so the recomputation is checked
against known-good values rather than trusted.

The test was not disabled and the baseline was not removed.

### 4.5 FullCalendar resource-timeline is a premium plugin

Not in the Metronic bundle. The timeline and resource views are Inventory tables (people/resources ×
hours) rather than a paid dependency — which also keeps them inside the one visual language.

---

## 5. Deploy SQL (idempotent, NOT executed)

- `CrossBuy/deploy/sql/tasks_ecosystem_checklist_dependencies_templates.sql` — 4 tables
- `CrossBuy/deploy/sql/calendar_scheduling_recurrence_resources.sql` — 3 tables

Both additive, both safe to run repeatedly, neither run against any database by this session.

---

## 6. Screens registered in the conformance guard

`Tasks/Board`, `Tasks/Templates`, `Tasks/Detail`, `Calendar/Timeline`, `Calendar/ResourceView` were
added to `AllScreens()` **at the moment they were created**. A screen that is not registered is a
screen that is not protected.
