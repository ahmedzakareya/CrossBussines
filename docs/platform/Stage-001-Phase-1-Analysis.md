# Stage 1 — Phase 1 pre-implementation analysis

**Status: analysis only. No Stage 1 code has been written.**
Every Stage 1 requirement is **Not Started** at the time of writing (table at the foot).

**Produced:** 2026-08-03, by inspection of the working tree at the end of Stage 0.
**Correction issued during this analysis:** [CORRECTION-003](../architecture/CORRECTION-003-Permission-Attribute-Recognition.md)
— the security backlog is **189**, not 306.

---

## 1. Files inspected

### Read in full

| File | Lines | Why |
|---|---|---|
`Models/Platform/BusinessContext.cs` | 37 | A1 — the contract to extend |
`BL/Platform/BusinessContextAccessor.cs` | 115 | A2 — the only existing resolution pipeline |
`BL/AccountingAccessService.cs` | 76 | A3/A4 — session-bound |
`BL/InventoryAccessService.cs` | 90 | A3/A4 — session-bound |
`BL/CrmAccessService.cs` | 121 | A3/A4 — session-bound, plus row-level ownership |
`BL/PosAccessService.cs` | 73 | A4 — the only session-free one |
`BL/Platform/PlatformPermissionProvider.cs` | 242 | A5 — provider + 6 adapters |
`BL/Platform/NotificationProjectionConsumer.cs` | 193 | A6 — the target consumer |
`BL/Platform/WorkerCompanyScope.cs` | 83 | A2 — worker company iteration (Stage 0) |
`Models/AccPermAttribute.cs`, `SessionValidationAttribute.cs` | 28 + 30 | A3 — compatibility surface |
`Models/Context/CrossDbContext.cs` (head + `OnModelCreating`) | 462 | B2 — filter foundation |
`Models/Context/Admin/Employee.cs`, `ViewModel/EmployeeViewModel.cs` | — | A2 — identity mapping |

### Scanned / queried

`Program.cs` (DI + DbContext registration) · all 42 controller files (via `scan-architecture.ps1`) ·
`Hubs/{Notifications,Chat,Pos}Hub.cs` · `Controllers/AccountController.cs` (session write) ·
`Controllers/PosAppController.cs` (session write) · `Controllers/ServiceController.cs` (company admin) ·
`BL/StockService.cs` + `BL/JournalEntryService.cs` (raw SQL) · `BL/HolidayService.cs` ·
`BL/InventoryApprovalService.cs` · `BL/PosSetupService.cs` · `docs/architecture/evidence/*.csv`.

---

## 2. Current context-resolution map

### 2.1 The one existing pipeline

```
IBusinessContextAccessor.GetCurrentAsync()
  ├─ source 1: HttpContext.Session["Employee"]  → EmployeeViewModel {ID, EmpCompanyID, BranchID, UserId}
  ├─ source 2: ClaimTypes.NameIdentifier → IEmployeeService.GetEmployeeByUserIdAsync → Employee row
  ├─ top-up:   _db.Employee (EmpCompanyID, BranchID) when the session blob is incomplete
  ├─ roles:    ClaimTypes.Role claims
  └─ FALLBACK: CompanyId = FallbackCompanyId (= 1)          ◄── DEFECT, A7 target
```

Cached per DI scope; one `CorrelationId` per scope. `IsSystem` is only ever set by
`BusinessContext.ForSystem(companyId)`.

### 2.2 The four identity/company sources actually in use

| Source | Where | Carries | Trustworthy? |
|---|---|---|---|
`Session["Employee"]` (JSON `EmployeeViewModel`) | 24 sites | ID, EmpCompanyID, BranchID, UserId, FullName | **No** — can be stale, and one writer omits every id (§5.2) |
`ClaimTypes.NameIdentifier` | accessor, `CommentsController`, `ApprovalsController` | AspNetUser id | Yes |
`Employee` row (`EmpCompanyID`, `BranchID`) | accessor top-up, `PosAccessService` | authoritative company/branch | **Yes — this is the authority** |
`Session["PosCtx"]` / `Session["HyperCtx"]` | 4 + 4 sites | `PosLoginContext` {EmployeeId, BranchId, CompanyId=1, Roles} | Branch-authoritative; **CompanyId is a literal 1** |

**Session keys in the entire application: 4** — `Employee` (24), `PosCtx` (4), `HyperCtx` (4), `probe` (2).

### 2.3 There is no company switching

No `SwitchCompany` / `SelectCompany` / `ChangeCompany` action exists anywhere. **The current company is a function of
`Employee.EmpCompanyID` and nothing else.** Consequences for A2:

- there is no user-selected company to validate against an allow-list — which removes a whole class of risk;
- `PosController:46` lists companies (`ViewBag.Companies`) filtered to the branches the user can reach — a **read-only**
  multi-company view, not a switch;
- `ServiceController.CompaniesList` → `GetAllCompaniesAsync()` is genuine **cross-company administration** and must
  survive Batch B.

### 2.4 Lifecycle

`AddDbContext<CrossDbContext>(…, ServiceLifetime.Scoped)` — **no pooling**. `IBusinessContextAccessor` is `Scoped`.
Hosted services create a scope per batch (`BusinessEventDispatchWorker`) or per pass (the four schedulers), so a
scope-cached context cannot leak between iterations. **Verified, and it is what makes A1 rule 7 already true today.**

---

## 3. Access-service session-dependency map

### 3.1 Comparison matrix

| | Accounting | Inventory | CRM | POS |
|---|---|---|---|---|
Vocabulary | `read · post · pay · manage · currency-override` | `read · doc · purchase · manage` | `read · edit · manage` | `pos-cashier · pos-manager · pos-kitchen · pos-waiter` predicates |
Role table | `AccountingUserRoles` | `InventoryUserRoles` | `CrmUserRoles` | `BranchUserRoles` |
Employee resolution | **`Session["Employee"]`** | **`Session["Employee"]`** | **`Session["Employee"]`** | **`userId` argument** |
Company | **`const int CompanyId = 1`** | **`const int CompanyId = 1`** | **`const int CompanyId = 1`** | `PosLoginContext.CompanyId = 1` default |
Async? | yes | yes | yes | **no** — sync predicates over a role list |
Bootstrap-open | **yes** (`AnyRoleConfiguredAsync` false ⇒ allow all) | **yes** | **yes** | no |
Row-level scope | none | `CanUseWarehouseAsync(warehouseId)` | **`VisibleOwnerIdsAsync()` + `TeamOwnerIdsAsync()`** | branch via `BranchUserRoles` |
Branch scope | none | keeper `ScopeBranchId` → `Warehouse.BranchHierarchicalId` | none | `Employee.BranchID` |
**Session-free?** | **No** | **No** | **No** | **Yes** |

### 3.2 The exact session-reading members

| Member | File:line | Reads |
|---|---|---|
`AccountingAccessService.CurrentEmployeeId()` | `BL/AccountingAccessService.cs:26-31` | `Session["Employee"]` |
`AccountingAccessService.MyRolesAsync()` | `:33-38` | via `CurrentEmployeeId()` |
`AccountingAccessService.CanAsync(action)` | `:43-59` | via `MyRolesAsync()` |
`AccountingAccessService.RoleLabelAsync(bool)` | `:61-74` | via `MyRolesAsync()` |
`InventoryAccessService.CurrentEmployeeId()` | `BL/InventoryAccessService.cs:27-32` | `Session["Employee"]` |
`InventoryAccessService.MyRolesAsync()` / `CanAsync` / `CanUseWarehouseAsync` / `RoleLabelAsync` | `:34-88` | via `CurrentEmployeeId()` |
`CrmAccessService.CurrentEmployeeId()` | `BL/CrmAccessService.cs:30-35` | `Session["Employee"]` |
`CrmAccessService.MyRolesAsync()` / `CanAsync` / `VisibleOwnerIdsAsync` / `RoleLabelAsync` | `:37-119` | via `CurrentEmployeeId()` |
`PosAccessService.*` | — | **nothing** — takes `userId` / role list as arguments |

**No access service reads claims or any static current-user state.** The dependency is exclusively
`IHttpContextAccessor.HttpContext.Session`.

### 3.3 Call sites that must keep working

| Member | Sites | Where |
|---|---|---|
`CanAsync(...)` | **34** | `PlatformPermissionProvider` (14 across 6 adapters), `InventoryController` (8), `AccountingController` (8), `TimelineProjectionService` (3), the four attributes (1 each) |
`CurrentEmployeeId()` | **26** | controllers + views |
`CanSell(...)` | 19 | POS controllers/views |
`IsManager(...)` | 10 | POS |
`VisibleOwnerIdsAsync()` | 1 | `CrmController` |
`MyRolesAsync()` | 1 | role-label UI |
`CanUseWarehouseAsync(...)` | **0** | **declared but never called** — a latent gate |

Attribute usage (post-CORRECTION-003): `AccPerm` **55** · `InvPerm` **74** · `CrmPerm` **37** ·
`PosLaneActivityGuard` **2 class-level (71 actions)** · `PlatformOps` **2 class-level (5 actions)** ·
`DevOnly` **1 class-level (224 actions)** · `SessionValidation` **153**.

### 3.4 The Stage 0 limitation, stated exactly

`IPlatformPermissionProvider.CanAsync(context, …)` **takes** a `BusinessContext`, and
`AccountingPermissionAdapter` / `InventoryPermissionAdapter` / `ManufacturingPermissionAdapter` /
`CrmPermissionAdapter` **throw it away** — they call `_access.CanAsync(moduleAction)`, which reads the session.

Two concrete consequences:

1. **Inside a worker there is no session**, so `CurrentEmployeeId()` is `null`, `MyRolesAsync()` is empty, and the
   answer is *not specific to the employee being checked*. `NotificationProjectionConsumer` builds a correct
   per-recipient context (`BuildRecipientContextAsync`, `IsSystem = false`) and the adapters ignore it. The consumer
   says so in a 14-line comment — it is honest, and it is the thing A6 must close.
2. `PlatformPermissionProvider:54` short-circuits `context.IsSystem ⇒ Allow("system context")`. Correct for the
   dispatcher's own reads, but it means **any caller that sets `IsSystem` bypasses every module check**. That is
   fine while only trusted kernel code constructs system contexts; it becomes the highest-value attack surface the
   moment AI/Search/Workflow reuse the provider. A5 must keep `IsSystem` explicit and narrow.

`PosPermissionAdapter` is already session-free (it resolves from `context.UserId`) — so **POS is the working
reference implementation**, not the odd one out.

---

## 4. Company-isolation entity classification (input to B1)

From `evidence/Entity-Inventory.csv` (261 entity classes, 218 DbSets):

| | Count |
|---|---|
Entities **with** a `CompanyID` property | **140** |
Entities **without** one | **121** |
Entities with a `BranchID` property | **22** |

### 4.1 Proposed classification (evidence-based, to be finalised in B1)

| Class | Count (est.) | Examples | Filter in the pilot? |
|---|---|---|---|
**CompanyScopedDirect** | ~140 | `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`, `Customer`, `Vendor`, `Item`, `Warehouse`, `Quotation`, `Lead`, `Opportunity`, `BusinessEvents`, `Notifications`, `CommMessages` | **candidates** |
**CompanyScopedIndirect** | ~35 | every `*Line` (`SalesInvoiceLine`, `JournalEntryLine`, `StockCountLine`, …), `BusinessEventDispatch`, `CommAttachment`, `AnnouncementRead`, `ChatMessage`, `ChatReaction`, `ConversationMember`, `CalendarEventAttendee`, `DepreciationLine`, `BankReconciliationLine`, `ItemBarcode`, `PriceListLine`, `UoMConversion`, `ItemWarehouseSetting`, `BinLocation`, `LandedCostCharge` | **no** — isolate via the parent; a filter needs a navigation and risks N+1 |
**GlobalReference** | ~10 | `AccountType`, `Currency`, `ExchangeRate`, `ItemTypes`, `Country`, `CompanyTypes` | **never** |
**CrossCompanyOperational** | ~4 | `Companies`, `Hierarchical` (org tree), `Branches` (read across companies in `PosController`), `AspNet*` Identity tables | **never** — filtering these breaks `ServiceController.CompaniesList` and login |
**BranchScoped** | 22 | `BranchPosSettings`, `KitchenStations`, `BranchUserRoles`, `PosOrders`, `DeliveryZones` | **not in the pilot** — branch semantics differ per module |
**SecuritySensitive** | 5 | `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles`, `BranchUserRoles`, `Employee` | **must not be filtered** — the resolution path reads them (§7.1) |
**LegacyUnclear** | ~10 | **`FiscalPeriod` (no `CompanyID` at all — risk R12)**, `LeaveRequest`, `LeaveApprovalStep`, `EmployeeRequestStep`, HR policy tables | **never, per the brief** |

### 4.2 Pilot proposal (B2) — 12 entities across four modules

Chosen because each has a direct `CompanyID`, unambiguous single-company ownership, no legitimate cross-company
default query, and existing test coverage.

| Module | Entities |
|---|---|
Accounting | `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`, `Customer` |
Inventory | `Item`, `Warehouse`, `Quotation` |
CRM | `Lead`, `Opportunity`, `CrmAccount` |
Platform | `BusinessEvent`, `Notification` |

**Deliberately excluded from the pilot, with the reason:**

- **`StockBalance`** — `StockService:449` reads it with
  `FromSqlInterpolated("SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = …")`. EF composes a
  global filter *around* a `FromSql`, which changes the plan for the row-lock that prevents overselling. This is the
  concurrency-critical path in the sole stock writer. **Not filtered until the lock is proven unchanged.**
- **`Employee`** — read by the context-resolution path itself (§7.1).
- **`Companies` / `Branches` / `Hierarchical`** — legitimate cross-company reads.
- All `*Line` children — isolated by their parent.

---

## 5. Hard-coded company findings

**Every occurrence, classified. 727 call sites across 8 controllers, 5 service-level constants, 2 accessor
constants.**

### 5.1 Shared platform / security code — **A7 must fix these**

| # | Location | Form | Classification | Why it matters |
|---|---|---|---|---|
1 | `BL/Platform/BusinessContextAccessor.cs:30,102` | `public const int FallbackCompanyId = 1` and `CompanyId = companyId is > 0 ? … : FallbackCompanyId` | **Defect** | The *platform* context silently becomes company 1 for any unresolved caller. Everything downstream — events, notifications, timeline, the monitor — inherits it. |
2 | `BL/AccountingAccessService.cs:21` | `private const int CompanyId = 1` | **Defect** | Roles are read for company 1 regardless of the user's company. On a second company, `AnyRoleConfiguredAsync` looks at company 1's table ⇒ either wrongly open or wrongly closed. |
3 | `BL/InventoryAccessService.cs:22` | same | **Defect** | as above, plus `CanUseWarehouseAsync` matches warehouses in company 1 only. |
4 | `BL/CrmAccessService.cs:25` | same | **Defect** | as above, plus `VisibleOwnerIdsAsync` scopes ownership to company 1's roles. |
5 | `BL/InventoryApprovalService.cs:26` | same | **Defect** (approval = security-adjacent) | Approval thresholds and records resolve to company 1. |

### 5.2 A production path where the fallback actually fires

`Controllers/PosAppController.cs:461` and `:582` write the session blob as:

```csharp
new EmployeeViewModel { FullName = c.EmployeeName, Email = "", ProfileImage = "" }
```

— **no `ID`, no `EmpCompanyID`, no `BranchID`.** A POS user opening the KDS or delivery board therefore produces
`employeeId = null`, `companyId = null` in `BusinessContextAccessor`, and **the company-1 fallback fires**. This is
not hypothetical; it is the code path a kitchen screen takes today. It is the single strongest argument for A7.

### 5.3 Deliberate single-company configuration — contain and document, do not "fix"

| Location | Form | Classification | Reason |
|---|---|---|---|
`BL/PosSetupService.cs:805` | `private const int PosCompanyId = 1` | **Deliberate** | Documented: cash accounts and catalog live under company 1 while branches sit under companies 65–79. Changing it changes POS accounting. **Out of Stage 1 scope** (the brief forbids changing POS calculations). |
`BL/PosAccessService.cs:16` | `PosLoginContext.CompanyId { get; set; } = 1` | **Deliberate, but security-relevant** | Same rationale, but it feeds `PosPermissionAdapter`. Must become explicit rather than defaulted. |

### 5.4 Write-side defect

| Location | Form | Classification |
|---|---|---|
`BL/HolidayService.cs:27` | `if (h.CompanyID <= 0) h.CompanyID = 1;` | **Defect** — a write-side silent default. Belongs to B4, not A7 (it is not in shared security code). |

### 5.5 Presentation-layer constants — measurable backlog, **not** Batch A scope

`private const int DefaultCompanyId = 1` in **8 controllers**, plus `HrCompanyId = 1` in `AdminController`:

| Controller | Usages |
|---|---|
`InventoryController` | **266** |
`AccountingController` | **192** |
`CrmController` | 97 |
`ProjectController` | 83 |
`PosController` | 40 |
`TasksController` | 33 |
`CurrencyController` | 9 |
`BrandController` | 7 |
`AdminController` (`HrCompanyId`) | ~40 |
| **Total** | **~767** |

**Classification: deliberate single-company compatibility setting.** Rewriting 767 call sites is exactly the "mass
change" the brief forbids, and doing it without query filters underneath would move risk rather than remove it. The
Batch A design gives each controller a **one-line** migration path (`await CurrentCompanyIdAsync()`), and Batch B's
filters make the constant redundant rather than load-bearing. Tracked, counted, and left alone in Batch A.

---

## 6. Raw SQL and direct-DbContext findings

### 6.1 Raw SQL inventory (complete)

| File | Call | Parameterised? | Company predicate? | Filter would apply? |
|---|---|---|---|---|
`BL/StockService.cs:449` | `FromSqlInterpolated` on `StockBalances` with `WITH (UPDLOCK, HOLDLOCK)` | **Yes** (interpolated ⇒ parameters) | **Yes** — `WHERE CompanyID = {companyId}` | Yes — EF composes the filter around it. **This is the risk**, not a protection. |
`BL/JournalEntryService.cs:313,322` | `SqlQueryRaw<int>` (number sequence) | **Yes** — positional args | **Yes** — `companyId` passed | **No** — scalar `SqlQueryRaw` is never filtered |
`BL/IntegrityCheckService.cs:309,+1` | `SqlQueryRaw<int>` / `<string>` | interpolated into SQL — **EF1002 warning present** | reconciliation queries | **No** |
`BL/PosOrderService.cs` | `SqlQueryRaw<int>`, `ExecuteSqlRawAsync` | Yes | Yes | **No** |
`Controllers/Api/DevSeedController.cs` | 9 × `SqlQueryRaw<int>`, 6 × `ExecuteSqlRawAsync`, 2 × `SqlQueryRaw<DecColRow>`, 1 × `SqlQueryRaw<decimal>`, 1 × `FromSqlInterpolated` | mixed | dev seeder | **No** — and `[DevOnly]` is the control |

**`IgnoreQueryFilters`: 0 occurrences. `HasQueryFilter`: 0 occurrences.** There is no existing filter to break and no
existing bypass to audit — B3 starts from a clean sheet.

### 6.2 Direct `CrossDbContext` in controllers

**28 of 42 controllers** inject `CrossDbContext` directly, including every controller in the 189-action backlog's top
five. Isolation in those queries is hand-written `.Where(x => x.CompanyID == DefaultCompanyId)`.

**This is why a controller receiving a `CompanyID` is not evidence of isolation** — the predicate is present in most
places and absent in an unknown number, and only a central filter makes the claim checkable.

### 6.3 Other isolation surfaces (item 17)

| Surface | Isolation today | Verdict |
|---|---|---|
SignalR notifications | `NotificationsHub` group `emp-{companyId}-{empId}` **plus** a company group | **Company-scoped** |
SignalR chat | `ChatHub` group per employee id; conversation groups by id | **No company in the group name** — relies on `Conversation.CompanyID` at query time |
SignalR POS | `PosHub` group per branch | Branch-scoped |
Uploaded files | `wwwroot/uploads/library/<company>/<guid>.ext` served by `UseStaticFiles` | **No authorization at all** (gap A4 / risk R5). Company appears in the *path*, which is not a control. |
API | Same session/claims path as MVC; `AccountingApiController` has 10 unguarded mutating actions | Same weaknesses |
Background workers | `WorkerCompanyRunner.ForEachCompanyAsync`, **no fallback to company 1** | **Correct** (Stage 0) |
Notifications consumer | per-recipient `BusinessContext`, `IsSystem = false` | Correct shape, **ineffective** until A4/A5 land |

---

## 7. Compatibility and security risks

### 7.1 The circular dependency that shapes Batch B

`IBusinessContextAccessor` → `CrossDbContext` (it queries `Employee`). If `CrossDbContext` were given a global filter
that reads the business context, resolving the context would require a filtered query on `Employee` **before the
company is known**.

**Consequence, decided now so Batch A can prepare for it:** the filter must read a *lightweight scope holder*
(`int? CompanyId`, no DbContext dependency) that the Batch A factory populates — **not** `IBusinessContextAccessor`.
`Employee` and the role tables must stay unfiltered.

### 7.2 Compatibility risks

| # | Risk | Severity | Mitigation |
|---|---|---|---|
C1 | Making `CanAsync` context-aware changes the answer where the session and `BusinessContext` disagree | **High** | Keep the existing signatures; have them resolve the context and delegate. Pin current semantics with tests **before** changing anything. |
C2 | `AnyRoleConfiguredAsync` per real company changes bootstrap-open behaviour on a multi-company install | **High** | Today it asks about company 1. Per-company is *more* correct but could newly lock out a company-2 user. Must be a documented, tested behaviour change. |
C3 | Removing `FallbackCompanyId` makes the POS KDS/delivery path throw where it silently worked | **High** | §5.2. Fix the session write **and** fail explicitly; both, in Batch A. |
C4 | 153 `[SessionValidation]` + 168 permission attributes must keep working unchanged | **High** | Attributes delegate to unchanged public methods. |
C5 | `TimelineProjectionService` calls `CanAsync` 3× | Medium | Same delegation. |
C6 | POS `PosLoginContext.CompanyId = 1` feeds the POS adapter | Medium | Make it explicit; do not change POS accounting. |
C7 | `CrmAccessService.TeamOwnerIdsAsync` loads the **entire** `Hierarchicals` table per call | Medium (performance) | Out of scope; noted. |

### 7.3 Security risks Batch A must not introduce

| # | Risk | Control |
|---|---|---|
S1 | A context-aware overload that callers can pass an arbitrary company to | The factory validates the employee↔company relationship; only a *system* context may name a company freely. |
S2 | `IsSystem ⇒ Allow` widening as more callers use the provider | Keep `IsSystem` constructible only through `ForSystem`/the system factory; assert in tests that a worker context for an employee is **not** system. |
S3 | Deny-by-default eroding into allow-by-default when identity is unresolved | Explicit tests for unresolved company **and** unresolved employee. |
S4 | Bootstrap-open silently extending to a new company | C2 above. |

---

## 8. Proposed file changes

### 8.1 To create

| File | Purpose |
|---|---|
`Models/Platform/BusinessContextSource.cs` | `enum BusinessContextSource { Http, Worker, System, Integration, Test }` |
`BL/Platform/BusinessContextFactory.cs` | `IBusinessContextFactory` + HTTP / Worker / System / ForEmployee creation, with validation. One file, per the module convention. |
`BL/Platform/ICompanyScopeHolder.cs` | scoped `int? CompanyId` holder with **no** DbContext dependency (prepares B2, §7.1) |
`BL/Platform/PermissionTarget.cs` | `PermissionTarget { EntityType, EntityId, WarehouseId, BranchId, OwnerEmployeeId, Visibility }` — only fields with a real caller |
`BL/Platform/IModuleAccessService.cs` | the canonical session-free contract the four services implement internally |
`CrossBuy.Tests/Stage1ContextTests.cs` | A8 items 1–5, 20 |
`CrossBuy.Tests/Stage1PermissionTests.cs` | A8 items 6–15, 18 |
`CrossBuy.Tests/Stage1NotificationAuthorizationTests.cs` | A8 items 16–17 |

### 8.2 To modify

| File | Change | Compatibility |
|---|---|---|
`Models/Platform/BusinessContext.cs` | add `Source`, `ActorEmployeeId`, `CrossCompanyScope`; make `ForSystem` explicit about source | additive |
`BL/Platform/BusinessContextAccessor.cs` | **delete `FallbackCompanyId`**; delegate to the factory; unresolved company ⇒ explicit failure | **behaviour change**, C3 |
`BL/AccountingAccessService.cs` | add `CanAsync(BusinessContext, action)`; existing `CanAsync(action)` resolves the context and delegates; company from context, not `const 1` | signatures preserved |
`BL/InventoryAccessService.cs` | same, plus `CanUseWarehouseAsync(BusinessContext, warehouseId)` | preserved |
`BL/CrmAccessService.cs` | same, plus `VisibleOwnerIdsAsync(BusinessContext)` | preserved |
`BL/PosAccessService.cs` | make `PosLoginContext.CompanyId` explicit | preserved |
`BL/Platform/PlatformPermissionProvider.cs` | adapters pass `request.Context` to the context-aware overloads | internal |
`BL/Platform/NotificationProjectionConsumer.cs` | remove the "honest limitation" comment; the check becomes real | behaviour: unauthorised recipients now excluded |
`BL/InventoryApprovalService.cs` | company from context | internal |
`Controllers/PosAppController.cs:461,582` | write a **complete** session blob (ID, EmpCompanyID, BranchID) | fixes C3 |
`Program.cs` | register the factory and the scope holder | additive |

### 8.3 Proposed SQL changes

**None in Batch A.** No schema change is required: every role table already carries `CompanyID`, and the context is
computed, not stored. Batch B may need none either — global query filters are model configuration.

---

## 9. Test plan (Batch A)

20 required cases from A8, mapped to the code that makes each provable:

| # | Test | Mechanism |
|---|---|---|
1 | HTTP context resolves the authenticated employee + company | fake `IHttpContextAccessor` with a session blob |
2 | Invalid selected company rejected | factory validates employee↔company |
3 | **Missing company does not become company 1** | assert the specific exception, and assert `FallbackCompanyId` no longer exists (source-level guard, as in Stage 0's worker test) |
4 | Worker context is explicit and isolated | `ForWorker(companyId)` requires the id |
5 | Context does not leak between scopes | two scopes, two contexts, distinct `CorrelationId` |
6–8 | Accounting / Inventory / CRM permission with **no** HTTP context | `IHttpContextAccessor.HttpContext == null` |
9 | POS adapter preserves semantics | same expectations as today's tests |
10–11 | Unknown action / unknown entity denied | existing provider paths |
12–14 | Confidential / Restricted / System visibility enforced | `TimelineProjectionService` + provider |
15 | Cross-company permission denied | employee of company 2 against a company-1 entity |
16–17 | Notification projection excludes unauthorised, keeps authorised | **two employees in the same role with different record access** |
18 | Existing attributes still functional | compiled-metadata assertions (Stage 0 pattern) |
19 | 230 existing tests green | full run |
20 | No production fallback resolves company 1 | repo-wide source assertion |

SQL Server tests: reuse `SqlServerFixture`; the multi-company cases (15, 16, 17) run in-process on SQLite and are
re-run on SQL Server where two companies are needed.

---

## 10. Rollback plan

| Change | Rollback |
|---|---|
New files (factory, holder, target, contract, tests) | delete; nothing else references them |
`BusinessContext` additions | additive with defaults — safe to leave |
Access-service context-aware overloads | additive; the old methods keep their behaviour |
**`FallbackCompanyId` removal** | the only genuinely breaking change. Rollback = restore the constant and the `is > 0 ? … : Fallback` expression (2 lines). Recorded here precisely so it is a 2-line revert. |
`PosAppController` session blob | revert to the 3-property blob (2 sites) |
`Program.cs` registrations | remove 2 lines |
No SQL, no schema, no data | nothing to undo |

**Deployment coupling: none.** Batch A is code-only. It does not depend on slice 2, slice 3 or
`platform_schema_history.sql` being applied.

---

## 11. Requirement status — before Batch A

| Req | Title | Status |
|---|---|---|
A1 | Business context contract | **Not Started** |
A2 | Business context factory | **Not Started** |
A3 | Session-free permission contract | **Not Started** |
A4 | Access service adapters | **Not Started** |
A5 | Platform permission provider | **Not Started** |
A6 | First real background permission consumer | **Not Started** |
A7 | Hard-coded company removal | **Not Started** |
A8 | Batch A tests | **Not Started** |
B1–B7 | Batch B | **Not Started** (blocked on Batch A approval) |