# Stage-Tasks-Calendar-02 — Agenda Evidence (Phase 4)

**Implemented previously; re-verified here, unchanged.** `CrossBuy/BL/TasksCalendar/WorkspaceAgendaService.cs`

---

## 1. Status

`IWorkspaceAgendaService` was completed in the previous increment and needed no change. It is re-verified against
the current tree: **16 agenda tests, 0 failures**, within the 74 focused tests.

## 2. The guarantees, and the test that proves each

| Requirement | Test |
|---|---|
| Task due dates remain Task-owned | `A_task_appears_as_a_task_and_is_never_turned_into_a_calendar_event` |
| Calendar events remain Calendar-owned | same test — the calendar source is consulted and produces nothing |
| **no duplicated rows** | `The_same_work_never_appears_twice` |
| date-range filtering | `A_date_range_excludes_what_falls_outside_it` |
| last day fully covered | `An_item_late_on_the_last_day_of_the_range_is_included` |
| stable ordering | `Ordering_is_deterministic_when_two_items_share_an_instant` |
| paging | `Paging_is_stable_and_does_not_repeat_a_row_across_pages` |
| company isolation | `An_unresolved_company_or_employee_is_refused_rather_than_defaulted` |
| permission filtering | `Somebody_elses_personal_event_is_shown_as_busy_with_its_detail_withheld` / `My_own_personal_event_is_not_redacted` |
| timezone conversion | `A_timed_item_carries_an_explicit_offset` |
| all-day preservation | `An_all_day_event_keeps_its_date_and_carries_no_offset` |
| unavailable-source reporting | `A_failing_source_degrades_the_agenda_visibly_instead_of_silently` |
| task scope never widened | `Task_scope_is_never_widened_by_the_agenda` |
| completed tasks excluded unless asked | `A_completed_task_is_excluded_unless_it_is_asked_for` |

## 3. No Task becomes a Calendar row

There is **no write path in the file at all** — no `Save`, no `DbSet`, no `CrossDbContext`. A task appears as
`AgendaItemType.Task` carrying the **task's own id**, never a synthesised event id.

## 4. Why the tests use fakes, and what that proves

The agenda tests substitute `ITaskService` and `ICalendarService` with fakes. That is not mocking away the subject —
it is what makes *"no task rule and no calendar rule is re-implemented here"* provable: a service that queried
`TaskItems` or `CalendarEvents` directly would return nothing under these fakes and every test would fail.

## 5. Handover to TAB 3 — now partly consumed

During this increment TAB 3 added `GetAgendaAsync` to their own `IWorkspaceService`, so the shell is building its
agenda surface. **No Workspace file was modified by this tab**, in either increment.

What is asked of TAB 3 remains: resolve `IWorkspaceAgendaService` and call `GetAgendaAsync` with the signed-in
employee's company, employee id and timezone. What must not happen: querying `CalendarEvents` directly, which would
put the organiser/attendee visibility rule in a second place.
