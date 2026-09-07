# ADR-001 — Business events are written inside the business transaction

**Status:** Accepted. Slice 1 implemented it for one producer; slice 2 applied it to five more, in two services
that had no transaction at all.

## Context

CrossBuy has a firm, documented convention for notifications, followed by ~25 call sites across 12 BL services:

```csharp
await tx.CommitAsync();
try { await _notify.NotifyRoleAsync(...); }
catch { /* notifications never block the business flow */ }
```

That convention is correct **for notifications**. A dropped bell is an annoyance; blocking a sale because a
notification failed would be worse.

The Business Event Platform was initially specified the same way — events consumed by Timeline, Notifications,
Audit, Dashboards, AI and Search. But the same table was also to be the audit trail and "the history of
business state".

Those two requirements are incompatible. An after-commit write inside a swallowing `catch` can fail silently,
which means:

* an event can be missing for an operation that succeeded, and
* an event can exist for an operation that was rolled back (if the write happened before a later failure).

An audit trail that can silently drop or invent rows is not an audit trail. Every consumer downstream —
timeline, dashboards, AI context — inherits that unreliability.

## Decision

**Business events are written inside the business transaction. Consumers run after it commits.** The
transactional-outbox pattern.

1. `IBusinessEventService.RecordAsync` enrols in the caller's ambient `ScopedTx`. It never opens or commits a
   transaction of its own — `ScopedTx.BeginOrJoinAsync` already gives every site a join-or-own handle, so the
   event simply participates in whatever unit of work the producer established.
2. The event and the business fact commit or roll back **together**.
3. `RecordAsync` is **not** wrapped in a swallowing `try/catch` at the call site. If the event cannot be
   written, the business transaction fails.
4. Calling `RecordAsync` with **no ambient transaction throws**. This turns the rule from a review convention
   into a mechanical guarantee — an event that could outlive a rolled-back operation is refused outright.
5. `RecordAsync` performs **no side effects**: no notification, no AI, no search indexing, no workflow. That is
   what makes it safe to call inside a financial transaction. Fan-out happens afterwards, through the outbox.
6. Dispatch rows are created in the same transaction, so a consumer can never miss an event that was recorded
   while the consumer was offline.

The notification convention is **unchanged**. In `ReceivableService.CreateSalesInvoiceAsync` the two now sit a
few lines apart — `RecordAsync` before `CommitAsync` with no catch, `NotifyRoleAsync` after `CommitAsync` inside
a catch — so the difference in guarantee is visible at the call site rather than buried in a document.

## Consequences

**Accepted costs**

* A producer must be inside a transaction to record an event. For the pilot this was free: both sales-invoice
  paths already wrap everything in one `ScopedTx`. A future producer with no natural transaction must open one.
* `RecordAsync` calls `SaveChangesAsync` on the shared request `DbContext`, which also flushes the caller's
  pending tracked changes. Mitigated by placing the call at a point where everything is already saved —
  immediately before `CommitAsync`. Producers must respect that placement.
* An event-contract bug now fails a business operation instead of being swallowed. This is intended: the
  failures it can cause are loud and immediate, and every contract check (`unknown entity code`, `non-canonical
  event type`, `oversized payload`) is deterministic and covered by tests, so they surface in development
  rather than production.

**Gained**

* The event log is genuinely durable, which is the precondition for every consumer that reads it.
* Consumer failures are isolated from the business transaction and retried independently (ADR-003).

## Alternatives rejected

* **After-commit + try/catch, matching the notification convention.** Rejected: produces an audit trail that
  can silently lose and invent rows.
* **Best-effort write plus a periodic reconciler** that compares documents against events. Rejected: the
  reconciler can only detect *missing* events for rows it knows how to inspect, cannot reconstruct actor,
  correlation or payload, and grows a bespoke checker per entity — all to approximate what one in-transaction
  insert gives exactly.
* **A separate `DbContext`/connection for events**, so event writes cannot affect the business transaction.
  Rejected: that is precisely the property we do *not* want. It restores independent failure.

## Slice 2 addendum — what applying this rule to five more producers cost

Three findings worth recording, because they are what the rule looks like in practice:

1. **Two write paths had no transaction at all.** `ReceivableService.CreateCustomerAsync` /
   `SaveCustomerAsync` and `ManufService.CreateAsync` / `SaveHeaderAsync` each did one or more bare
   `SaveChanges`. `ScopedTx.BeginOrJoinAsync` made adding one free: with no ambient transaction it owns a new
   one, so behaviour for existing callers is byte-identical, and it joins when a caller already has one.
2. **The staged manufacturing lifecycle needed an OUTER transaction, not an edit to the writer.**
   `Release`/`Cancel`/`Complete`/`ProducePartial` change state inside `StockService`, the sole stock + GL writer,
   which owns its own `ScopedTx`. Each `ManufService` wrapper now opens an outer `ScopedTx` that
   `StockService`'s inner one joins — the fact and the event commit together and the writer is untouched. This
   is the own-or-join design being used for exactly what it was built for.
3. **It closed a latent gap.** `StockService.CancelWorkOrderAsync` has an early-return path (nothing issued →
   no GL) that committed with **no transaction of its own**. The outer transaction now covers that path too, so
   a cancellation is atomic whether or not it moves stock.

The rule that `RecordAsync` refuses to run without an ambient transaction is what made all three visible
immediately rather than at review time.

## Verification

* `Event_is_rolled_back_with_the_business_transaction`
* slice 2: `Customer_event_is_rolled_back_with_its_transaction` ·
  `An_event_and_its_dispatch_rows_roll_back_together_on_SQL_Server` (real SQL Server) ·
  `A_real_work_order_transition_through_the_service_records_one_event` (through the real service, own ScopedTx)
* `Event_and_its_dispatch_rows_survive_a_commit_together`
* `A_contract_failure_propagates_and_takes_the_business_data_with_it`
* `Recording_outside_a_transaction_is_refused`
* `Duplicate_dedup_key_returns_the_original_event_and_writes_nothing`
