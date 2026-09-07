# ADR-003 — Dispatch state is per consumer, and work is claimed by status

**Status:** Accepted. Slice 1 implemented it with one consumer; slice 2 added the second and validated the
design against a real SQL Server.

## Context

The event platform fans out to six consumers: Timeline, Notifications, Workflow, Dashboards, AI and Search
Index. The initial design carried a single `DispatchedAt` timestamp on `BusinessEvents`.

**A single timestamp cannot describe six independent consumers.** If search indexing fails while notifications
succeeded, one timestamp offers exactly two options: leave it null and redeliver to *everyone* (duplicate
notifications), or set it and lose the search index entry silently. Neither is acceptable, and the second
destroys the guarantee that one event stream serves every service.

A second, subtler problem sat in how work would be *selected*. The obvious dispatcher reads
`WHERE EventId > lastSeen`. That is unsafe:

* transaction A inserts and takes `EventId` 100, still open;
* transaction B inserts, takes 101, and **commits first**;
* the dispatcher reads up to 101 and advances its cursor;
* A commits. `EventId` 100 becomes visible — *below* the watermark. It is never dispatched.

Identity values are assigned at `INSERT` but become visible at `COMMIT`. This is not a rare interleaving here:
`ScopedTx` is explicitly built for nesting, so long overlapping transactions are the normal case. The same
hazard applies to any monotonic watermark, including one on `CreatedAt`.

## Decision

**One dispatch row per (event, consumer), and work is selected by status only.**

1. `BusinessEventDispatch(ID, EventId, Consumer, Status, Attempts, Error, UpdatedAt)`, unique on
   `(EventId, Consumer)`. It reuses the shape `CommMessage` already established for the email outbox
   (`Status`/`Attempts`/`Error`).
2. Rows are created **inside the business transaction** by `RecordAsync`, one per registered consumer, so a
   consumer offline at record time still has its work waiting.
3. `IEventDispatchStore` exposes **no cursor, no `MAX(EventId)`, no watermark**. The absence is part of the
   contract, not an implementation detail.
4. Status lifecycle: `Pending → Claimed → Done`, with `Failed` between `Claimed` and a retry. `Claimed` exists
   so claiming can be atomic.
5. Claiming on SQL Server is one statement:
   `UPDATE TOP (n) … SET Status='Claimed', Attempts=Attempts+1 OUTPUT inserted.* FROM BusinessEventDispatch
   WITH (ROWLOCK, READPAST, UPDLOCK) WHERE <eligible>`.
   `UPDLOCK` takes the update lock at read time (no lock-upgrade race), `READPAST` skips rows another worker
   holds rather than blocking, `OUTPUT` returns exactly the rows won.
6. Eligibility: `Pending`; or `Failed` past `RetryBackoffSeconds`; or `Claimed` past `StaleClaimMinutes` (the
   owning worker died) — always with `Attempts < MaxAttempts`.
7. A row that exhausts its attempts stays `Failed` **with its error text**, for an operator. Never deleted,
   never silently dropped. Errors are truncated to the `nvarchar(400)` column so a long stack trace cannot
   break the write.
8. `BusinessEvents.CompletedAt` is stamped only when every consumer for that event is `Done`. It is a
   **reporting roll-up** and must never be used to decide what to process.
9. `BusinessEventConsumers.Registered` contains only consumers that are **implemented**. Registering one
   without an implementation accumulates rows nothing drains; the worker logs an error if that state is ever
   reached.

The claiming index is filtered on `Status <> 'Done'`, so the work queue stays small forever regardless of how
large the event log grows — finished work leaves the index entirely.

## Consequences

* Each consumer retries independently; a failing one cannot duplicate or suppress another's work.
* Adding a consumer is one entry in `Registered` plus one `IBusinessEventConsumer`. Existing events do not get
  rows retroactively — a new consumer starts from the events recorded after it is registered. If a consumer
  ever needs history, that is an explicit backfill, not an accident of cursor arithmetic.
* One extra row per event per consumer. Bounded by the filtered index and the archival direction in PKS-001 §10.
* Dispatch order is not guaranteed. Acceptable here: the timeline is ordered by `CreatedAt` at read time, and a
  consumer that ever needs ordering can use `CorrelationId`.
* Persistence is behind the interface, so a real queue can replace the table without touching a consumer.
  `DispatchWorkItem` deliberately carries no payload for the same reason.

## Alternatives rejected

* **Single `DispatchedAt` on the event.** Rejected — the problem this ADR opens with.
* **A per-consumer cursor table (`Consumer, LastEventId`).** Rejected: smaller, but still a watermark, so it
  still loses the late-committing lower id. It also cannot express per-row retry state or carry an error.
* **`SELECT` then `UPDATE` without lock hints.** Rejected: two workers can read the same row before either
  writes. The portable fallback in `SqlEventDispatchStore` does use a conditional update for providers without
  `READPAST` (the SQLite test host) and is documented as not a production path.
* **Deleting rows on success.** Rejected: loses the audit of what was dispatched and when, and makes
  `CompletedAt` unverifiable.

## Slice 2 addendum — the design's actual payoff

Adding `NotificationProjection` required **no schema change whatsoever**. One entry in
`BusinessEventConsumers.Registered`, one DI registration, one `IBusinessEventConsumer` — and every event from
that moment on got its second dispatch row automatically, with independent retry state. That is the whole
argument of this ADR reduced to a diff.

Two things that were previously only asserted in unit tests are now verified against a real SQL Server
(ADR-007):

* a worker **skips** a row another worker holds (`READPAST`) rather than blocking, and four concurrent workers
  partition a ten-row backlog with no row claimed twice and no `Attempts` inflated past 1;
* the out-of-order commit hazard this ADR opens with is **reproduced**: transaction A takes `EventId` 100 and
  stays open, B takes 101 and commits first, B is dispatched, then A commits — and 100 is still claimed.

One thing slice 2 confirmed by choosing *not* to do it: a new consumer starts from the events recorded **after**
it is registered. No `NotificationProjection` rows were back-filled for historical events, because that would
deliver a burst of notifications about work already finished. Recorded in the slice-2 deployment script.

## Verification

* `Consumers_have_independent_dispatch_state`
* `A_failing_consumer_does_not_reset_a_completed_one`
* `Pending_work_is_found_regardless_of_EventId_order`
* `An_event_whose_transaction_commits_late_is_still_dispatched` — reproduces the exact hazard with explicit ids
* `A_claimed_row_cannot_be_claimed_again`
* `A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_stale_window`
* `A_row_stops_being_retried_after_max_attempts`
* `Error_text_is_truncated_to_the_column_width`
* `An_event_is_completed_only_when_every_consumer_is_done`
* slice 2, in-process: `A_notification_failure_leaves_the_timeline_consumer_Done_and_retries_only_itself`
* slice 2, real SQL Server: `Only_one_worker_can_own_a_dispatch_row` ·
  `Concurrent_workers_partition_pending_rows_instead_of_colliding` ·
  `An_event_that_commits_after_a_higher_EventId_is_not_lost` ·
  `A_failed_notification_row_leaves_the_completed_timeline_row_alone` ·
  `A_claim_abandoned_by_a_dead_worker_is_reclaimed_after_the_timeout` ·
  `A_Done_row_is_never_claimed_again_even_when_stale`
