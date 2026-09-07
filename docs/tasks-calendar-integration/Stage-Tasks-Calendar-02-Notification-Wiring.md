# Stage-Tasks-Calendar-02 — Notification Wiring (Phase 1)

**Implemented.** `CrossBuy/BL/TaskService.cs` now calls `ITaskNotificationService` on every supported transition.

---

## 1. What changed

The previous increment built the notification service but called it only from the overdue sweep, so assignment,
reassignment, completion and reopening still notified nobody. That gap is closed.

| Transition | Where | Event(s) published | Notified |
|---|---|---|---|
| create | `SaveAsync` (new) | `Task.Created` + `Task.Assigned` | assignee |
| assign (was unassigned) | `SaveAsync` (edit) | `Task.Assigned` | assignee |
| reassign | `SaveAsync` (edit) | `Task.Reassigned` | new **and** previous assignee |
| due-date change | `SaveAsync` (edit) | `Task.DueDateChanged` | assignee |
| complete | `ChangeStatusAsync` | `Task.StatusChanged` + `Task.Completed` | creator |
| reopen | `ChangeStatusAsync` | `Task.StatusChanged` + `Task.Reopened` | assignee |
| overdue | `TaskOverdueSweepService` (TM-7 worker) | `Task.BecameOverdue` | assignee |
| cancel | contract + notification path exist | `Task.Cancelled` | assignee + creator |

**`Task.Cancelled` is defined but unreachable today.** `TaskItem.Status` is `New | InProgress | Done` — there is no
`Cancelled` value to transition to. Adding one changes the task status vocabulary, which is a behaviour change this
increment did not make. Stated rather than implied.

## 2. The ordering rule, and why it is not negotiable

```
open ScopedTx  →  SaveChangesAsync  →  RecordAsync (events)  →  COMMIT  →  notify
```

Notifications are sent **after the commit**, never inside it. A notification is a message that something *has
happened*; sending it inside the transaction would announce a change that could still roll back — and unlike a
database row, a notification cannot be rolled back.

Events go the other way: **inside** the transaction, so they cannot survive a rollback (doc 03).

## 3. Nothing is notified for something that did not happen

Three refusal paths return before any write:

| Path | Result |
|---|---|
| validation failure (`SaveAsync`: blank title, no assignee, bad scheduled criteria) | returns early — no event, no notification |
| unknown or **disallowed** status transition | returns early — no event, no notification |
| status set to its current value | returns `(true, null)` early — idempotent success, no event, no notification |

Asserted by `A_rejected_save_writes_no_event_and_no_notification`,
`A_refused_transition_publishes_nothing_and_notifies_nobody` and
`Setting_the_same_status_again_changes_nothing`.

## 4. No notification for an unchanged value

`SaveAsync` captures `previousAssignee` and `previousDue` **before** mutating, then compares. A save that changes
only the title produces no `Assigned`, no `Reassigned` and no `DueDateChanged`, and notifies nobody —
`Saving_with_no_change_publishes_no_transition_event_and_notifies_nobody`.

## 5. Properties carried from the previous increment, still enforced

- **Company isolation** — company is read from the task row; a recipient must be an active employee of that company.
- **Actor/recipient separation** — the actor is never notified of their own action.
- **Idempotency survives read status** — the service checks the `Notifications` table for any row with the dedup key,
  read or unread, because the platform's own `DedupKey` only suppresses *unread* duplicates.
- **Correlation-based grouping** — one user action produces one `CorrelationId` across every event and notification
  it produces.
- **No second notification engine** — everything goes through the existing `INotificationService`.

## 6. Dependency choice, stated

`TaskService` now takes `ITaskCalendarEventPublisher` and `ITaskNotificationService` as **required** constructor
dependencies, not optional ones. A transition that silently skipped its event or notification because a dependency
was absent would be the worst outcome: the behaviour would look wired and would not be.

Both services were previously DI-only (no `new TaskService(` anywhere in the solution), so the constructor change
broke no call site.

## 7. Tests

`TasksCalendarTransitionTests` — 11 tests covering each transition, both refusal paths, the unchanged-value case,
and the transactional guarantees. Full list in doc 06.
