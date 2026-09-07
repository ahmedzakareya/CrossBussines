# Slice 002 — Platform Kernel Expansion

> **Goal:** prove the kernel works across multiple ERP domains and is not coupled to `SalesInvoice`.
> **Pilots:** `Customer` (Accounting) · `PurchaseInvoice` (Accounting) · `ManufWorkOrder` (Manufacturing).
> **Also:** a second real consumer (`NotificationProjection`) and a real SQL Server concurrency suite.
> **Status:** implemented. Both projects build. 91/91 tests pass (82 in-process + 9 SQL Server integration).

---

## 1. Registry definitions added or changed

| Code | Change | Module | Scope | Route | Timeline | Comments | Picker |
|---|---|---|---|---|---|---|---|
| `Customer` | **upgraded** (existed in slice 1) | Accounting | `Accounting` | `/Accounting/CustomerStatement?id={id}` | ✅ **new** | — | ✅ |
| `PurchaseInvoice` | **added** | Accounting | `Accounting` | `/Accounting/PurchaseInvoiceDetail?id={id}` | ✅ | ✅ | ❌ |
| `ManufWorkOrder` | **upgraded** (existed in slice 1) | Manufacturing | `Manufacturing` **new** | `/Inventory/WorkOrderDetails?id={id}` | ✅ **new** | — | ✅ |

No definition was duplicated; the two existing codes were reused and their capabilities raised. No alias became
a canonical code — stored data already uses the canonical PascalCase codes (`DocComment.EntityType` rows written
before the kernel say `"SalesInvoice"` / `"PurchaseInvoice"`, which already match), so **no compatibility
mapping was needed**.

`PurchaseInvoice` is kept **out of the TM-2 record picker** (`ListedInRecordPicker = false`) because it was
never offered there before the kernel. The picker's seven types and their order are unchanged.

### Deviation from the brief: Customer → Accounting, not CRM

The brief asked for `Customer → CRM permission adapter`. **Implemented as `Accounting` instead**, deliberately:
this `Customer` is the accounting customer master (`Models/Context/Accounting/ReceivablesPayables.cs`), its
screens are `/Accounting/Customers` and `/Accounting/CustomerStatement`, and `SaveCustomer` is gated by
`[AccPerm("post")]`. `CrmAccessService` governs a *different* set of tables (`CrmAccounts` / `CrmContacts` /
leads, bridged by `CrmCustomerLink`). Authorizing this entity by CRM roles would deny the timeline to accounting
users on a page they can already open — a real defect, not a preference. The brief's own rule for
`PurchaseInvoice` ("according to the real page access") is what was applied.

---

## 2. Real transitions found, and the events implemented

Determined by reading the services, not by assumption.

### Customer — `ReceivableService`

| Operation | Transition | Event |
|---|---|---|
| `CreateCustomerAsync` (quick-add) | row created, `IsActive = true` | `Customer.Created` |
| `SaveCustomerAsync` | **UPSERT** — creates when no row is found, else updates | `Customer.Created` *or* `Customer.Updated` |

**`Customer.StatusChanged` REJECTED.** There is no activate/deactivate operation anywhere — `IsActive` is a
checkbox on the same edit form. An `IsActive` flip is reported inside `Customer.Updated` via `changedFields`
plus `oldStatus`/`newStatus`, which is what actually happened.

**Neither write path had a transaction.** A single `SaveChanges` was atomic on its own, so none was needed.
Both now open `ScopedTx.BeginOrJoinAsync`, which joins an ambient transaction when there is one and owns a new
one otherwise — so a caller with no transaction behaves exactly as before.

### Purchase Invoice — `PayableService`

| Operation | Transition | Event |
|---|---|---|
| `CreatePurchaseInvoiceAsync` | row created already `Status = "Posted"` | `PurchaseInvoice.Created` |
| `EditPurchaseInvoiceAsync` | reverse + re-post in place | `PurchaseInvoice.Updated` |

**`Posted` REJECTED** — creation *is* the posting; there is no draft-then-post step.
**`Approved` REJECTED** — no approval transition exists anywhere in `PayableService`.
**`Cancelled` declared, no producer** — `PurchaseInvoice.Status` documents `'Cancelled'` as legal, but nothing
sets it. The name is frozen for whichever slice adds the action.

### Manufacturing Work Order — `ManufService`

The only pilot with a genuine multi-state lifecycle (`Draft | Released | Completed | Cancelled`).

| Operation | Transition | Event |
|---|---|---|
| `CreateAsync` | → `Draft` | `ManufWorkOrder.Created` |
| `SaveHeaderAsync` | header edit, `Draft` only | `ManufWorkOrder.Updated` |
| `SetStatusAsync("Released")` · `ReleaseAsync` | `Draft` → `Released` | `ManufWorkOrder.Released` |
| `ProducePartialAsync` (not finalising) | `ProducedQty` advances | `ManufWorkOrder.Produced` |
| `CompleteAsync` · `ProducePartialAsync` finalising | → `Completed` | `ManufWorkOrder.Completed` |
| `SetStatusAsync("Cancelled")` · `CancelAsync` | → `Cancelled` | `ManufWorkOrder.Cancelled` |

**`Started` REJECTED** — there is no `Started` status and no operation that would produce one.

`ManufWorkOrder.Produced` was added because partial production is a **real operation** with a real effect
(`ProducedQty` changes, stock and GL move) even though it is not a status transition. Whether a
`ProducePartialAsync` call produced `Produced` or `Completed` is decided by the **status after the call**, not
by the caller's `finalize` flag — `finalize` only completes the order when the whole quantity is done.

### Producer placement for the staged lifecycle

`Release` / `Cancel` / `Complete` / `ProducePartial` change state inside **`StockService`**, the sole stock + GL
writer, which owns its own `ScopedTx`. Rather than edit that writer, each `ManufService` wrapper opens an
**outer** `ScopedTx`; `BeginOrJoinAsync` makes `StockService`'s inner transaction join it, so the business fact
and its event commit together and `StockService` is untouched. This is precisely what ScopedTx's own-or-join
design exists for.

It also closes a real gap: `CancelWorkOrderAsync` has an early-return path (nothing issued → no GL) that
commits with **no transaction of its own**. The outer transaction now covers that path too.

---

## 3. Payload schemas (all version 1)

`CustomerEventPayload` — `customerCode` (`TaxRegNo`; there is no separate code column) · `customerName` ·
`customerType` (`Segment`) · `initialStatus` · `oldStatus` · `newStatus` · `changedFields`

`PurchaseInvoiceEventPayload` — `invoiceNumber` · `supplierId` · `supplierName` · `invoiceDate` ·
`totalBefore` · `totalAfter` · `oldStatus` · `newStatus` · `changedFields`

`ManufWorkOrderEventPayload` — `workOrderNumber` · `itemId` · `itemName` · `plannedQuantity` ·
`completedQuantityBefore` · `completedQuantityAfter` · `oldStatus` · `newStatus` · `plannedStartDate` ·
`plannedEndDate` · `changedFields`

**Excluded by contract** and asserted by tests: invoice/PO lines, BOM and routing graphs, GL entries, full
supplier or customer records, employee/labour data, binary or file content, credentials. `changedFields` carries
field **names** only — which is what lets an update report that `Phone` or `Email` changed without carrying the
value.

Worst-case measured payload is well under 10% of the 64 KB cap.

**Visibility: `Internal` for every slice-2 event.** Everything in these payloads is already visible to anyone
who may open the record. No cost, margin or credit figure is carried, so no `Confidential` tier is warranted
yet — the first event that carries one will need it.

**DedupKeys** are set only where a retry or double-submit is realistic and the transition can happen once:
`Customer.Created:{id}`, `PurchaseInvoice.Created:{id}`, `ManufWorkOrder.Created|Released|Cancelled|Completed:{id}`.
Updates and partial productions carry none — they legitimately repeat, and each is its own fact.

---

## 4. Notification mapping

See **ADR-006** for the full table and reasoning. Summary:

* Mapped: `SalesInvoice.Created`, `PurchaseInvoice.Created` (both **replacing removed legacy producers**),
  `ManufWorkOrder.Released`, `ManufWorkOrder.Completed` (both new — manufacturing had never had notifications).
* Not mapped, on purpose: all `Customer` events, both `Updated` events, `ManufWorkOrder.Created` / `.Updated` /
  `.Produced` / `.Cancelled`.
* **Duplicate prevention:** the two legacy `NotifyRoleAsync` calls were **deleted**, each replaced by a comment
  at the site. Two legacy notifications remain in `ReceivableService` and are documented as correctly retained
  (stale-rate warning; credit-limit block on a *rejected* invoice, where no business fact exists).
* Two new catalog types: `work_order_released`, `work_order_completed`, with `Manufacturing` category metadata.

---

## 5. Database changes

`deploy/sql/platform_business_events_slice_002.sql` — additive, idempotent, no data modification:

1. `Notifications.EntityType NVARCHAR(60) NULL` + `Notifications.EntityId INT NULL`
2. `IX_Notifications_Recipient_DedupKey` (filtered, non-unique) — the idempotency lookup
3. `IX_Notifications_Entity` (filtered) — find notifications by the record they point at

**`BusinessEvents` and `BusinessEventDispatch` need no change.** Slice 1's schema already carries everything a
second consumer needs — that is the point of ADR-003. Adding `NotificationProjection` was a code change only.

No backfill is included. Two are deliberately omitted and documented in the script: mapping legacy
`(Type, RefId)` pairs onto entity codes (a guess for several types), and creating `NotificationProjection`
dispatch rows for historical events (would deliver a burst of notifications about finished work).

---

## 6. Deployment order

```
1. deploy/sql/platform_business_events.sql              (slice 1 — if not already applied)
2. deploy/sql/platform_business_events_slice_002.sql     (this slice)
3. Deploy the application
```

**SQL before code.** `RecordAsync` is inside the business transaction with no swallowing catch, so a missing
column or table fails the operation that produced the event. Both scripts print `EXISTS` on a second run.

---

## 7. Rollback procedure

Code-only rollback, in order (safe, no data loss — the new columns are nullable and read only by kernel rows):

1. Remove the three `_DocEventTimeline` includes (`CustomerStatement`, `PurchaseInvoiceDetail`,
   `WorkOrderDetails`) → timelines disappear from the UI.
2. Remove `NotificationProjection` from `BusinessEventConsumers.Registered` **and** its DI registration →
   no new dispatch rows for it; existing ones stop being claimed.
   **Then restore the two legacy `NotifyRoleAsync` calls** in `ReceivableService.CreateSalesInvoiceAsync` and
   `PayableService.CreatePurchaseInvoiceAsync`, or those two notifications stop entirely.
3. Revert the producers: `ReceivableService` (Customer), `PayableService` (PurchaseInvoice), `ManufService`
   (work order). Each `RecordAsync` block is self-contained; the `ScopedTx` wrappers can stay harmlessly.
4. Set `SupportsTimeline = false` for the three pilots in `EntityRegistry` if the registry is kept.

Schema rollback is optional and unnecessary — the columns and indexes are additive. If required:

```sql
DROP INDEX IX_Notifications_Entity ON dbo.Notifications;
DROP INDEX IX_Notifications_Recipient_DedupKey ON dbo.Notifications;
ALTER TABLE dbo.Notifications DROP COLUMN EntityId, EntityType;
```

---

## 8. Known limitations

1. **Per-recipient notification permission is not truly per-recipient** — the access services read the employee
   from the HTTP session and the dispatcher has none. The audience is the module's own role data, so it is
   authorized by construction, and the seam is in place for when an adapter reads from `BusinessContext`. See
   ADR-006.
2. **Manufacturing has no RBAC of its own.** `ManufacturingPermissionAdapter` delegates to
   `IInventoryAccessService`; the work-order controller actions carry no `InvPerm` attribute at all, so the
   effective page gate today is "authenticated staff". `View` maps to inventory `read` (the module's own
   decision that any authenticated user may look); the elevated tiers map to `doc`/`manage`, which are real
   gates. A real `ManufacturingUserRole` table would change only that adapter.
3. **A cancelled pre-kernel work order shows no cancellation** — the schema has no cancellation timestamp, and
   a fact with no honest date is not emitted (ADR-005).
4. **Pre-kernel partial productions are unrecoverable** — only the running `ProducedQty` total survives.
5. **A customer with `CreatedAt = NULL`** contributes no legacy creation row.
6. **`PurchaseInvoice.Cancelled` has no producer** (declared only); **`Approved`/`Posted` are not declared at
   all**; **`Customer.StatusChanged` is not declared**; **`ManufWorkOrder.Started` is not declared**.
7. **`Customer.SupportsComments` stays false** — the comments widget was not added to `CustomerStatement`, so no
   screen outside the timeline scope changed.
8. **No `Confidential` slice-2 event exists yet**, so the elevated visibility tiers remain exercised only by
   tests, not by real data.
9. **Employee search is still not company-scoped** (inherited from slice 1, deliberately preserved).
10. **SQL Server integration tests need `CROSSBUY_TEST_SQL`**; without it they skip. They create and drop their
    own scratch database and refuse to target `CrossBuyDB`/`CrossBuyDB2`/`CrossBuy`.

---

## 9. Related documents

* **PKS-001** — the kernel specification (updated for this slice)
* **ADR-001** Transactional Business Events · **ADR-002** Entity Registry · **ADR-003** Dispatch State Per
  Consumer · **ADR-004** Business Event Visibility
* **ADR-005** Legacy Timeline Adapters · **ADR-006** Notification Projection · **ADR-007** SQL Server Dispatch
  Locking
