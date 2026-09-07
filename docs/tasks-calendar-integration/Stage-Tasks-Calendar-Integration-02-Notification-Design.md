# Stage-Tasks-Calendar-Integration-02 — Notification Design

**Implemented.** `CrossBuy/BL/TasksCalendar/TaskNotificationService.cs`

---

## 1. What existed before

Nothing. A grep for `NotifyAsync`/`NotifyRoleAsync` across `TasksController`, `TaskService`,
`TaskGeneratorService` and `TaskScheduleMatcher` returned no match. Assignment, reassignment, due-date change,
overdue, completion and reopening were all silent.

## 2. What was built — and what it deliberately is not

A **producer** over the existing `INotificationService`. It creates no table, no queue, no delivery channel, no hub
call and no second engine. It decides **who is told what**, and hands that to the platform.

Direct `NotifyAsync` is the correct pattern here, not a shortcut: ADR-006 retains direct producers for entities that
are **not** onboarded to the event kernel, and `Task` is not onboarded. When it is, these calls must be **removed in
the same change** that adds the projection — leaving both double-notifies, which is exactly why ADR-006 records that
the inline `NotifyRoleAsync` was deleted when its projection landed.

## 3. The seven notification kinds

| Kind | Recipient | Why that recipient |
|---|---|---|
| `task_assigned` | assignee | the most-missed notification in the product |
| `task_reassigned` | new **and** previous assignee | telling only one leaves somebody believing they still own it |
| `task_due_date_changed` | assignee | |
| `task_became_overdue` | assignee | priority `High`; produced by the sweep (doc 07 §3) |
| `task_completed` | **creator**, not assignee | the assignee usually completed it; the person who asked is the one who wants to know |
| `task_reopened` | assignee | work has come back |
| `task_cancelled` | assignee + creator | |

`Task.Created` and `Task.StatusChanged` notify **nobody** by design — they are timeline facts. Notifying on every
status change is how people stop reading notifications.

## 4. Idempotency — the requirement, and why the platform's own guard was not enough

`Notification.DedupKey` is documented in the platform as an **unread-noise** guard: `NotifyAsync` skips when an
*unread* row with the same key exists. That is not idempotency — once the recipient reads it, a retry delivers a
second copy.

So this service checks the `Notifications` table for **any** row with the same key, read or unread, before calling
`NotifyAsync`. That is a read of a platform table, not a write, and it adds no schema.

The key is deterministic:

```
task:{companyId}:{taskId}:{kind}:{recipientId}:{occurrence}
```

`occurrence` is what makes it correct rather than merely unique:

| Kind | Occurrence | Consequence |
|---|---|---|
| assigned | `"assign"` | one per task, however many retries |
| reassigned | `"reassign:{previousAssigneeId}"` | a second reassignment is a new fact |
| due date changed | `"due:{previous}->{new}"` | moving twice notifies twice; retrying one move notifies once |
| **overdue** | `"overdue:{dueDate}"` | **keyed on the missed DATE, not the sweep time** — a 15-minute worker notifies once |

Proven by `Idempotency_survives_the_recipient_reading_the_notification` and
`A_repeat_of_the_same_event_does_not_notify_twice`, which also asserts that a **different correlation id still does
not re-notify**: the occurrence, not the call, is what is unique.

## 5. Authorization and isolation

- **Company comes from the task row.** Never from a caller argument, and there is no fallback.
- **A recipient must be an ACTIVE employee of the task's company.** An employee of another company is never told a
  task exists — not even its title (`An_employee_of_another_company_is_never_notified`).
- **A recipient must stand in a real relationship to the task** — assignee, previous assignee or creator. Those are
  the people the task itself authorizes, so this service cannot become a way to push a task title to an arbitrary
  employee.
- **The actor is never notified of their own action** (`The_actor_is_never_notified_of_their_own_action`).
- **A task whose assignee is also its creator is notified once, not twice.**

## 6. Payload discipline

The body carries the **title only**. `TaskItem.Description` is never included — asserted by
`A_notification_body_carries_the_title_and_never_the_description`, which seeds a description containing
`CONFIDENTIAL` and proves it does not reach the notification.

## 7. Deep link — a contract, not an invented route

`/Tasks/Index?taskId={id}`. Tasks has a list screen and **no per-task detail screen**, so
`/Tasks/Details/{id}` would ship a dead link. The query form is honest today and survives whatever detail route is
added later.

## 8. What is NOT claimed

This persists an in-app notification and lets the platform push it. **No email, push or WhatsApp delivery is claimed
or implemented** — there is no adapter here that could confirm one. `TaskNotificationOutcome.Result` reports what
actually happened: `Delivered` | `Duplicate` | `NotAuthorized` | `SelfActor`.

## 9. Wiring status — stated plainly

The service is registered in DI and fully tested. **It is called today only by the overdue sweep.** Calling it from
`TaskService.SaveAsync`/`ChangeStatusAsync` for assignment, completion and reopening is a change to those methods'
behaviour, which this increment deliberately did not make: the brief limits it to implementing the notification
capability, and the owner should decide whether task mutation begins notifying in the same release. That call site is
one line per transition and is the first item in doc 08's next steps.
