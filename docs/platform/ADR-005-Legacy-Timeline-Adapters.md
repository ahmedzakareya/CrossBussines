# ADR-005 — Legacy history is reconstructed at read time, per entity, and never fabricated

**Status:** Accepted. Slice 1 established the pattern for one entity; slice 2 generalised it to four.

## Context

`BusinessEvents` starts empty. Every record that existed before the kernel has no events, so the moment a
timeline goes live on a screen, all historical records show a blank widget — a visible regression on exactly
the data users trust most. It also makes the claim "the event log is the history of business state" false for
everything older than the kernel.

Slice 1 solved this for `SalesInvoice` with one adapter. Slice 2 had to answer the harder question: does the
approach generalise, and what happens when an entity's history is **partly** recoverable or **not** recoverable?

The three pilots turned out to be three different cases:

| Entity | Historical source found | Quality |
|---|---|---|
| `Customer` | `Customer.CreatedAt` + **`Crm.Activity`** (linked by `CustomerId` *and* by `EntityType`/`EntityId`) | Genuine per-event history, and the only source that records a real actor |
| `PurchaseInvoice` | `PurchaseInvoice.CreatedAt` + `JournalEntries` where `SourceType='PurchaseInvoice'` | Creation + one item per re-post; same shape as the sales invoice |
| `ManufWorkOrder` | The row's **own lifecycle stamps**: `CreatedAt`, `ReleasedAt`, `CompletedAt`, `ClosedAt`, plus `JournalEntries` where `SourceType='WorkOrder'` | Richest — real per-status history |

## Decision

**Merge reconstructed history at read time behind one interface. Never backfill, and never invent a fact.**

1. `ILegacyTimelineAdapter` (slice 1) stays the only abstraction. Slice 2 added three implementations and
   changed nothing above them.
2. Adapters **write nothing**. There is no migration to reverse and no half-finished backfill that could
   double-count.
3. `TimelineProjectionService` merges, orders newest-first, and applies company / branch / permission /
   visibility filtering to the merged result — the adapters do not authorize.
4. **Deduplication rule (unchanged from slice 1):** the kernel is authoritative from the moment it recorded its
   first event for a record. A legacy item is dropped if its event type already appears among the real events,
   or if it is dated at/after the first real event. Anything strictly older is history the kernel never saw, so
   it is kept.
5. **Identity is deterministic** — `MD5("legacy:{code}:{id}:{eventType}:{discriminator}")`. A refresh must not
   look like new activity. Slice 2 lifted this, and the item builder, into `LegacyTimelineSupport` so four
   adapters cannot drift on the one thing dedup and the UI both depend on.
6. **A fact with no honest timestamp is not emitted.** This is the rule that keeps the whole mechanism
   trustworthy, and slice 2 forced it in three places:
   * a `Customer` row with `CreatedAt = NULL` (older rows allow it) produces **no** creation item rather than
     one dated "now";
   * a `Crm.Activity` with neither `CreatedAt` nor `DueDate` is skipped;
   * **a cancelled pre-kernel work order shows no cancellation.** `ManufWorkOrder` has `ReleasedAt`,
     `CompletedAt` and `ClosedAt` but **no** cancellation timestamp, so `Status='Cancelled'` is the only trace.
     The order shows its earlier history and simply stops.
7. **No actor is invented.** `ActorEmployeeId` stays null everywhere except `Crm.Activity`, which genuinely
   records `OwnerEmployeeId` — and there the adapter resolves the display name itself, because the projection
   only names actors for real event rows.
8. Reconstructed rows are `Source = Legacy`, badged "historical" in the UI, and their descriptions carry an
   explicit marker, so derived history is never mistaken for a recorded event.

### Sources deliberately NOT used

* **`DocComment`** — authored discussion, not derived history, and the comments widget already renders it on the
  screens where it is wired. Surfacing it here would duplicate that widget.
* **`Notification`** — delivery records. That a message was sent is not the business fact.
* **`ManufWorkOrderLabor`** — cost lines carrying employee cost data, which has no place in an
  `Internal`-visibility row.
* **`ManufWorkOrderComponent`**, `PurchaseInvoiceLine` — quantities and prices, not history.

## Consequences

* Old records show history on all four screens, with no data migration and nothing to roll back.
* Legacy items are **coarser** than real events. That is honest: the finer detail was never recorded. Partial
  productions before the kernel, for instance, survive only as a running `ProducedQty` total, so individual
  steps are unrecoverable.
* Every read pays the adapter's queries. Bounded and indexed, and it disappears per record as real events take
  over — but a persisted projection would remove it entirely if it ever matters.
* Four adapters is the point at which the pattern's cost becomes visible: each one needs its own source study.
  The interface is per-entity precisely so that study stays local and deletable.
* `CRM Activity`, `DocComment` and `Notification` were **not** globally migrated — explicitly out of scope.

## Alternatives rejected

* **A one-time backfill into `BusinessEvents`.** Rejected: it would write invented `EventUid`s, actors and
  payloads for facts nobody recorded, is not idempotent without extra bookkeeping, and a partial run is worse
  than no run. It also permanently mixes derived rows into the audit log with no way to tell them apart.
* **Leave old records blank.** Rejected: a visible regression, and it makes the platform's own claim untrue.
* **One generic adapter driven by configuration** (table, date column, label column). Rejected: the three
  sources have nothing structurally in common — one is a polymorphic activity log, one is a journal-entry
  count, one is four columns on the row itself.
* **Fabricate the missing work-order cancellation date** from `ClosedAt` or the last journal entry. Rejected:
  it would put a wrong date in a user-visible history and there is no way for the reader to know.

## Verification

* `A_pre_kernel_customer_shows_its_creation_and_its_CRM_activities`
* `A_pre_kernel_purchase_invoice_shows_its_recording_and_re_posts`
* `A_pre_kernel_work_order_shows_its_own_lifecycle_stamps` (and `ClosedAt == CompletedAt` is not a second row)
* `A_cancelled_pre_kernel_work_order_is_not_given_an_invented_date`
* `A_customer_row_with_no_CreatedAt_is_not_dated_to_now`
* `Events_and_legacy_entries_merge_newest_first`
* `Legacy_entries_are_suppressed_once_real_events_cover_them`
* `Legacy_reconstruction_is_stable_across_reads_for_every_pilot`
* `An_entity_with_no_legacy_data_returns_an_empty_result_safely`
* slice 1: `A_pre_kernel_invoice_still_shows_a_timeline`, `Legacy_items_are_deduplicated_once_a_real_event_covers_them`
