# PKS-001 — Platform Kernel Specification

> **Status:** slice 1 implemented (Entity Registry · Business Context · Business Events + Transactional Outbox · Timeline Projection · Authorization Contract · Sales Invoice pilot).
> **Slice 2 implemented** (Customer · PurchaseInvoice · ManufWorkOrder pilots · `NotificationProjection` consumer · SQL Server concurrency suite) — see `Slice-002-Kernel-Expansion.md`, ADR-005, ADR-006, ADR-007.
> **Scope:** this document is the contract. The ADRs beside it record *why* each contested decision went the way it did.

---

## 1. What this slice is

Platform services layered **on top of** the existing ERP. No POCO business entity was modified, no inheritance
was introduced, and no module behaviour changed outside the Sales Invoice pilot. Capabilities attach to any
business object through the pattern already proven by `DocComment`: **`EntityType` + `EntityId`**.

Migrations are disabled in this project, so every structural change ships as an idempotent script under
`deploy/sql/`.

---

## 2. IEntityRegistry contract

The single source of truth for business object types. Promoted from `TaskLinkResolver` (TM-2), which already
owned the bilingual label, icon, deep link and record search — see **ADR-002**.

```csharp
IReadOnlyList<EntityDefinition> GetDefinitions();
EntityDefinition GetDefinition(string entityCode);              // throws EntityCodeNotRegisteredException
bool TryGetDefinition(string? entityCode, out EntityDefinition? definition);
bool IsValid(string? entityCode);
Task<List<EntitySearchResult>> SearchAsync(string entityCode, string? query, BusinessContext context, CancellationToken);
Task<EntityResolveResult> ResolveAsync(string entityCode, int entityId, BusinessContext context, CancellationToken);
string? BuildUrl(string entityCode, int entityId);
```

`EntityDefinition`: `Code · DisplayNameAr · DisplayNameEn · Module · Icon · Color · RouteTemplate ·
SupportsSearch · SupportsTimeline · SupportsComments · SupportsFiles · SupportsFollowers · PermissionScope ·
ListedInRecordPicker`.

### Frozen entity codes

| Code | Module | PermissionScope | Route | Timeline | Picker |
|------|--------|-----------------|-------|----------|--------|
| `SalesInvoice` | Accounting | Accounting | `/Accounting/SalesInvoiceDetail?id={id}` | ✅ | ✅ |
| `PurchaseInvoice` | Accounting | Accounting | `/Accounting/PurchaseInvoiceDetail?id={id}` | ✅ *(slice 2)* | ❌ |
| `Customer` | Accounting | Accounting | `/Accounting/CustomerStatement?id={id}` | ✅ *(slice 2)* | ✅ |
| `Supplier` | Accounting | Accounting | none | — | ❌ |
| `ManufWorkOrder` | Manufacturing | **Manufacturing** | `/Inventory/WorkOrderDetails?id={id}` | ✅ *(slice 2)* | ✅ |
| `PosOrder` | Pos | Pos | none | — | ✅ |
| `Employee` | Hr | None | `/Admin/EmployeesList` (list-only) | — | ✅ |
| `Project` | Projects | None | `/Project/Projects` (list-only) | — | ✅ |
| `Item` | Inventory | Inventory | `/Inventory/EditItem?id={id}` | — | ✅ |

`Customer` stays on the **Accounting** scope, not CRM: it is the accounting customer master, its screens live
under `/Accounting`, and `CrmAccessService` governs different tables. See `Slice-002` §1.

**Rules**

* Codes are compared **ordinally**. `salesinvoice` is not `SalesInvoice`; it is invalid.
* New platform code must never accept a free-text entity type. Before the kernel there were three
  disagreeing vocabularies — `TaskLinkResolver` keys (PascalCase), `DocComment.EntityType` (unconstrained),
  and `NotificationTypes` strings (snake_case). The registry freezes the PascalCase set.
* `ListedInRecordPicker` exists purely for TM-2 compatibility: `Supplier` was searchable but never offered by
  the picker, and therefore never resolvable through it. The flag preserves that exactly.
* Capability flags describe what is **wired**, not what is conceivable. They flip on per entity as each one is
  onboarded, so a caller that gates on `SupportsTimeline` is never lied to.
* A `RouteTemplate` without `{id}` is a list-only screen and `BuildUrl` returns it unchanged. `null` means the
  type has no screen at all.

`TaskLinkResolver` remains as a thin compatibility wrapper delegating to the registry. Its DTOs and signatures
are unchanged; `TasksController` and `DevSeedController` were not touched.

---

## 3. BusinessContext contract

```csharp
int CompanyId · int? BranchId · int? EmployeeId · string UserId
IReadOnlyCollection<string> Roles · Guid? CorrelationId · int? TenantId · bool IsSystem
```

`IBusinessContextAccessor.GetCurrentAsync()` builds it once per DI scope. It introduces **no new source** of
user or company identity — it reads the two the project already uses, in the order the project already prefers:

1. the `"Employee"` session blob (`EmployeeViewModel`) — the source `AccountingAccessService` uses;
2. `ClaimTypes.NameIdentifier` → `IEmployeeService` → `Employee.EmpCompanyID` — the source
   `CommentsController.MeAsync()` and `ApprovalsController` use.

When neither yields a company it falls back to company `1`, which is the literal
`private const int DefaultCompanyId = 1` that eight controllers already hard-code. **That fallback is the
current tenancy reality, not a new decision.** `CorrelationId` is one Guid per scope, so every event produced
by one request shares it.

### Future TenantId strategy

`TenantId` is present on the contract and always `null`. There is no `Tenant` table and nothing reads it. It
exists so that service **signatures do not change** when SaaS lands: the only component that will need editing
is `BusinessContextAccessor`, because it is the single place that decides the isolation key. Deferring the
*feature* while fixing the *contract* is what keeps the six platform tables built in later slices from needing
a retrofit.

---

## 4. BusinessEvent contract

### 4.1 Table

`BusinessEvents` — append-only, immutable once written. Columns: `EventId · EventUid · CompanyID · BranchID ·
EntityType · EntityId · EventType · ActorEmployeeId · Payload · PayloadVersion · CorrelationId · DedupKey ·
Visibility · CreatedAt · CompletedAt`.

`CompletedAt` is set when every registered consumer reaches `Done`. It is a **reporting roll-up only** — see
§6.

### 4.2 Recording

```csharp
Task<long> IBusinessEventService.RecordAsync(BusinessEventRecord record, CancellationToken);
```

Rules, all enforced in code:

1. The event is inserted **inside the caller's ambient `ScopedTx`**. The service never opens or commits a
   transaction; it enrols in the caller's so the fact and its event share one fate.
2. It is **never** written after `CommitAsync`.
3. There is **no** swallowing `try/catch`. If the event cannot be written, the business transaction fails.
   This is the deliberate opposite of the notification convention (`after-commit + try/catch { }`), and both
   conventions now sit a few lines apart in `ReceivableService` so the difference is visible at the call site.
4. Calling `RecordAsync` with **no ambient transaction throws**. An event that could outlive a rolled-back
   business operation is precisely the failure the kernel exists to prevent, so it is refused rather than
   trusted to review.
5. `EntityType` is validated against `IEntityRegistry`; `EventType` is validated against that entity code.
6. `CompanyID`, `BranchID`, `ActorEmployeeId` and `CorrelationId` come from `BusinessContext`. The explicit
   overrides on `BusinessEventRecord` are honoured **only** when `BusinessContext.IsSystem` is true.
7. `DedupKey` makes recording idempotent per company: a second call with the same key writes nothing and
   returns the original `EventId`. A lost race on `UX_BusinessEvents_DedupKey` is caught and resolved to the
   winner's id.
8. Dispatch rows are created in the **same transaction**, one per registered consumer.
9. `RecordAsync` sends no notification, runs no AI, indexes nothing and executes no workflow. It records
   durable facts. Nothing else.

### 4.3 Event naming rules

Canonical form: **`<EntityCode>.<Action>`**, action in PascalCase, total length ≤ 80.

Accepted: `SalesInvoice.Created`, `SalesInvoice.Updated`, `SalesInvoice.Cancelled`.

Slice 2 added, all backed by a real transition (`Slice-002` §2):
`Customer.Created` · `Customer.Updated` · `PurchaseInvoice.Created` · `PurchaseInvoice.Updated` ·
`PurchaseInvoice.Cancelled` *(declared, no producer)* · `ManufWorkOrder.Created` · `.Updated` · `.Released` ·
`.Produced` · `.Completed` · `.Cancelled`.

Names **rejected because the transition does not exist in the code**: `PurchaseInvoice.Posted` (creation *is*
posting), `PurchaseInvoice.Approved` (no approval anywhere), `Customer.StatusChanged` (no activate/deactivate
operation — an `IsActive` flip is reported inside `Customer.Updated`), `ManufWorkOrder.Started` (no such status).

Rejected *forms*, with the reason each one is rejected:

| Rejected | Why |
|----------|-----|
| `InvoiceCreated` | no entity code, no separator |
| `sales_invoice_created` | snake_case (the `NotificationTypes` style) |
| `SalesInvoiceCreated` | entity code and action fused, unparseable |
| `SalesInvoice.created` | action not PascalCase |
| `SalesInvoice.Created.Again` | more than one separator |
| `SalesInvoice.Created` recorded against `Customer` | prefix does not match the entity |

### 4.4 Payload versioning and limits

* `PayloadVersion` is the **schema version of the payload, and nothing else**. It is not an entity concurrency
  token.
* **Maximum payload: 64 KB of UTF-8.** Oversized payloads throw `BusinessEventPayloadTooLargeException`
  carrying both the actual and the maximum size. The event log is destined to be the largest table in the
  database, so a payload is a *summary*, never a document.
* Never place in a payload: files, binary data, secrets, passwords, access tokens, connection strings, or
  unrestricted employee data.
* A payload supplied as a `string` must already be valid JSON. It is parsed at record time so the failure
  surfaces there rather than at `COMMIT` against the `ISJSON` check constraint — i.e. inside someone else's
  financial transaction.

### 4.5 Envelope

The wire contract handed to consumers:

```
eventUid · eventType · entity.code · entity.id · actor.employeeId
context.companyId · context.branchId · context.correlationId
payloadVersion · visibility · occurredAt · payload
```

---

## 5. Visibility vocabulary

**Frozen** (ADR-004), mirrored by `CK_BusinessEvents_Visibility`:

| Value | Who may read it |
|-------|-----------------|
| `Internal` | anyone who may `View` the entity. The default for ordinary document facts. |
| `Confidential` | requires elevated module rights (`ViewConfidential`) — cost, margin, credit decisions. |
| `Restricted` | a module manager (`ViewRestricted`), **or the actor of that specific event**. |
| `System` | machine bookkeeping. Same gate as `Restricted`; never surfaced below it. |

The own-actor exception on `Restricted` is enforced in two halves: the SQL filter admits `Restricted` rows so
the caller's own can be found, then a row-level pass drops every row whose actor is someone else. Those two
halves must stay distinct — collapsing them into one flag grants every viewer every restricted event, which is
a real bug this slice's tests caught before it shipped.

---

## 6. Per-consumer dispatch state

`BusinessEventDispatch` — one row per **(event, consumer)**. Columns: `ID · EventId · Consumer · Status ·
Attempts · Error · UpdatedAt`.

Status lifecycle:

```
Pending --claim--> Claimed --success--> Done
                          \--failure--> Failed --retry--> Claimed
```

Why a table and not a flag on the event: a single `DispatchedAt` cannot express N consumers. If search
indexing fails while notifications succeeded, one timestamp offers only "redeliver to everyone" (duplicates) or
"call it done" (silent loss). See **ADR-003**.

Registered consumers: **`TimelineProjection`** (slice 1) and **`NotificationProjection`** (slice 2, ADR-006).
A consumer is added to `BusinessEventConsumers.Registered` *when it is implemented*, never before — a registered
consumer with no implementation accumulates rows nothing drains, and the dispatch worker logs an error if that
ever happens. Search / AI / Dashboards / Integrations are still absent for that reason.

Slice 2 is where this design earned its keep: adding the second consumer required **no schema change at all**,
and `TimelineProjection` staying `Done` while `NotificationProjection` fails and retries alone is now verified
both in-process and against a real SQL Server.

### The pending-based dispatch rule

**A dispatcher must never use `EventId > lastSeen`, `MAX(EventId)`, or any global cursor.**

Identity values are assigned at `INSERT` but become visible at `COMMIT`. Transaction A can take `EventId` 100
and still be open while transaction B takes 101 and commits first. A cursor that advances to 101 will never
come back for 100 once A commits. `ScopedTx` is built for nesting, so long overlapping transactions are the
normal case here, not an edge case.

Work is therefore selected by **status only**. `IEventDispatchStore` deliberately exposes no cursor:

```csharp
GetPendingAsync(consumer, batchSize, ct)     // non-mutating peek — diagnostics and tests
ClaimPendingAsync(consumer, batchSize, ct)   // atomic claim — what the worker uses
MarkDoneAsync(dispatchId, ct)
MarkFailedAsync(dispatchId, error, ct)       // error truncated to 400 chars
IncrementAttemptsAsync(dispatchId, ct)
TryCompleteEventAsync(eventId, ct)           // stamps CompletedAt only when all consumers are Done
```

Eligibility: `Pending`, or `Failed` past `RetryBackoffSeconds`, or `Claimed` past `StaleClaimMinutes` (a worker
that died holding the row), and always `Attempts < MaxAttempts`. A row that exhausts its attempts stays
`Failed` **with its reason**, for an operator — never deleted, never silently dropped. All timestamps are UTC.

Claiming on SQL Server is one atomic statement:

```sql
UPDATE TOP (@take) d SET Status='Claimed', Attempts=Attempts+1, UpdatedAt=SYSUTCDATETIME()
OUTPUT inserted.ID, inserted.EventId, inserted.Consumer, inserted.Attempts
FROM BusinessEventDispatch AS d WITH (ROWLOCK, READPAST, UPDLOCK)
WHERE <eligible>
```

`UPDLOCK` takes the update lock as the row is read, `READPAST` skips rows another worker holds instead of
blocking, and `OUTPUT` returns exactly the rows this caller won.

### Future queue-based dispatch

Persistence sits behind `IEventDispatchStore`, so the SQL table can be replaced by Service Bus / Rabbit
without touching a consumer. `DispatchWorkItem` carries no payload for the same reason — the consumer loads
the event it needs, keeping future messages small.

---

## 7. Authorization pipeline

```csharp
Task<PermissionDecision> IPlatformPermissionProvider.CanAsync(
    BusinessContext context, string entityType, int entityId, string action, CancellationToken);
```

The provider owns **no policy**. Policy already lives in the module access services, and duplicating any of it
would create a second source that drifts. The provider resolves `EntityDefinition.PermissionScope` and routes
to an adapter, which translates the canonical action onto that module's own vocabulary:

| Scope | Access service | `View` | `ViewConfidential` | `ViewRestricted` |
|-------|----------------|--------|--------------------|------------------|
| `Accounting` | `IAccountingAccessService` | `read` | `post` | `manage` |
| `Inventory` | `IInventoryAccessService` | `read` | `doc` | `manage` |
| `Manufacturing` *(slice 2)* | `IInventoryAccessService` — manufacturing has no RBAC of its own | `read` | `doc` | `manage` |
| `Crm` | `ICrmAccessService` | `read` | `edit` | `manage` |
| `Pos` | `IPosAccessService` | any POS role | `CanSell` | `IsManager` |
| `None` | *(none exists)* | authenticated | **denied** | **denied** |

`Manufacturing` is a **separate scope that delegates** rather than a reuse of `Inventory`: the work-order screens
are governed by inventory roles today, but the seam lets manufacturing policy diverge later without touching the
inventory entities. Its elevated tiers (`doc`/`manage`) are real gates.

**Notification recipients are authorized through this same provider** before a notification is created, so no
user is ever linked to a record they cannot open — with the session-scoped caveat in ADR-006.

None of those four services was modified. POS is wrapped by an adapter because its checks are synchronous and
take the role list as an argument rather than reading the session.

Scope `None` (HR, Projects) is deliberately the weakest adapter and says so in code: it grants `View` to an
authenticated user in the company and refuses every elevated action, because there is no module policy to
delegate to. Inventing thresholds there would be exactly the second authorization source this abstraction
exists to prevent.

Timeline is the first caller. AI Context, Search, Files, Relations and Followers will call this same method
unchanged — which is what makes "AI never bypasses application authorization" enforceable rather than
aspirational.

---

## 8. Timeline projection

```csharp
Task<IReadOnlyList<TimelineItemViewModel>> ITimelineProjectionService.GetAsync(
    string entityCode, int entityId, BusinessContext context, int take = 100, CancellationToken);
```

**The UI never queries `BusinessEvents`.** Every read passes four filters that are therefore impossible to
forget:

1. **company isolation** — `CompanyID` must equal the caller's company;
2. **branch isolation** — applied only when *both* sides carry a branch. A caller with no branch (head office)
   must not lose branch-stamped history, and an event with no branch must not vanish for a branch user;
3. **user permission** — `View` via `IPlatformPermissionProvider`. Denial throws
   `PlatformAccessDeniedException`, which the controller maps to **403** — "you may not view this record" is
   not the same answer as "this record has no history";
4. **event visibility** — per row, per §5.

The contract returns a projection, not events, so adding a persisted projection table later changes nothing
above this interface. The current implementation projects at read time.

Rendering is bilingual and split on purpose:

* `TimelineEventPresenter.TryPresent` — **strict**. Used by the `TimelineProjection` consumer at write time,
  so an unrenderable event fails its dispatch row and reaches an operator.
* `TimelineEventPresenter.Present` — **lenient**. Used by the read path, so an unrecognised event still renders
  its canonical action name rather than breaking a document screen.

### What the TimelineProjection consumer does

Because the projection is read-time, the consumer writes nothing. It does the work read-time projection
*cannot* do for itself: it validates, once, at write time, that each event is renderable — catching an entity
whose `SupportsTimeline` was turned off after events existed, a payload from a newer build
(`PayloadVersion` ahead of this code), or a payload that does not match the version it declares. Read-time
projection meets those problems in a user's browser, where the only options are a blank row or a broken
screen. When the persisted projection table arrives, this class is where it gets written.

---

## 9. Legacy timeline compatibility

**Strategy chosen: merge at read time. No backfill.** Generalised to four entities in slice 2 — the per-entity
sources, the "a fact with no honest timestamp is not emitted" rule, and what cannot be reconstructed are all in
**ADR-005**.

`BusinessEvents` starts empty, so every pre-existing invoice would show a blank timeline the moment the
projection went live — a visible regression on historical data.
`SalesInvoiceLegacyTimelineAdapter` reconstructs those items at read time from data the system already stores
durably:

* the issue itself, from `SalesInvoice.CreatedAt ?? InvoiceDate` plus `InvoiceNo` and `GrandTotal`;
* one *updated* item per GL re-post, from `JournalEntries` where `SourceType='SalesInvoice'`. Index 0 is the
  original posting (already covered by the issue item); each subsequent one is an edit. Reversal entries carry
  `SourceType='Reversal'` and therefore cannot double-count.

Why merge rather than backfill: nothing is written, so there is no migration to reverse and no risk of a
half-finished backfill double-counting. The cost is that legacy items are coarser than real events — which is
honest, because the finer detail was never recorded. No actor is invented for reconstructed history, and each
item is marked `Source = Legacy` and badged "historical" in the UI.

**Deduplication rule:** the kernel is authoritative from the moment it recorded its first event for a record.
A legacy item is dropped if its event type already appears among the real events, or if it is dated at/after
the first real event. Anything strictly older is history the kernel never saw, so it is kept.

Identity is deterministic (`MD5` of `legacy:{code}:{id}:{eventType}:{discriminator}`) so a page refresh does
not look like new activity.

`CRM Activity`, `DocComment` and `Notification` were **not** migrated — explicitly out of scope. All legacy
logic lives in that one adapter class so it can be deleted whole once history no longer matters.

---

## 10. Retention and archive direction

Not implemented in this slice; recorded here so it is not forgotten.

`BusinessEvents` will become the largest table in the database. The 64 KB payload cap is the first control. The
intended direction:

* the `IX_BusinessEventDispatch_Pending` index is filtered on `Status <> 'Done'`, so the **work queue** stays
  small forever regardless of log size — completed work leaves the index entirely;
* archival should move rows older than a retention horizon to a cold table, oldest-first, and only where
  `CompletedAt IS NOT NULL`. The FK from `BusinessEventDispatch` guarantees no queue entry is left pointing at
  an archived event;
* partitioning on `CreatedAt` is the natural next step if archival alone proves insufficient.

---

## 11. Known limitations

1. **Employee search/resolve is not company-scoped.** The TM-2 employee picker never filtered by company;
   adding the filter would silently hide employees from an existing multi-company picker, which is a behaviour
   change outside this slice's pilot. Deliberately preserved and commented at both call sites in
   `EntityRegistry`.
2. **`SalesInvoice.Approved` does not exist.** `CreateSalesInvoiceAsync` writes the invoice already
   `Status="Posted"`; there is no separate approval step anywhere in the codebase. Naming an `Approved` event
   would put a fact in the audit log that never happened.
3. **`SalesInvoice.Cancelled` is declared but has no producer.** `SalesInvoice.Status` documents `'Cancelled'`
   as a legal value, yet no code path sets it. The name is frozen for whichever slice adds the cancel action.
4. **Row-level (ownership) authorization is not applied.** Module RBAC in this codebase is action-level.
   `CrmAccessService.VisibleOwnerIdsAsync` exists but is not wired in, because the pilot entity has no owner
   concept. It belongs to the slice that onboards a CRM entity.
5. **Scope `None`** (HR, Projects) has no real access service — see §7.
6. **The two-worker claim test proves single-ownership, not skip-locked concurrency.** SQL Server's
   `READPAST` behaviour under genuine parallelism needs a real SQL Server instance; the in-process suite uses
   SQLite. `SqlEventDispatchStore` keeps a portable claim path for that provider and it is not a production
   path.
7. **Referential integrity is off in the test host.** The kernel tables sit at the end of long FK chains in the
   real schema; the FK that matters here is created and enforced by the deployment script.
8. **Comments coverage is unchanged.** `_DocTimeline` (the comments widget) is shared by three screens, so the
   event timeline was added as a separate partial — wired into the sales invoice in slice 1 and into the
   customer statement, purchase invoice and work order in slice 2. `_DocTimeline` itself was never modified.

Slice 2 adds its own limitations — manufacturing RBAC, per-recipient notification authorization, and what
pre-kernel history cannot be reconstructed. See `Slice-002-Kernel-Expansion.md` §8.

---

## 12. Related documents

* **ADR-001** — Transactional Business Events
* **ADR-002** — Entity Registry
* **ADR-003** — Dispatch State Per Consumer
* **ADR-004** — Business Event Visibility
* **ADR-005** — Legacy Timeline Adapters *(slice 2)*
* **ADR-006** — Notification Projection *(slice 2)*
* **ADR-007** — SQL Server Dispatch Locking *(slice 2)*
* **Slice-002-Kernel-Expansion.md** — the slice-2 record: entities, events, mappings, deployment, rollback
