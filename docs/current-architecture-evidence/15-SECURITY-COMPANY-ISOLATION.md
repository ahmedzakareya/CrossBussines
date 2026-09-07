# 15 — Security and Company Isolation

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Authorization baseline — `engineering/authorization-baseline.json`

| Metric | Value |
|---|---|
| Mutating actions | **439** |
| Attribute-protected | **156** |
| In-body protected | **190** |
| **Gaps** | **93** |

Rule recorded in the file: *"This baseline may only SHRINK. CI rejects any commit that adds an entry.
A new unprotected mutating action fails the build."*

93 of 439 mutating actions (21%) have no proven authorization. That is the estate's largest open
security number.

## Analyzers — `CrossBuy.Analyzers/` (8 files)

`AuthorizationAnalyzer`, `AuthorizationDiagnostics`, `AuthorizationInventory`, `AuthorizationResolver`,
`AuthorizationSurface`, `AttributeFacts`, `BaselineDocument`, `EndpointModel`. Diagnostics CBA000–CBA006.
11 analyzer test files.

Not re-run in this pack: a full analyzer pass requires a build, and the shared tree has 772 dirty files
from other tabs. Last independently measured on a pristine worktree during the CRM foundation work:
**CBA000/001/004/006 = 0**, analyzer suite 102/102.

## Company isolation model

| Layer | Type | Notes |
|---|---|---|
| Request context | `BusinessContext`, `BusinessContextAccessor`, `BusinessContextFactory` | |
| Resolver | `RequestCompanyResolver` / `IRequestCompanyResolver` | fail-closed; a caller-supplied company is compatibility-only |
| Scope holder | `CompanyScopeHolder`, `ICompanyScopeHolder` | read by the query filters |
| Middleware | `CompanyScopeMiddleware` | |
| Query filters | `CompanyQueryFilters` — **12 pilot entities** | applied last in `OnModelCreating` (`CrossDbContext.cs:415`) |
| Write guard | `CompanyWriteGuardInterceptor` | same 12 entities; original value used for update/delete |
| Worker scope | `WorkerCompanyScope`, `WorkerCompanyRunner`, `WorkerScope.ForCompany` | per-company scope per worker iteration |
| Bypass | `ICompanyIsolationBypass` | kinds distinguish read-only from cross-company write |

The 12 pilot entities: `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`, `Customer`, `Item`,
`Warehouse`, `Quotation`, `Lead`, `Opportunity`, `CrmAccount`, `BusinessEvent`, `Notification`.
**Everything else — including all Task tables, all Communication tables, `InventoryApproval`,
`Hierarchical` and `Employee` — relies on hand-written predicates.**

## `CompanyId = 1` sites in production code

| Site | Classification | Basis |
|---|---|---|
| `PosSetupService.cs:805` `PosCompanyId` | **DESIGN-INTENTIONAL** | comment: *"cash accounts / catalog live under company 1 (branches sit under 65–79)"* |
| `PosAccessService.cs:44` `CatalogCompanyId` | **DESIGN-INTENTIONAL** | same catalog model |
| `HyperController.cs:17`, `HyperPosController.cs:25`, `PosAppController.cs:21` | **DESIGN-INTENTIONAL** | explicitly match `PosAppController.PosCompanyId` |
| `Platform/CertificationDataContract.cs:54` `CertificationCompanyId` | **TEST-ONLY** | certification fixture contract |
| `Uat/UatDatasetSeeder.cs:187` `PrimaryCompanyId` | **DEV-ONLY** | UAT seeding |
| `Api/DevSeedController.cs:225` | **DEV-ONLY** | dev seed endpoint |
| `AccountingController.cs:185` `DefaultCompanyId` | **DEFECT** | 191 uses; same class as the CRM defect already repaired |
| `AdminController.cs:37` `HrCompanyId` | **DEFECT** | HR pinned to company 1 |
| `Api/InventoryApiController.cs:17` `CompanyId` | **DEFECT** | API surface pinned to company 1 |
| `BrandController.cs:14`, `CurrencyController.cs:19` | **DEFECT** | small surfaces, same pattern |
| `HomeController.cs:15`, `StoreController.cs:12` `StoreCompanyId` | **UNKNOWN** | may be the same catalog model as POS; no comment says so |
| `HolidayService.cs:27` `if (h.CompanyID <= 0) h.CompanyID = 1;` | **DEFECT** | silent fallback on write |
| `Platform/BusinessContextAccessor.cs:13`, `RequestCompanyResolver.cs:8`, `TasksController.cs:25` | **not live** | all three are *comments* quoting the anti-pattern they exist to remove |

`InventoryController.cs` and `CrmController.cs` no longer appear: CRM was repaired (98 → 0) and
Inventory's 267 uses were repaired by another tab. `AccountingController` at 191 is now the largest
remaining instance.

## Access services

Seven modules register `IModuleAccessService`: Accounting, HR, Projects, POS, Tasks, Calendar,
Communication — and CRM as of `82f3a1a`. Each registers three ways (concrete, module interface,
`IModuleAccessService`) resolving one scoped instance. `PlatformPermissionProvider` selects an adapter
by scope; a module with no registered access service **denies**.

`IBootstrapAccessPolicyReader` is the canonical answer to "module not configured yet": explicit,
expiring, per-(company, scope, action) rows with a Never-first ordering. Consumed by Accounting and, as
of `82f3a1a`, CRM.
