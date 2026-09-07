# Stage 1 — Security, Company Isolation and Permission Foundation: batch roadmap

**Status at the time of writing: Batch A COMPLETE. Batches B, C and D NOT STARTED.**

Stage 1 establishes the security and tenancy foundation that Business Workspace, Communication Platform, Task and
Work Platform, Workflow and Approval Engine, Action Governance, Enterprise Search, AI Context and Copilot, Human
Capital, CRM, Business Intelligence, External Collaboration and the Industry Packs all depend on. **None of those
product platforms is implemented in Stage 1.** They are recorded as planned architecture in
[../architecture/Living-Architecture-Vision.md](../architecture/Living-Architecture-Vision.md).

| Batch | Scope | Status |
|---|---|---|
**A** | BusinessContext pipeline · session-free permissions · company-1 fallback removal | **Complete** |
**B** | Company isolation · EF global query filters · controlled bypass · write-side enforcement | Not started |
**C** | The three missing module access services: HR, Projects, Tasks/Communication | Not started |
**D** | Risk-ranked remediation of the corrected 189-action endpoint backlog | Not started |

**Ordering is a dependency chain, not a preference.** D cannot start before C, because 132 of the 189 actions sit in
modules that have no access service to reference. C is more useful after B, because a new access service written
against centrally-enforced isolation is smaller than one that re-implements company predicates by hand.

---

## Batch A — complete

BusinessContext source/factory pipeline · `ICompanyScopeHolder` (populated, read by nothing until B) ·
Accounting/Inventory/CRM/POS made context-aware and session-free via `IModuleAccessService` · adapters use the
supplied context · `IsSystem ⇒ Allow` replaced by `SystemContextPolicy` · real per-recipient checks in
`NotificationProjectionConsumer` · POS KDS/delivery session blobs completed · five shared-platform `CompanyID = 1`
defects removed · POS catalog constant contained in `PosCompanyPolicy`.

Records: [ADR-010](ADR-010-Session-Free-Permission-Evaluation.md) ·
[ADR-022](ADR-022-BusinessContext-Resolution-Policy.md) ·
[Stage-003 maturity result](../architecture/maturity/Stage-003-Stage-1-Batch-A-Result.md) ·
[CORRECTION-003](../architecture/CORRECTION-003-Permission-Attribute-Recognition.md).

---

## Batch B — company isolation and global query filter foundation

Not started. Blocked on Batch A review.

Scope as briefed: entity isolation classification (B1) · EF global query filters on a **pilot set** (B2) · controlled
auditable bypass, never a global static disable (B3) · write-side company enforcement (B4) · raw SQL and direct
`DbContext` safety (B5) · security backlog reclassification (B6) · a two-company/two-employee isolation test matrix
(B7).

**Two findings from Phase 1 that constrain B2 before it starts:**

1. **`StockBalance` must not be in the pilot.** `StockService:449` reads it with
   `FromSqlInterpolated("SELECT * FROM StockBalances WITH (UPDLOCK, HOLDLOCK) WHERE CompanyID = {companyId}")`. EF
   composes a global filter **around** a `FromSql`, which changes the plan for the row lock that prevents
   overselling — in the sole stock writer. Not filtered until the lock is proven unchanged.
2. **`Employee`, the four role tables, `Companies`, `Branches` and `Hierarchical` must not be filtered.** The context
   resolution path reads `Employee`; `ServiceController.CompaniesList` is genuine cross-company administration; and
   `PosController` legitimately lists companies across branches.

Proposed pilot (12 entities, four modules): `JournalEntry`, `SalesInvoice`, `PurchaseInvoice`, `Customer`, `Item`,
`Warehouse`, `Quotation`, `Lead`, `Opportunity`, `CrmAccount`, `BusinessEvent`, `Notification`.

---

## Batch C — missing module access services

**Not started. Do not implement during Batch A or B.**

### Why this batch exists

[CORRECTION-003](../architecture/CORRECTION-003-Permission-Attribute-Recognition.md) changed the *shape* of the
security backlog, not only its size. **132 of the corrected 189 unprotected mutating actions sit in three modules
that have no access service at all:**

| Module | Backlog actions | Access service today |
|---|---|---|
HR (`AdminController` 42, `PeopleController` 5, `HrApiController` 2, `LeaveApiController` 2) | **51** | **none** |
Projects (`ProjectController`) | **30** | **none** |
Tasks and Communication (`TasksController` 13, `ChatController` 9, `FileManagerController` 5, `CommController` 4, `AnnouncementsController` 3, `CommentsController` 2, `CalendarController` 2, `NotificationsController` 1, `NotificationsApiController` 2) | **41** | **none** |
| **Total** | **122–132** depending on how the API surface is attributed | |

For these, `DefaultPermissionAdapter` grants `View` to a company-scoped authenticated user and refuses every elevated
action — because there is no policy to delegate to. **Adding attributes to those 132 actions is impossible before the
services exist**, which is precisely why Batch D follows Batch C.

### Required scope

**Real vocabularies derived from existing business behaviour — not invented generic roles.** The precedent is
Batch A's four modules, each of which has a vocabulary that reflects what its module actually does
(`read/post/pay/manage/currency-override` for accounting; predicates for POS). A generic `read/write/admin` triple
would be a fifth vocabulary that describes nothing.

Evidence to derive each vocabulary from, all of which already exists:

| Module | Existing behaviour to read the vocabulary out of |
|---|---|
**HR** | `LeaveWorkflowService.ManagerChainAsync` (manager chain is already a real relation) · `EmployeeRequestService` · `HrDocumentService` (document confidentiality) · `FinalSettlementService` · `AppraisalService` (appraiser vs appraisee) · `Hierarchicals` org tree (`H_Type = 5` employee nodes) · the existing `LeaveApprovalStep.Level` semantics |
**Projects** | `ProjectService` · `ContractService` (advance/retention are financial) · `ProgressBillingService` (المستخلص — a GL-posting action) · `ProjectMaterialIssueService` / `ProjectLaborService` (actual cost) · `BoqService` · `ProjectBudgetService` (read-only) |
**Tasks / Communication** | `TasksController` assignment and progress · `DocCommentService` (already entity-addressed) · `ChatService` conversation membership (`ConversationMember` is the real access rule) · `FileManagerService` · `AnnouncementService` |

Each service must deliver:

- **Module-specific roles and actions**, with the role table added as idempotent SQL under `deploy/sql` (the three
  existing role tables — `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles` — are the pattern).
- **`IModuleAccessService` implementation** — context-aware `CanAsync`, session-free, deny-by-default, published
  `Actions`.
- **Record-level rules where they genuinely exist**: HR has the strongest case (manager chain, own-record,
  confidential documents); Chat has `ConversationMember`; Projects has project membership.
- **A `PlatformPermissionProvider` adapter** per module, replacing `DefaultPermissionAdapter` for those scopes.
- **Controller attribute compatibility** — new `[HrPerm]` / `[ProjectPerm]` / `[TaskPerm]` attributes following the
  existing `AccPerm`/`InvPerm` shape, so Batch D can apply them mechanically.
- **Background-worker compatibility** — every method must answer for an explicit employee, as Batch A's four now do.
  `TaskGeneratorHostedService`, `CrmReminderHostedService` and `TaskScheduleMatchHostedService` are the first callers.
- **Business Workspace / Unified Inbox / AI Context compatibility** — the same contract, so a Workspace tab, an inbox
  filter and an AI retrieval all ask one question. No module-specific side channel.
- **Tests**, **documentation** (vocabulary mapping tables, as ADR-010 does for the existing four), and a **maturity
  measurement**.

**Bootstrap-open must be a deliberate decision, per module.** The three existing services grant everything when their
role table is empty company-wide. It is a documented anti-lockout measure and it is also the reason
`PlatformOpsAttribute` inherits an open gate on an unconfigured install. Each new service must state its choice
explicitly rather than copy the pattern by reflex.

---

## Batch D — risk-ranked endpoint permission remediation

**Not started. Do not implement during Batch A, B or C.**

Remediate the corrected **189**-action backlog in waves. **No bulk attribute application** — every endpoint is
assessed individually, because several are already gated by a different mechanism (POS session, `PosLaneActivityGuard`,
`DevOnly`) that a naive attribute would duplicate or contradict.

### Waves

| # | Wave | Indicative content |
|---|---|---|
1 | **Critical financial and stock mutations** | the 2 remaining `AccountingController` writes without `AccPerm`; the 10 `AccountingApiController` writes; the 1 `InventoryController` write |
2 | **POS financial operations** | `PosController` 42 — separating genuine setup writes from lane operations already covered by `PosLaneActivityGuard` |
3 | **Admin, identity and HR security operations** | `AdminController` 42, `PeopleController` 5, `AccountController` 2, `ServiceController` 2, `HrApiController` 2, `LeaveApiController` 2 — **requires Batch C's HR service** |
4 | **Projects and billing operations** | `ProjectController` 30 — **requires Batch C's Projects service** |
5 | **Tasks and communication mutations** | `TasksController` 13, `ChatController` 9, `FileManagerController` 5, `CommController` 4, `AnnouncementsController` 3, `CommentsController` 2, `CalendarController` 2, `NotificationsController` 1, `NotificationsApiController` 2 — **requires Batch C's Tasks/Communication service** |
6 | **Lower-risk setup operations** | `BrandController` 3, `AiController` 4, remaining singletons |

### Per-endpoint record required before any change

Every remediation carries: **expected module permission · company isolation status · anti-forgery status · direct
`DbContext` usage · tests · compatibility review.** Two of those are already measured per action in
`evidence/Endpoint-Inventory.csv` (`antiforgery`, `permission_attributes`, `inherited_permission`), and
`Controller-Inventory.csv` carries `direct_dbcontext` per controller — so the backlog table is generated, not
hand-maintained.

### Two exclusions to state up front

- **`AuthApiController.Login`** is intentionally anonymous and stays out of the backlog.
- **`DevSeedController`** has **224 `[HttpGet]` endpoints and zero `[HttpPost]`**, under `[AllowAnonymous]` +
  `[DevOnly]`. They are real mutations but not mutating *verbs*, so they are correctly absent from the 189. The
  residual risk is one attribute and one `ASPNETCORE_ENVIRONMENT` value; recorded in CORRECTION-003 as a risk, not
  scheduled for Batch D.

---

## What Stage 1 will still not have delivered when D is done

Stated so the foundation is not mistaken for the platforms built on it:

- no Workspace UI, Unified Inbox, Workflow Designer, Escalation Dashboard, AI Copilot, Report Center, Customer Portal,
  HR screens, CRM Command Center or Entity Registry Explorer;
- no workflow engine — the four approval silos are untouched;
- no entity-addressed Communication (`CommMessage` still has no `EntityType`);
- 9 of 58 business objects registered;
- **no tests for the two sanctioned writers' accounting or costing behaviour**, which remains the largest single gap in
  Testing and Reliability.

---

## Hotfix A.1 — delivered (2026-08-04)

`AccountingApiController` authorization + company isolation. Separate delivery, separate report:
[Stage-001-Hotfix-A1-Delivery-Report.md](Stage-001-Hotfix-A1-Delivery-Report.md) ·
analysis: [Stage-001-Hotfix-A1-Analysis.md](Stage-001-Hotfix-A1-Analysis.md) · policy: ADR-025.

10 mutating actions + 12 reads secured · permission backlog **193 → 183** · 79 tests · maturity **unchanged at
54.20%** (level 80 requires "no hard-coded company anywhere", which is still false outside the API).

**Remaining Stage 1 work, not started:** Batch C (HR / Projects / Tasks-Communication access services) and
Batch D (risk-ranked remediation of the 183-action backlog).

---

## Batch C — delivered (2026-08-04)

Missing module access services: HR · Projects · Tasks · Communication.
[Delivery report](Stage-001-Batch-C-Delivery-Report.md) · [analysis](Stage-001-Batch-C-Analysis.md) ·
[approved architecture](Stage-001-Batch-C-RBAC-Architecture-Proposal.md) · ADR-026/027/028/029.

One shared `PlatformRoleAssignments` + module-specific `ProjectMembers` · `IPlatformRoleDirectory` · `IOrgHierarchy`
(the company intersection `Hierarchical` never had) · four access services · four adapters · additive
`PermissionTarget` forwarding · `AccessScope` · **four proof endpoints** · **+114 tests (606 total, 0 skipped on SQL
Server)** · maturity **unchanged at 54.20%** · backlog **183 → 183** (−2 Batch C, +2 concurrent).

**Two disclosed Partial items:** the `AccessScope` no-per-record-call demonstration test, and SignalR hub adoption.

**Remaining Stage 1 work:** **Batch D** — remediate the 183-action backlog now that the foundations exist, repoint the
two existing `Hierarchical` walks at `IOrgHierarchy`, and remove `DefaultCompanyId` from the MVC controllers.
