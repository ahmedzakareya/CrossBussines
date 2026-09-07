# Stage-Tasks-Calendar-02 — Business Event Evidence (Phase 3)

**Publishing.** `CrossBuy/BL/TasksCalendar/TaskCalendarEventPublisher.cs`, called from `TaskService` inside its
transaction.

---

## 1. A latent defect the previous increment could not have found

Three contracts were **unpublishable as written**:

| Was | Now |
|---|---|
| `CalendarAttendee.Added` | `CalendarEvent.AttendeeAdded` |
| `CalendarAttendee.Removed` | `CalendarEvent.AttendeeRemoved` |
| `CalendarReminder.Triggered` | `CalendarEvent.ReminderTriggered` |

`BusinessEventTypes.TryValidate(eventType, entityCode)` requires the name's prefix to **be** the entity code.
`CalendarAttendee` and `CalendarReminder` are not registered entity codes and never will be — they are facets of a
calendar event, not aggregates.

A contracts-only increment cannot surface this: nothing validates a name until something tries to publish it.
Attempting to publish is what exposed it. Pinned now by
`A_registered_event_name_validates_and_a_mismatched_one_does_not`, which asserts the old shape is **rejected**.

## 2. Transactional publishing — the guarantee, and how it is proved

```csharp
await using (var tx = await ScopedTx.BeginOrJoinAsync(_db))
{
    await _db.SaveChangesAsync();          // the state change
    await _events.TaskCreatedAsync(...);   // RecordAsync — inside this transaction
    await tx.CommitAsync();                // one commit for both
}
```

The publisher opens **no transaction of its own**. `RecordAsync` throws without an ambient one, so the caller must
provide it — which is what makes "no event on rollback" true by construction rather than by convention.

`ScopedTx.BeginOrJoinAsync` **joins** an existing transaction rather than nesting, so an outer transaction the caller
controls keeps the commit decision.

**Proved two ways, both against the real `BusinessEventService` — not a stub:**

- `An_event_and_its_state_change_are_one_commit` — after a save, a **new context** sees both the task and its two
  events.
- `No_event_survives_a_rolled_back_outer_transaction` — an outer transaction is opened, the task is saved, its events
  are visible *inside* the transaction, the transaction is rolled back, and a new context then sees **neither the
  task nor any event**.

A stub would have recorded the call and proved nothing about the transaction. That is why the real service is used.

## 3. What is published today

**Task (7 of 9 reachable):** `Created` · `Assigned` · `Reassigned` · `StatusChanged` · `DueDateChanged` ·
`Completed` · `Reopened` · plus `BecameOverdue` from the sweep.

**`Task.Cancelled` is not reachable** — `TaskItem.Status` has no `Cancelled` value (doc 01 §1).

**Calendar (6 defined, publisher methods implemented, not yet called):** `Created` · `Updated` · `Rescheduled` ·
`Cancelled` · `AttendeeAdded` · `AttendeeRemoved`.

**Stated plainly:** the Calendar publisher methods exist, compile and are correct, but **`CalendarService` does not
call them yet.** Wiring them means restructuring `CalendarService.SaveAsync` — which also carries the attendee
delete-and-reinsert defect (TC-G-08). Adding events on top of a save that destroys attendee identity would produce
`AttendeeRemoved`/`AttendeeAdded` pairs on every unrelated edit — noise that looks like real churn. **That fix comes
first**, and it is the top item in doc 07's next steps.

## 4. Payload discipline — enforced, not just declared

Payloads are built field by field. Nothing is serialised wholesale: passing the entity would leak `Description`,
`BillRate`, `CustomerId` and the attendee list into a payload the timeline renders to more people than the record.

`No_task_event_payload_carries_the_description_or_commercial_data` seeds a task with
`Description = "CONFIDENTIAL: budget overrun detail"`, `BillRate = 999`, `CustomerId = 77` and asserts **none** of
them appears in any stored payload.

Calendar payloads carry an attendee **count**, never the list, and an `attendeeEmployeeId` — never an email address.

## 5. Envelope

`Every_published_event_carries_company_entity_actor_and_a_utc_instant` asserts, on every stored row:
`CompanyID` · `EntityType` · `EntityId` · `ActorEmployeeId` · a real UTC instant · a non-empty `CorrelationId` —
and that **one user action produces exactly one correlation id** across all its events.

## 6. Idempotency

Every event carries a deterministic `DedupKey`: `{eventType}:{companyId}:{entityId}:{occurrence}`. `RecordAsync`
returns the existing `EventId` when the key matches, so a retried save records one event.

For overdue, the occurrence is the **missed due date**, not the sweep time — so a 15-minute worker records one event
per missed date, and a rescheduled-then-missed task is a genuinely new occurrence.

## 7. No consumers

None were implemented. Timeline, Communication, Reporting and AI consumers belong to their own tabs.
