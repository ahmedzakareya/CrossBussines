# Stage 1 Batch B / B1 — Authoritative entity-isolation classification

**Status: B1 COMPLETE. No filter has been enabled. No code has been changed by B1.**
B1 is analysis. B3 (controlled bypass) is next, and B2 (filters) cannot start before B3 is tested.

**Evidence:** `evidence/Entity-Isolation-Classification.csv` (machine-readable) · source inspection ·
**read-only** queries against CrossBuyDB2 (row counts and NULL-company counts; nothing was written).

---

## 1. The twelve pilot entities — CompanyID ownership confirmed

All twelve are **CompanyScopedDirect**: each owns a real `CompanyID` column on its own table. Verified from the
entity classes and from live data.

| Entity | DbSet | `CompanyID` | Rows | NULL company | Companies | Verdict |
|---|---|---|---|---|---|---|
`JournalEntry` | `JournalEntries` | `int` | 1938 | 0 | {1} | ready |
`SalesInvoice` | `SalesInvoices` | `int` | 307 | 0 | {1} | ready |
`PurchaseInvoice` | `PurchaseInvoices` | `int` | 79 | 0 | {1} | ready |
`Customer` | `Customers` | `int` | 39 | 0 | {1} | ready |
`Item` | `Items` | `int` | 199 | 0 | {1} | **ready only with an explicit public scope** |
`Warehouse` | `Warehouses` | `int` | 3 | 0 | {1} | ready |
`Quotation` | `Quotations` | `int` | **0** | 0 | — | ready (zero data risk) |
`Lead` | `Leads` | `int` | 12 | 0 | {1} | ready |
`Opportunity` | `Opportunities` | `int` | 7 | 0 | {1} | ready |
`CrmAccount` | `CrmAccounts` | `int` | 16 | 0 | {1} | ready |
`BusinessEvent` | `BusinessEvents` | `int` | 88 | 0 | {1} | **ready only behind a bypass** |
`Notification` | `Notifications` | **`int?`** | 1248 | **0** | {1} | **ready only behind a bypass + a NULL decision** |

### Two ownership facts that change the design

**`Notification.CompanyID` is nullable — the only one of the twelve.** A filter written as
`n.CompanyID == current` silently hides every row where the column is `NULL`. Live data has **0 NULLs across 1248
rows**, so the risk is latent rather than active here — but the slice-2 script performed **no backfill**, so a NULL
is a legitimate historical state and the filter must decide about it **explicitly** rather than by accident. B2 will
treat `NULL` as *not company-scoped* and exclude it from company-scoped reads only when a company scope is resolved,
and this is stated in the filter's own comment.

**No pilot entity has soft-delete.** `BaseEntity` carries `CreatedBy/At` and `UpdatedBy/At` only; none of the twelve
declares `DeletedAt`. **The `DeletedAt` half of gap B1 is therefore out of scope for this pilot** — there is nothing
to filter. (The parallel team's Comm/Calendar/Library modules do soft-delete, and none of those is in the pilot.)

---

## 2. Every excluded entity, and the exact reason

### 2.1 Excluded by instruction, and independently confirmed unsafe

| Entity | Reason |
|---|---|
**`StockBalance`** | `StockService.cs:452` reads it with `FromSqlInterpolated("SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = {companyId} …")`. **EF composes a global query filter *around* a `FromSql`**, so the hint applies to the inner query while the filter becomes an outer predicate — changing the plan for the row lock that prevents overselling, in the **sole stock writer**. Not filtered until a dedicated SQL Server test proves the locking and concurrency behaviour unchanged. |

### 2.2 SecuritySensitive — filtering these breaks authorization itself

| Entity | Reason |
|---|---|
**`Employee`** | `BusinessContextFactory` reads it to **resolve the company**. Filtering it is a circular dependency: resolve company → query `Employee` → apply company filter → resolve company. |
**`AccountingUserRole`, `InventoryUserRole`, `CrmUserRole`** | Read by the access services to decide permission, and by `NotificationProjectionConsumer` to resolve an audience **for the event's company**, which is not necessarily the reader's. |
**`BranchUserRole`** | Has **no `CompanyID` at all**. `PosAccessService.ResolveByUserIdAsync` reads it to establish POS access *before* any context exists. |

### 2.3 CrossCompanyOperational — legitimately read across companies

| Entity | Reason |
|---|---|
**`Companies`** | `ServiceController.CompaniesList` → `GetAllCompaniesAsync()` is genuine cross-company administration. `WorkerCompanyScope.EligibleCompanyIdsAsync` enumerates **every** company to drive the four schedulers. |
**`Branch`** | `PosController:46` lists companies through their branches; `BusinessContextFactory` validates a session branch against a company *before* the context exists. |
**`Hierarchical`** | No `CompanyID`. `CrmAccessService.TeamOwnerIdsAsync` loads the whole org tree to resolve a manager's team — it is the org-resolution substrate. |
**`AspNet*` Identity tables** | Sign-in happens before any company is known. |

### 2.4 CompanyScopedIndirect — no `CompanyID` of their own

Every `*Line` child (`SalesInvoiceLine`, `PurchaseInvoiceLine`, `JournalEntryLine`, `QuotationLine`, `StockCountLine`,
`StockTransferLine`, …), plus `CommAttachment`, `AnnouncementRead`, `ChatMessage`, `ChatReaction`,
`ConversationMember`, `CalendarEventAttendee`, `DepreciationLine`, `BankReconciliationLine`, `ItemBarcode`,
`PriceListLine`, `UoMConversion`, `ItemWarehouseSetting`, `BinLocation`, `LandedCostCharge`.

**Reason:** a filter would have to navigate to the parent header, which risks N+1 and changes `Include()` behaviour.
They are isolated *by* their parent, which is the correct model.

**One of them is a special case that must be stated separately:**

| Entity | Reason |
|---|---|
**`BusinessEventDispatch`** | **It is a QUEUE, and it must never be filtered.** `SqlEventDispatchStore.ClaimPendingAsync` claims work across **every** company in one atomic `UPDATE … OUTPUT` statement. A company filter would partition the queue, so one company's scope would stall every other company's fan-out. |

### 2.5 GlobalReference — genuinely global

`Currency`, `ExchangeRate`, `AccountType`, `ItemTypes`, `CountriesLookup`, `CompanyTypes`. Filtering them would empty
every currency, country and account-type picker in the application.

### 2.6 BranchScoped — a different question

22 entities carry `BranchID` (`BranchPosSettings`, `KitchenStations`, `PosOrders`, `DeliveryZones`, …). Branch
semantics differ per module, and branch scoping is a separate decision from company scoping. **Not in this pilot.**

### 2.7 LegacyUnclear — not eligible, per instruction

`FiscalPeriod` (**no `CompanyID` column at all** — risk R12: period open/close may be global across companies),
`LeaveRequest`, `LeaveApprovalStep`, `EmployeeRequestStep`, and the HR policy tables. Giving these isolation is a
**business** decision about whether the concept is per-company, not a filter.

---

## 3. Legitimate flows that will require the controlled bypass (B3 input)

These are the concrete, line-numbered reasons the approved order puts **B3 before B2**. Enabling a filter first would
break each of them.

| # | Flow | Evidence | What breaks without a bypass | Severity |
|---|---|---|---|---|
**1** | **The outbox dispatcher loads events across companies.** The queue spans every company in one pass; the worker then loads each event **by id with no company predicate**. | `BusinessEventDispatchWorker.cs:103` | The load returns `null` → `MarkFailedAsync("Event N no longer exists.")` → **healthy rows are marked Failed and `Attempts` is burned**. The outbox stops working and looks like data corruption. | **Critical** |
**2** | **Notification idempotency.** The consumer's dedup check reads `(RecipientEmployeeID, DedupKey)` with no company predicate — and it is the *only* real idempotency guard (the service's own check is unread-only). | `NotificationProjectionConsumer.cs:71` | The check returns false → **every stale-claim redelivery sends a duplicate notification**. | **Critical** |
**3** | `NotificationService` unread-dedup read, and its write resolves the company from the **recipient's** `Employee` row — which may differ from the scope doing the writing. | `NotificationService.cs:33,39` | Duplicate unread notifications; a recipient in another company cannot be de-duplicated. | **High** |
**4** | **Business Event Monitor elevated cross-company view** — a shipped product feature for platform admins. | `BusinessEventMonitorService.SearchAsync(..., crossCompany: true)` | The elevated view silently returns only the operator's own company, i.e. the feature lies. | **High** |
**5** | **The anonymous public storefront reads `Items`.** `[AllowAnonymous]`, `const int StoreCompanyId = 1`, and **no `BusinessContext` at all**. | `StoreController.cs:12`, `HomeController.cs:15`, `StoreCatalogService.cs:39` | With a fail-closed filter the **public catalogue returns zero products**. | **Critical** |
**6** | **Per-company worker iteration.** `WorkerCompanyRunner.ForEachCompanyAsync` loops companies and calls per-company work. | `BL/Platform/WorkerCompanyScope.cs:43` | Not a bypass — a **scope-setting requirement**: each iteration must publish *its* company to `ICompanyScopeHolder`, and a scope cannot be repointed (Batch A throws), so each iteration needs its own DI scope. | **High** |

**#5 is not an administration bypass and must not be built as one.** A public catalogue pinned to one company needs
an **explicit public company scope**, not a cross-company right. B3 will provide the audited admin bypass; the
storefront gets an explicit scope entry instead, which is strictly better than today's bare constant.

**`ServiceController.CompaniesList` needs no bypass for this pilot** — it reads `Companies`, which is excluded. That
is a useful confirmation that the exclusion list is drawn in the right place.

---

## 4. Raw SQL and `FromSql` risk for the pilot

**Zero raw SQL touches any of the twelve pilot entities.** Complete inventory of raw SQL in the application:

| Site | Target | Pilot? | Filtered by EF? |
|---|---|---|---|
`StockService.cs:452` | `StockBalances` | **no — excluded** | would be (this is exactly why it is excluded) |
`JournalEntryService.cs:313,322` | `NumberSequences` via `SqlQueryRaw<int>` | no | **no** — scalar `SqlQueryRaw` is never filtered |
`IntegrityCheckService.cs:309,+1` | reconciliation scalars | no | **no** |
`PosOrderService` | POS scalars + `ExecuteSqlRaw` | no | **no** |
`DevSeedController` (19 sites) | `StockBalances`, `StockMovements`, `sys.tables` | no | **no**; and `[DevOnly]` 404s the controller outside Development |

Two rules this produces for B5, stated now so B2 cannot be misread:

- **`FromSql` on an entity IS filtered** (EF composes the filter around it) — which is a *risk*, not a protection,
  and is why `StockBalance` is out.
- **`SqlQueryRaw<T>` for a scalar or a non-entity type is NEVER filtered.** No claim will be made that global filters
  protect it.

---

## 5. Write-side tampering risks (B4 input)

| # | Risk | Evidence | Assessment |
|---|---|---|---|
**1** | **`AccountingApiController` takes `int companyId = 1` from the request** on at least 10 actions, including **`PayrollPost`** — which posts to the GL through `JournalEntry`, a pilot entity — plus `Journals`, `TrialBalance`, `Ledger`, `Customers`, `Accounts`, `Summary`, `PayrollPreview`. | `Controllers/Api/AccountingApiController.cs:34,90,100,187,195,206,214,226` | **A live cross-company read AND write vector: `?companyId=2` reads and posts another company's data.** Higher than the "asymmetry" it was classified as. **Authorization is Hotfix A.1 by instruction.** B4's write-side guard will *incidentally* block the `JournalEntry` write path, and that effect is reported rather than claimed as the hotfix. |
**2** | `AdminController.GetBranches(int? companyId)` / `GetHierarchicals(int? companyId)` | `AdminController.cs:418,518` | Targets **excluded** entities (`Branches`, `Hierarchicals`). No pilot impact; recorded for Batch D. |
**3** | The 8 controllers' `DefaultCompanyId = 1` | 767 call sites | **Not posted** — a compile-time constant. No tampering vector today; it is a tenancy *limitation*, not a hole. |

---

## 6. Expected compatibility impact

| Area | Impact |
|---|---|
**Normal staff requests** | **Behaviour-neutral.** Every pilot row is company 1 and every staff request resolves a company (Batch A), so a filter pinned to the resolved company selects exactly what the hand-written `.Where(x => x.CompanyID == DefaultCompanyId)` already selects. |
**The 767 `DefaultCompanyId` call sites** | Unchanged and still correct — they become **redundant** rather than load-bearing. B2 does not touch them. |
**`EntityRegistry.SearchAsync` / `ResolveAsync`** | Already filter on the context's company → **double filtering, harmless**. |
**`TimelineProjectionService`** | Already filters on company and visibility → harmless. |
**Anonymous storefront** | **Breaks** unless given an explicit public scope (§3 #5). |
**Outbox dispatcher + notification idempotency** | **Breaks** unless bypassed (§3 #1–3). |
**Elevated monitor view** | **Silently narrows** unless bypassed (§3 #4). |
**Per-company schedulers** | Need a scope per iteration (§3 #6). |
**Raw SQL** | Unaffected — nothing raw touches a pilot entity. |
**Writes** | Unaffected by read filters. EF applies query filters to reads, not to `SaveChanges`; write protection is B4's job and is independent. |

---

## 7. What B1 explicitly does NOT claim

- **Not system-wide isolation.** 12 of 261 entities are in the pilot — **4.6%**. 128 further `CompanyScopedDirect`
  entities remain unfiltered after Batch B, and that will be stated in the Batch B report as plainly as it is here.
- **No claim about `DeletedAt`.** No pilot entity soft-deletes.
- **No claim that filters protect raw SQL or writes.**

## 8. Confirmations requested before B2

| Question | Answer |
|---|---|
Each pilot entity's `CompanyID` ownership | **Confirmed** — all 12 own a direct column; `Notification` is the only nullable one |
Every excluded entity and the exact reason | **§2**, seven classes, each with evidence |
Legitimate cross-company flows requiring bypass | **§3**, six flows with file:line; two are Critical |
Raw SQL / `FromSql` risks | **§4** — zero on pilot entities; `StockBalance` confirmed unsafe |
Write-side tampering risks | **§5** — `AccountingApiController`'s request-supplied `companyId` is the live one |
Expected compatibility impact | **§6** — neutral for staff requests; four flows break without B3 |

**Proceeding to B3.** No filter will be enabled until the bypass exists and its tests pass.
