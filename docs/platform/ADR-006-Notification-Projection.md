# ADR-006 — Notifications become an outbox consumer, and the legacy producer is removed at the same time

**Status:** Accepted, implemented in Platform Kernel slice 2.

## Context

Slice 1 shipped one consumer (`TimelineProjection`). ADR-003 argued that per-consumer dispatch state matters
because consumers fail independently — but with a single consumer that argument was untested in production.

Notifications were the obvious second consumer, and the obvious hazard: notifications **already exist**. Two
producers fire immediately after the transactions that now record events:

| Location | Notification |
|---|---|
| `ReceivableService.CreateSalesInvoiceAsync` | "New sales invoice" → `acc` / `ChiefAccountant`, type `sales_invoice` |
| `PayableService.CreatePurchaseInvoiceAsync` | "New purchase invoice" → `acc` / `ChiefAccountant`, type `purchase_invoice` |

Adding a consumer that maps the same events to the same notifications, without touching those, would
**double-notify every accountant**.

Two further problems surfaced on inspection:

* `NotificationService.NotifyAsync`'s `dedupKey` check filters on **unread** rows only. It is a noise guard, not
  idempotency — a redelivery after the user has read the notification creates a second one. An outbox consumer
  can be redelivered (retry, stale claim), so it cannot rely on it.
* `Notification` had no way to say **which entity** it was about. `Type` is a catalog key (a meaning) and
  `RefId` is untyped, so a click-through URL could only come from a hand-maintained `switch`.

## Decision

**Add `NotificationProjection` as a real consumer, remove the two legacy producers it replaces, and keep the
decision separate from the delivery.**

### 1. Three collaborators, one responsibility each

* `IBusinessEventNotificationMapper` — a **pure function** of the envelope: what to say, to whom, about what.
  No database, no permissions, no idempotency, no delivery. Returning an empty list is a normal, successful
  answer, and most events return one.
* `NotificationProjectionConsumer` — resolves the audience, authorizes it, checks idempotency, and delivers.
* `NotificationService` — unchanged owner of persistence, the SignalR push and the mute rules.

The worker holds **no switch statement**; it does not know notifications exist.

### 2. Duplicate prevention: remove the legacy call

The **preferred** option in the brief, taken deliberately over a feature flag. Both legacy
`NotifyRoleAsync` calls were deleted, each replaced by a comment at the exact site explaining what now produces
it. The mapper reproduces the same audience, the same catalog `Type` and the same wording, so users see no
change other than the click-through now resolving through `IEntityRegistry`.

A flag was rejected: it would leave two code paths for one user-visible behaviour, and "which one is live?"
becomes a deployment question rather than a code question.

**Legacy notification producers that remain, and why** (all inspected, none duplicated by an event):

| Producer | Kept because |
|---|---|
| `ReceivableService` stale-exchange-rate warning | No event exists for it — it is a data-quality warning, not a business fact |
| `ReceivableService` credit-limit block | Fires on a **rejected** invoice. No business fact exists to record, so there is no event to map |
| All other producers across the 12 BL services | Their entities are not onboarded. Converting them is not this slice's scope |

### 3. Idempotency is the consumer's own

`DedupKey = "evt:{EventUid:N}:{recipientId}"` — derived from the event's own identity, which never changes
across retries and is unique per event. Before delivering, the consumer queries `Notifications` for that key
**regardless of read state**. That check, not `NotificationService`'s, is the guarantee.

### 4. Recipients must be able to open the record

A notification is a link. The consumer runs every candidate through `IPlatformPermissionProvider` for `View`
before delivering, so nobody is handed a dead end or told a record exists that they cannot see. The actor is
excluded by default, mirroring `NotifyRoleAsync`'s existing `exceptEmployeeId`.

### 5. Two additive columns

`Notifications.EntityType` + `EntityId` (nullable), plus an index for the idempotency lookup. This makes a
notification entity-addressable the same way `BusinessEvents` is, and lets the URL come from
`IEntityRegistry.BuildUrl` instead of a hand-maintained map. `NotifyAsync` gained two optional trailing
parameters — the same additive technique the Comm-Hub P1 enrichment used, so all existing callers are unchanged.

### 6. What is mapped, and what is deliberately not

| Event | Notification | Reason |
|---|---|---|
| `SalesInvoice.Created` | ✅ `acc`/ChiefAccountant | replaces the removed legacy producer |
| `PurchaseInvoice.Created` | ✅ `acc`/ChiefAccountant | replaces the removed legacy producer |
| `ManufWorkOrder.Released` | ✅ `inv`/InventoryManager + WarehouseKeeper | new — manufacturing had never had a notification |
| `ManufWorkOrder.Completed` | ✅ `inv`/InventoryManager | new |
| `Customer.Created` / `.Updated` | ❌ | master-data edits are routine and high-volume; no legacy notification existed, and notifying on every one is noise |
| `SalesInvoice.Updated` / `PurchaseInvoice.Updated` | ❌ | no legacy notification existed for edits |
| `ManufWorkOrder.Created` / `.Updated` | ❌ | the actor's own routine work |
| `ManufWorkOrder.Produced` | ❌ | fires many times per order |
| `ManufWorkOrder.Cancelled` | ❌ | no established audience; adding one is a product decision, not a technical default |

## Consequences

* **ADR-003 is now proven in production, not just in tests**: `TimelineProjection` can be `Done` while
  `NotificationProjection` is `Failed`, retry touches only the failed row, and `CompletedAt` waits for both.
* `RecordAsync` creates two dispatch rows per event instead of one. That is the design (fan-out is decided at
  record time so a consumer offline at that moment still has its work waiting).
* Notification volume for work orders is new behaviour, visible to inventory staff. Deliberate: the events are
  real and the audience is the RBAC that governs those screens.
* **HONEST LIMITATION.** The per-recipient permission loop is not per-recipient *today*: the accounting and
  inventory access services read the current employee from the HTTP **session**, and the dispatcher has none,
  so their answer is not specific to the candidate. Two things keep it correct rather than theatre — the
  audience is already the module's own authorization data (`AccountingUserRoles` / `InventoryUserRoles`), so a
  `ChiefAccountant` is authorized for accounting *by construction*; and the call goes through the one
  abstraction, so the day an adapter reads its employee from `BusinessContext` this loop starts enforcing with
  no change at the call site. Making it truly per-recipient now would mean rewriting the four access services.
* A notification suppressed by a user's category **mute** is a success, not a failure — `NotificationService`
  owns that rule and the consumer does not second-guess it.

## Alternatives rejected

* **Keep the legacy producers and gate the consumer behind a flag.** Rejected — see §2.
* **Call `NotifyRoleAsync` from the consumer.** Rejected: it resolves the audience and writes in one step, so
  it cannot give each recipient its own dedup key or run a per-recipient permission check. The consumer reads
  the same role tables and calls `NotifyAsync` per recipient instead.
* **Put the mapping switch in the dispatch worker.** Rejected: the worker is consumer-agnostic infrastructure;
  a per-event-type switch there would grow forever and couple the outbox to notifications.
* **Rely on `NotifyAsync`'s existing `dedupKey`.** Rejected: unread-only, so not idempotent under retry.
* **Send email/SMS/push from the consumer.** Rejected: `NotificationService` owns delivery, including the
  SignalR push. The consumer persists through it and adds no channel of its own.

## Verification

* `It_creates_exactly_one_notification_with_the_platform_fields_populated` (URL via registry, CompanyID,
  EntityType/EntityId, actor, dedup key, category)
* `Redelivery_is_idempotent_even_after_the_notification_was_read` — the exact case `NotifyAsync` misses
* `The_actor_is_not_notified_about_their_own_action`
* `A_recipient_who_cannot_open_the_record_is_not_notified`
* `An_event_with_no_audience_is_a_successful_no_op`
* `A_timeline_only_event_produces_no_notification_and_does_not_fail`
* `A_notification_failure_leaves_the_timeline_consumer_Done_and_retries_only_itself` (+ `CompletedAt` waiting)
* `Work_order_events_notify_the_inventory_audience`
* `The_mapper_covers_exactly_the_event_types_this_slice_converted`
* SQL Server: `A_failed_notification_row_leaves_the_completed_timeline_row_alone`
