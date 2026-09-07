# Stage-Tasks-Calendar-Integration-07 — Test Evidence

**57 focused tests, all passing.** Full application suite: **1408 passed, 0 failed, 183 skipped.**

---

## 1. Files

| File | Tests | Covers |
|---|---|---|
| `CrossBuy.Tests/TasksCalendarIntegrationTests.cs` | 17 | notifications (11) + overdue sweep (6) |
| `CrossBuy.Tests/TasksCalendarTimeAndContractTests.cs` | 24 | time model (13) + registry/event contracts (11) |
| `CrossBuy.Tests/TasksCalendarAgendaTests.cs` | 16 | workspace agenda |

Storage is SQLite over a shared in-memory connection with the **real** `CrossDbContext` model — the same choice
`PlatformTestHost` makes, and for the same reason: it honours transactions, and the mapping under test is the
production mapping.

## 2. Notifications (Phase 10 group 1)

| Requirement | Test |
|---|---|
| assignment notifies the assignee | `Assignment_notifies_the_assignee_with_company_actor_and_deep_link` |
| reassignment notifies the correct recipients | `Reassignment_notifies_both_the_new_and_the_previous_assignee` |
| completion notification behaviour | `Completion_notifies_the_creator_and_not_the_actor` |
| **unauthorized user receives nothing** | `An_inactive_employee_is_not_notified` |
| **company mismatch receives nothing** | `An_employee_of_another_company_is_never_notified` |
| **retry does not duplicate** | `A_repeat_of_the_same_event_does_not_notify_twice` |
| retry does not duplicate *after the recipient reads it* | `Idempotency_survives_the_recipient_reading_the_notification` |
| the actor is not notified of their own action | `The_actor_is_never_notified_of_their_own_action` |
| one recipient, one notification | `A_task_whose_assignee_is_also_its_creator_is_notified_once_not_twice` |
| a real change notifies; a retry does not | `A_due_date_change_notifies_once_per_change_not_once_per_call` |
| no sensitive payload | `A_notification_body_carries_the_title_and_never_the_description` |

Two are worth singling out.

**`Idempotency_survives_the_recipient_reading_the_notification`** marks the row read and retries. The platform's own
`DedupKey` guards only *unread* rows, so without this service's own check a second copy would be delivered. This
test is the one that proves the requirement rather than the platform's approximation of it.

**`A_repeat_of_the_same_event_does_not_notify_twice`** also retries with a *different correlation id* and still
expects `Duplicate` — because the **occurrence**, not the call, is what must be unique.

## 3. Overdue (Phase 9 / 10)

| Requirement | Test |
|---|---|
| **one notification per overdue occurrence** | `An_overdue_task_notifies_once_however_often_the_sweep_runs` (three sweeps, one notification) |
| completion prevents overdue notification | `A_completed_task_is_never_reported_overdue` |
| due-date change recalculates correctly | `Moving_the_due_date_forward_makes_it_a_new_occurrence` |
| not overdue before it is due | `A_future_due_date_is_not_overdue` |
| company isolation | `The_sweep_touches_only_the_company_it_was_given` |
| unresolved company sweeps nothing | `An_unresolved_company_sweeps_nothing_rather_than_everything` |

**One owner, no third worker:** the sweep is invoked only from `TaskGeneratorHostedService`, inside its existing
per-company scope, in its own `try`/`catch` so a notification failure cannot stop TM-7 rule generation.

## 4. Registry onboarding + business events (Phases 3–6 / 10)

| Requirement | Test |
|---|---|
| fails closed, never permissive | `The_task_registration_request_is_scoped_and_never_permissive` |
| **capabilities pending until access proven** | `Comment_surfaces_stay_closed_until_an_access_proof_exists` |
| privacy ceiling enforced | asserted in the same tests (`InternalOnly`, public + external forbidden) |
| external principal deferred | `The_calendar_registration_defers_external_attendees_and_claims_no_module_scope` |
| exact event name and version | `Every_business_event_is_versioned_and_named_for_its_registered_entity` |
| correct company and entity ids | `Every_business_event_carries_the_full_envelope` |
| **no sensitive payload leakage** | `No_event_payload_carries_a_description_or_an_attachment`, `No_event_payload_carries_commercial_task_data` |
| required set complete | `The_required_event_set_is_complete` |
| all-day expressed structurally | `A_calendar_event_expresses_all_day_as_a_local_date_and_timed_as_utc` |
| reschedule distinct from update | `Reschedule_is_a_separate_event_from_update` |

Two Phase-10 items are **not** asserted here, and the reason is structural rather than an omission:
*"emitted only after successful state change"* and *"no event on denied/failed mutation"* describe a **raiser**, and
no event is raised in this increment. They become testable in the increment that raises them, and are recorded in
doc 08.

## 5. Time model (Phase 7 / 10)

UTC storage · explicit offset · all-day preservation · timezone conversion · no server-local ambiguity — 13 tests,
including both DST edge cases and a source-scanning guard that no new integration file calls `DateTime.Now`.

## 6. Agenda (Phase 8 / 10)

Combines Tasks and Calendar · **no duplicate materialised event** · permission filtering (redaction) · date-range
filtering · deterministic ordering · source-of-truth preservation — 16 tests.

## 7. Honest notes about the test approach

- **The notification recorder also persists a `Notification` row**, so the service's duplicate check — which reads
  the `Notifications` table — is exercised for real rather than mocked away.
- **The agenda tests use fakes for `ITaskService` and `ICalendarService` deliberately.** That is what makes "no task
  rule is re-implemented here" provable: a service that queried the database itself would return nothing under these
  fakes and every test would fail.
- **Two test-side defects were found and fixed during the run**, and neither was a product defect: the `Employee`
  seed omitted ten NOT NULL columns, and the `DateTime.Now` guard matched its own documentation prose before it was
  taught to strip comments.
- **A stale-assembly trap was hit and is worth recording:** a lingering `testhost.exe` held `CrossBuy.Tests.dll`
  open, MSBuild could not copy the fresh build, and `dotnet test` ran the **old** assembly — showing failures that
  the current code did not have. Every result in this document was taken after an explicit, verified build.
