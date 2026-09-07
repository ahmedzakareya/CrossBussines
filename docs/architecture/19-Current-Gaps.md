# 19 — Current Gaps

> WARNING - **CORRECTED. See [CORRECTION-001](CORRECTION-001-Manufacturing-Permission-Finding.md).** The
> manufacturing work-order permission finding published in the original discovery pass was **wrong** and has
> been withdrawn: all nine work-order write actions already carried `[InvPerm("doc")]` + anti-forgery. The
> permission-coverage numbers below are the regenerated, corrected figures.

## Scope
Prioritised register of everything discovery found missing, broken or inconsistent. Each gap carries evidence, a
size and a priority. Categories are kept separate as required.

Size: **S** ≤ days · **M** ≤ weeks · **L** ≤ months · **P** = program.

---

## A. Existing defects (something is wrong today)

| # | Gap | Evidence | Business impact | Technical impact | Security impact | Modules | Resolution | Size | Priority |
|---|---|---|---|---|---|---|---|---|---|
~~A1~~ | ~~**Work-order writes carry no permission attribute**~~ — **WITHDRAWN, THE FINDING WAS FALSE.** All nine work-order write actions already carried `[InvPerm("doc")]` + `[ValidateAntiForgeryToken]`; the discovery scanner could not read concatenated same-line attributes | [CORRECTION-001](CORRECTION-001-Manufacturing-Permission-Finding.md); 13 tests in `Slice3ManufacturingSecurityTests.cs` | none | none | **none** | — | Stage 0 added the genuinely-missing `[InvPerm("read")]` on the five read actions (defense in depth) | S | **Withdrawn — CLOSED** |
A2 | **Platform Kernel SQL not deployed** while its code is merged | `deploy/sql/platform_business_events*.sql` unexecuted; `RecordAsync` is in-transaction with no catch | sales/purchase invoice, customer and work-order creation **fail** | write paths throw | — | Accounting, Manufacturing | run both scripts before the next deploy | **S** | **Critical** |
~~A3~~ | ~~**Failed emails are never retried and invisible**~~ — **RESOLVED in Stage 0 Batch A.** `CommMessageDispatcherHostedService` + `SqlCommMessageDispatchStore` (UPDLOCK/READPAST claiming, bounded retry, stale reclaim, permanent-failure logging) | `BL/Comm/*`; `deploy/sql/comm_outbox_slice_003.sql`; 22 tests (13 in-process + 9 SQL Server) | — | — | — | Communication | done | S | **CLOSED** |
A4 | **Uploaded files served as unguarded static URLs** | `wwwroot/uploads/library/<company>/<guid>.ext`; retrieval bypasses every controller | HR documents / attachments readable by URL | — | **data exposure** | Communication, HR | serve through an authorizing controller | **M** | **High** |
~~A5~~ | ~~**Journal reversal emits no `BusinessEvent`**~~ — **RESOLVED in Stage 0 Batch A.** `JournalEntry.Reversed` recorded inside `ReverseAsync`'s ScopedTx, before commit, no swallowing catch; `Confidential` visibility; dedup-keyed | `BL/JournalEntryService.cs`; `Models/Platform/JournalEntryEventPayload.cs`; 8 tests | — | — | — | Accounting | done | S | **CLOSED** |
~~A6~~ | ~~**`DocComment.EntityType` accepts free text**~~ — **RESOLVED in Stage 0 Batch A.** `Quotation` registered; `DocCommentService.AddAsync` validates through `IEntityRegistry` on WRITE only, so historical rows keep loading. No alias mapping needed — the stored literal already equalled the canonical code | `BL/Platform/EntityRegistry.cs`; `BL/DocCommentService.cs`; 11 tests | — | — | — | Inventory, Platform | done | S | **CLOSED** |
~~A7~~ | ~~**Two workers hard-code `CompanyId = 1`**~~ — **RESOLVED in Stage 0 Batch A, and it was FOUR workers, not two**: `IntegrityCheckHostedService`, `CrmReminderHostedService`, `TaskGeneratorHostedService`, `TaskScheduleMatchHostedService`. All now iterate companies via `IWorkerCompanyScope` + `WorkerCompanyRunner` with per-company failure isolation and **no fallback to company 1** | `BL/Platform/WorkerCompanyScope.cs`; 10 tests incl. a source-level regression guard | — | — | — | Inventory, CRM, Tasks | done | S | **CLOSED** |
~~A8~~ | ~~**9 SQL scripts have no idempotency guard**~~ — **WITHDRAWN, THE FINDING WAS FALSE, and CLOSED in Batch B.** Zero deployable scripts are unsafe to re-apply. Two scanner defects produced the claim: a guard regex that recognised neither `WHERE NOT EXISTS`/`COL_LENGTH`/`IF @var IS NOT NULL` nor **predicate-idempotent DML** (`UPDATE t SET c = x WHERE c IS NULL` is re-runnable and no textual guard will ever see it), and a UTF-8-only read of a UTF-16LE file. `manifest.json` now records 88 `guarded`, 7 `guarded-by-predicate` (read and judged, with evidence), 14 `no-mutation`, **0 unread**, 1 `not-deployable` (`script.sql`, an unversioned scratch file excluded by name) | [CORRECTION-002](CORRECTION-002-Evidence-Scanner-Defects.md); `deploy/sql/manifest.json`; `scan-sql-manifest.ps1` | — | — | — | multiple | done | S | **Withdrawn — CLOSED** |
A9 | **Duplicated session gate with divergent bypass lists** | `SessionValidationMiddleware` + `SessionValidationAttribute` | a path opened in one and not the other | — | availability or exposure | all | collapse into one | **M** | Medium |

---

## B. Architectural debt

| # | Gap | Evidence | Impact | Modules | Resolution | Size | Priority |
|---|---|---|---|---|---|---|---|
B1 | **No EF global query filters** for `CompanyID` / `DeletedAt` | no `HasQueryFilter` in `CrossDbContext` | isolation is manual in ~1000 queries; soft-deleted rows can leak | all | add filters (additive) | **M** | **Critical** |
B2 | **`DefaultCompanyId = 1` in 8 controllers (~767 call sites)** — **PARTIALLY RESOLVED in Stage 1 Batch A**: the four access services' `const int CompanyId = 1`, `InventoryApprovalService`'s, and `BusinessContextAccessor.FallbackCompanyId` are **removed**, so authorization and identity are no longer evaluated against the wrong company. **Still open:** the 767 presentation-layer call sites (`InventoryController` 266, `AccountingController` 192, `CrmController` 97, `ProjectController` 83, `PosController` 40, `TasksController` 33, `CurrencyController` 9, `BrandController` 7, `AdminController` ~40 via `HrCompanyId`) | grep; [ADR-022](../platform/ADR-022-BusinessContext-Resolution-Policy.md) | tenancy is nominal in the presentation layer | all | each has a one-line path (`await CurrentCompanyIdAsync()`); Batch B's query filters make the constant redundant rather than load-bearing | **M** | **High** (was Critical) |
~~B3~~ | ~~**Four access services, three action vocabularies, two employee-resolution styles**~~ — **RESOLVED in Stage 1 Batch A.** All four now implement `IModuleAccessService.CanAsync(BusinessContext, action, PermissionTarget?)`, none reads the HTTP session in its permission path, and the adapters resolve modules by scope instead of casting. This is the prerequisite AI Context and per-recipient notification checks were blocked on — and the notification check is now real. **Still open:** the three vocabularies are addressable through one contract but not unified (unifying them would change semantics for 195 guarded actions), and HR/Projects/Tasks still have no service at all (Batch C) | [ADR-010](../platform/ADR-010-Session-Free-Permission-Evaluation.md); 53 tests | all | unify vocabularies only if a real need appears | **M** | **Medium** (was High) |
B4 | **14 of 38 controllers write via `CrossDbContext` directly** | `Controller-Inventory.csv:direct_dbcontext` | bypasses transactions, events, notifications | most | route writes through `BL/` | **L** | High |
~~B5~~ | ~~**Two `deploy/sql` folders, no manifest, no applied-scripts table**~~ — **RESOLVED in Stage 0 Batch B** (partially: the two folders remain). `deploy/sql/manifest.json` gives 110 scripts a dependency `rank`, a SHA-256, a detected encoding and a per-`GO`-batch idempotency verdict, and **surfaces the 4 file names that exist in both trees with different content** — so "apply `pos_setup.sql`" is now known to be ambiguous. `PlatformSchemaHistory` + `vw_PlatformSchemaCurrent` + `report-schema-history.ps1` answer "is the DB current?" directly, classifying every script as `applied`/`changed`/`failed`/`pending`/`missing` — `changed` being the finding a name-only log cannot produce. **Still open:** consolidating the two folders, which is a separate change with its own risk | `deploy/sql/manifest.json`; `platform_schema_history.sql`; [ADR-012](../platform/ADR-012-Deployment-Manifest-And-Schema-History.md); [SQL-Deployment-Runbook.md](../deployment/SQL-Deployment-Runbook.md) | all | consolidate the two folders | **S** | Medium (was High) |
B6 | **`LeaveApprovalStep` and `EmployeeRequestStep` are the same table twice** | identical shapes | two implementations drift | HR | unify during engine migration | **M** | Medium |
B7 | **Three "timeline" components with confusing names** | `_DocTimeline`=comments, `_DocEventTimeline`=events, `_Timeline`=CRM | developer + user confusion | Communication, CRM | rename | **S** | Medium |
B8 | **`AdminController` split across 4 partial files mixing HR/appraisals/recruitment/training** | file list | change risk | HR | split by concern | **M** | Medium |
B9 | **`DevSeedController` ≈12.7k lines with 176 unguarded writes** | `[DevOnly]` mitigates | one attribute guards production | Admin | move to a test project | **L** | Medium |
B10 | **Service layer returns Arabic UI strings** | `return (false, "العميل غير موجود", null)` | presentation in `BL/`; blocks localisation of errors | all | resource keys | **M** | Low-Medium |
B11 | **`JournalEntry.SourceType` — 52 free-text values** | grep | the backbone of legacy reconstruction is untyped | Accounting | freeze a vocabulary | **M** | Low-Medium |
B12 | **No FKs on 4 role tables and every approver column** | 08 | orphan role/approval rows | Security, HR, Inventory | add FKs | **S** | Medium |
B13 | **EF Core 9 + Localization 9 on ASP.NET Core 8 / net8.0** | csproj | supportability | all | align or record the decision | **S** | Low |

---

## C. Missing platform capabilities

| # | Capability | Status | Evidence | Dependencies | Size | Priority |
|---|---|---|---|---|---|---|
C1 | **Unified Workflow / Approval Engine** | **Missing** — 4 incompatible silos | 14 | needs: status normalisation (06), approver resolution (B3), deferred commands (silo 3) | **P** | **Critical** |
C2 | **Unified Work Inbox (actionable)** | **Partial** — `/Approvals` is read-only | 14 | C1 | M | High |
C3 | **`BusinessRelation`** (Book 1 Layer 3) | **Missing** — 3 partial string mechanisms | 08 cross-domain map | registry codes | M | High |
C4 | **`EntityFile(EntityType, EntityId)`** | **Missing** — 3 unrelated file mechanisms | 07 | registry codes, A4 | M | High |
C5 | **Followers / Observers** | **Missing** — no table | 07 | registry, events | S–M | Medium |
C6 | **Tags** | **Missing** — no table (dropped between Book 1 v1.1 and v2.0) | 07 | registry | S | Low |
C7 | **Enterprise Search** | **Missing** — 9 registry codes, everything else screen-local | 07 | registry expansion, events as feed | L | High |
C8 | **AI Context Platform** | **Missing** — 3 aggregate endpoints only | 09, 16 | **B3 is a hard prerequisite** (permission filtering before assembly) | L | Medium |
C9 | **Business Event Monitor** (an operational screen over the outbox) | **Missing** | `BusinessEventDispatch` is queryable but has no UI | none | S | High |
C10 | **Entity Registry Explorer** | **Missing** | registry is code-only | none | S | Low |
C11 | **Meetings / calls / voice notes** | **Missing** (labels only in `Activity.Type`) | 12 | separate scope | L each | Low |
C12 | **SaaS tenancy** | **Missing** — `TenantId` contract exists and is always null | 09 | B1, B2 | P | Low (deliberately last) |
C13 | **Persisted timeline projection** | **Missing** — read-time only | 09 | none | M | Low |
C14 | **`BusinessEvents` archival/retention** | **Missing** — direction documented only | 09, PKS-001 §10 | none | M | Medium |

---

## D. Product gaps

| # | Gap | Evidence | Priority |
|---|---|---|---|
D1 | Timeline covers **4 of 58** business objects | 07 | High |
D2 | Comments cover **3 of 58** | 07 | High |
D3 | No API for Accounting / Inventory / CRM / POS — the API surface is mobile-shaped | 16 | Medium |
D4 | No channels in chat (`Conversation.Kind` allows it) | 12 | Low |
D5 | Mentions are not persisted — "where was I mentioned?" is unanswerable | 12 | Medium |
D6 | Calendar is internal-only, no external sync | 01 | Low |
D7 | No payment gateway | 01 | Low |

---

## E. Security gaps
A1 · A4 · B2 · B3 plus:

| # | Gap | Evidence | Priority |
|---|---|---|---|
E1 | **189 of 384 mutating actions (POST/PUT/DELETE/PATCH) carry no module permission.** **Corrected DOWNWARD in Stage 1 Phase 1 from 306/381 — see [CORRECTION-003](CORRECTION-003-Permission-Attribute-Recognition.md), which found that 85 of 173 permission attributes were invisible to the scanner (fully-qualified names, plus `PosLaneActivityGuard` and `DevOnly` missing from the recognised list). Previously corrected upward in Stage 0 Batch B from 267/362** — a four-file `partial` `AdminController` was only partly attributed (24 → 42) and API controllers were absent from the table (+20). Worst: `AdminController` 42, `PosController` 42, `ProjectController` 30, `TasksController` 13, `AccountingApiController` 10, `ChatController` 9. **132 of the 189 are in HR, Projects and Tasks/Communication, which have NO access service** — so the fix is Stage 1 Batch C (build them) then Batch D (remediate), not attributes. **This is the Stage 1 security backlog**; Stage 0 deliberately did not remediate it, because bulk attribute application would change permission semantics for 189 endpoints at once and several are gated by a different mechanism (POS session, lane guard) | `Permission-Coverage.csv` (regenerated by `scan-architecture.ps1`); [CORRECTION-002](CORRECTION-002-Evidence-Scanner-Defects.md) | **Critical** |
E2 | **193 routed screens rely on authentication only** | `Screen-Inventory.csv` | High
E1a | **RESOLVED (Stage 1 Hotfix A.1)** — the `AccountingApiController` subset of E1: 10 mutating actions that let any valid mobile token post to the GL, post payroll, and target another company via `?companyId=` / `dto.CompanyID`. Now authorized + company-validated + 79 tests. **Backlog 193 → 183.** The remaining 183 are unchanged and still Critical as a set | `Permission-Coverage.csv`; [ADR-025](../platform/ADR-025-Accounting-API-Security-Policy.md) | **Resolved** | |
E3 | **3 of 4 access services allow everything when their role table is empty** | `AnyRoleConfiguredAsync()` | High |
E4 | **218 of 273 API actions have no `[Authorize]`** | `Permission-Coverage.csv` | High |
E5 | No RBAC for Manufacturing, HR, Projects, Tasks | 05, 13 | High |
E6 | Secrets in plaintext config (`Jwt:Key`, `Smtp:Password`, `AiService:Secret`) | 18 | High |
E7 | Workers are unconditionally authorized (`IsSystem` short-circuit) | 13 | Medium — correct for the dispatcher, unbounded for the rest |
E8 | No row-level authorization outside CRM | 13 | Medium |

---

## F. Performance risks

| # | Risk | Evidence | Priority |
|---|---|---|---|
F1 | `BusinessEvents` unbounded growth, no archival | 09 | Medium |
F2 | Legacy timeline adapters query per read (invoice + JE per open) | ADR-005 | Low-Medium |
F3 | `TimelineProjectionService` issues 3 permission calls per read | 09 | Low (cacheable) |
F4 | No output/response caching anywhere | 03 | Low |
F5 | No AI client timeout — a hung Python service holds request threads ~100s | 16 | Medium |
F6 | Runtime Razor compilation package shipped (dev-only path, but the reference is present) | 02 | Low |

---

## G. Data-integrity risks

| # | Risk | Evidence | Priority |
|---|---|---|---|
G1 | **Cross-company read via a forgotten `CompanyID` predicate** | B1 | **Critical** |
G2 | **`FiscalPeriod` has no company column** — period control may be global | 08 | High |
G3 | `LeaveRequest` + 76 classes have no company column | 08 | High |
G4 | Soft-deleted rows leak without a filter | 08 | Medium |
G5 | **Silo 3's `PayloadJson` is an unversioned command store** — approving an old payload after a code change may create a document with different semantics | 14 | High |
G6 | Orphan role/approval rows (no FKs) | 08 | Medium |
G7 | No audit interceptor — audit stamps set by hand per service | 08 | Medium |

---

## H. Operational risks
See 18 for the full list. Top: no applied-scripts tracking (Critical) · scale-out breaks 4 workers (Critical if
attempted) · no health checks (High) · `wwwroot/uploads` unbacked (High) · no CI (Medium).

---

## I. Documentation gaps

| # | Gap | Priority |
|---|---|---|
I1 | Design docs specify features that were **not** implemented (`CommMessage.EntityType/EntityId`, SMTP retry dispatcher). Reading them as architecture is a real hazard | High |
I2 | Single-worker-process constraint is undocumented in `DEPLOYMENT.md` | High |
I3 | No rollback documented for 103 of 105 SQL scripts | Medium |
I4 | Table ownership per module is nowhere recorded | Medium |
I5 | ADRs exist only for the Platform Kernel (7); the rest of the system has none | Medium |

---

## Gap count by priority

| Priority | Count |
|---|---|
**Critical** | **7** - A2, B1, B2, C1, E1, G1, H1/H2 *(A1 withdrawn - see CORRECTION-001)* |
High | 24 |
Medium | 23 |
Low | 12 |

## Dependencies
21-Modernization-Roadmap sequences these. 20-Architecture-Risks ranks the risk subset.

---

## Stage 1 Batch C — gap update (2026-08-04)

**Closed:** E-C1 — *"HR, Projects and Tasks/Communication have NO access service at all"*, which CORRECTION-003 named
as the reason 132 of the backlog sat in those modules. Four session-free, context-aware services now exist on one
shared role table, with record-level rules where the data supports them. See
[ADR-026](../platform/ADR-026-Shared-Platform-RBAC.md).

**Still open, and NOT reduced by the above:**

| Gap | Detail |
|---|---|
| **114 of 118 in-scope mutating actions unremediated** | Batch C protected **four** proof endpoints by instruction. An access service existing is not enforcement, and the backlog was not reduced for the other 114. Batch D. |
| **`IOrgHierarchy` not adopted by existing code** | `CrmAccessService.TeamOwnerIdsAsync` and `LeaveWorkflowService` still walk `Hierarchical` **without a company intersection**, so on a multi-company install a manager's team can include another company's employees. Predates Batch C; now has a fix available but not applied. |
| **SignalR hub authorization** | `IsConversationParticipantAsync` exists and is tested; no hub calls it yet. `ChatService`'s internal check still applies, so not a new hole. |
| **No membership administration UI** | `ProjectMembers` is populated by data until a screen exists. |
| **Bootstrap-open is temporary compatibility** | Any company with no role assignment for a scope behaves as before. It is logged at Information on every evaluation, per company and scope. |
| **Two role models coexist** | Legacy tables for four modules, the shared table for four others (ADR-026 §6). |
| **`DefaultCompanyId` in MVC controllers** | `AccountingController`, `ProjectController`, `TasksController`. Blocks maturity dimension 2 from level 80. |
