# Stage 2 - Consolidated Risk Register

> **GENERATED - do not edit by hand.** This document and
> `docs/architecture/evidence/Stage-002-Risk-Register.csv` are both emitted by
> `docs/architecture/evidence/generate-risk-register.py` from one canonical list, so they cannot diverge.
> Hand edits are overwritten on the next generation.

**50 risks** - 8 Critical, 25 High, 14 Medium, 3 Low.

Identifier policy: `RISK-###` here, `IMP-###` for improvements, `CORR-###` for corrections. Legacy ids from
accepted documents are preserved in the CSV `legacy_id` column and are not renamed in those documents.

## Critical (8)

### RISK-001 - 143 mutating actions reach no authorization authority

*Legacy id:* `R-01` | *Probability* Certain | *Impact* High | *Domains* All modules

**Evidence.** Regenerated Permission-Coverage: 388 = 157 + 88 + 143

**Current control.** Stage1PermissionBacklogTests pins 143; analyzer baseline designed not built

**Mitigation.** Waves 2-6 by risk rank; shrink-only analyzer baseline

*Target phase* **2A then waves** | *Owner* Platform owner | *Trigger* Any new unprotected mutating action

**Acceptance evidence.** Backlog strictly decreasing; CI rejects baseline additions

### RISK-002 - 59 High-risk endpoints unprotected incl privilege-granting and credential change

*Legacy id:* `R-02` | *Probability* Certain | *Impact* High | *Domains* HR Identity Projects AI

**Evidence.** Phase 1 wave plan W2=52 W3=7; AssignPosRole RemovePosRole ChangePassword; 4 AI endpoints return journal cashflow inventory

**Current control.** None beyond SessionValidation which is not authorization

**Mitigation.** Wave 2 using the proven gate pattern

*Target phase* **Wave 2** | *Owner* Platform owner | *Trigger* Any privilege or credential endpoint touched

**Acceptance evidence.** Per-endpoint tests incl company mismatch and identifier tampering

### RISK-003 - 13 controllers resolve company from a compile-time constant (908 references)

*Legacy id:* `R-03` | *Probability* Certain | *Impact* High | *Domains* Inventory Accounting CRM Projects POS Tasks HR

**Evidence.** InventoryController 267 AccountingController 201 CrmController 97 ProjectController 87

**Current control.** Correction005StructuralTests pins the 13 by name

**Mitigation.** Class A sweep as its own batch never inside a feature wave

*Target phase* **2C onward** | *Owner* Platform owner | *Trigger* A 14th constant appears

**Acceptance evidence.** Structural test; per-endpoint validated company source

### RISK-022 - Master Data migration could change stock valuation

*Legacy id:* `R-22` | *Probability* Medium | *Impact* Critical | *Domains* Inventory Accounting Manufacturing POS

**Evidence.** StockCostLayer and moving-average history; CostingMethod immutable once stock exists; ItemComponent read by StockService kit explosion

**Current control.** None yet

**Mitigation.** Mandatory nine-invariant non-regression gate

*Target phase* **2C** | *Owner* Finance and Inventory owners | *Trigger* Any Master Data migration script

**Acceptance evidence.** Nine invariants proven on a disposable SQL Server DB with RequiredEvidence trait

### RISK-024 - AI could answer from data the asker may not see

*Legacy id:* `R-24` | *Probability* Medium | *Impact* Critical | *Domains* AI Accounting Inventory

**Evidence.** 4 AiController endpoints authentication-only returning journal anomaly cashflow forecast inventory analysis; all in the 143 backlog

**Current control.** None

**Mitigation.** Wave 2 authorizes endpoints; Stage 9 requires trimming BEFORE retrieval never post-filtering

*Target phase* **Wave 2 then Stage 9** | *Owner* AI owner | *Trigger* Any AI feature ships

**Acceptance evidence.** Trimming test proving AI context excludes non-permitted objects

### RISK-026 - Test environment drift hides evidence

*Legacy id:* `R-26` | *Probability* Certain | *Impact* Critical | *Domains* All testing

**Evidence.** CROSSBUY_TEST_SQL unset so 46 tests reported skipped for two batches and had never executed; first run failed with 20 Invalid column name errors

**Current control.** F4 proofs now run; 763/763 with 0 skipped; Razor enabled

**Mitigation.** SQL Evidence CI guardrail: required tests fail if skipped; Razor in acceptance; leftover scratch DBs fail; four-state reporting

*Target phase* **2A** | *Owner* Platform owner | *Trigger* Any required-evidence test reports Skipped

**Acceptance evidence.** CI shows 0 skipped required tests 0 leftover scratch DBs 3 identical runs

### RISK-037 - PlatformRoleAssignments has production readers but NO production writer so HR and Projects resolve through bootstrap-open

*Legacy id:* `mitigated` | *Probability* Certain | *Impact* Critical | *Domains* HR Projects Identity Platform Security

**Evidence.** 4 production readers (PlatformRoleDirectory HrAccessService ProjectsAccessService EntityRegistry); writes exist ONLY in 4 test files; the table is empty and unfillable in production; Wave 2 plans to gate 52 High-risk HR and Identity endpoints on HrAccessService where those gates would evaluate bootstrap-open and allow broadly while tests pass because fixtures seed grants directly

**Current control.** payroll-manage and confidential-view are never bootstrap-open and project billing delegates to accounting - those three keep explicit closed behaviour. MITIGATED IN STAGE 2A BATCH A: IPlatformGrantWriter is the production writer (create revoke validity list get) with administration authorization a privilege ceiling resolved through the REAL access services idempotency transactional BusinessEvents and an additive audit schema. 17 acceptance tests pass on an isolated SQL Server probe and NONE seeds a grant row - every row is created by the writer. Proven end to end on HrActions.PayrollManage which is never bootstrap-open: denied then granted then denied. NOT YET CLOSED because no company has been cut over and the legacy writers are still live (RISK-038)

**Mitigation.** Production Grant Writer with administration authorization additive audit schema and SQL Server evidence. BLOCKS Wave 2 HR authorization rollout AND Security Administration Console activation AND Platform-source cutover until delivered

*Target phase* **2B before Wave 2** | *Owner* Security owner | *Trigger* Wave 2 HR gating or Security Console work begins

**Acceptance evidence.** A production grant path creates an assignment that HrAccessService honours plus a seeded-versus-unseeded guard proving bootstrap-open is not why a test passes

### RISK-040 - Financial modules cannot exclude dangerous bootstrap actions

*Legacy id:* `new` | *Probability* Certain | *Impact* Critical | *Domains* Accounting Inventory CRM Projects Platform

**Evidence.** AccountingAccessService InventoryAccessService and CrmAccessService evaluate a bootstrap decision BEFORE the action switch: if no module role is configured for the company return Allow. No per-action exclusion is possible. Exposed today: Accounting.post Accounting.pay Accounting.currency-override Inventory.doc Inventory.warehouse-access CRM.manage and delegated Projects.billing

**Current control.** Mechanism B modules (HR Tasks Communication) DO exclude their sensitive actions - that pattern exists and is the model

**Mitigation.** Move these modules to the action-aware bootstrap pattern; define Never Bootstrap Open actions; preserve behaviour through explicit LegacyCompatibility policies BEFORE hardening; add decision-source logging; require real SQL Server evidence; block behavioural rollout until the Grant Writer exists where relevant. BLOCKS financial bootstrap hardening AND Wave 2 authorization relying on these services AND production cutover for affected companies and scopes

*Target phase* **2B** | *Owner* Security owner | *Trigger* Any unconfigured company exists in production or a new company is provisioned

**Acceptance evidence.** Never Bootstrap Open actions deny with zero roles on a disposable SQL Server database plus behaviour-preserving policy seed proven unchanged

## High (25)

### RISK-004 - Two role systems answer the same authorization question

*Legacy id:* `R-04` | *Probability* High | *Impact* High | *Domains* Accounting Inventory CRM POS Platform

**Evidence.** Wave 1 fixtures had to seed AccountingUserRoles AND PlatformRoleAssignments for one decision

**Current control.** Both read at runtime

**Mitigation.** IMP-001 consolidation read-both write-new with parity tests

*Target phase* **2B** | *Owner* Security owner | *Trigger* Security Console work begins

**Acceptance evidence.** Parity tests identical decisions before and after

### RISK-005 - Bootstrap-open makes an unconfigured company fully permissive

*Legacy id:* `R-05` | *Probability* High | *Impact* High | *Domains* Accounting Inventory CRM Projects

**Evidence.** Three modules return true for every action when the role table is empty; a guard test asserts it

**Current control.** Documented plus guard test

**Mitigation.** IMP-002 explicit per-company flag audit and first-run warning; do not flip default silently

*Target phase* **2B** | *Owner* Security owner | *Trigger* A new tenant is provisioned

**Acceptance evidence.** Both flag states asserted per module

### RISK-006 - leave-manage is bootstrap-open so Encash Provision CarryOver stay open on an unconfigured company

*Legacy id:* `R-06` | *Probability* Medium | *Impact* High | *Domains* HR

**Evidence.** HrAccessService excludes payroll-manage and confidential-view from bootstrap-open but not leave-manage

**Current control.** payroll tier closed by default

**Mitigation.** Fold into IMP-002; consider excluding leave-manage

*Target phase* **2B** | *Owner* HR owner | *Trigger* HR go-live on a new company

**Acceptance evidence.** Test asserting leave-manage closed when configured

### RISK-007 - SQL Server rejects the EF model cascade graph

*Legacy id:* `R-07` | *Probability* Certain | *Impact* Medium | *Domains* Platform

**Evidence.** GenerateCreateScript fails on FK_Branches_CountriesLookup_CountryID multiple cascade paths

**Current control.** Production schema hand-written; F4 fixture strips FKs

**Mitigation.** IMP-003 measure ALL rejected constraints then decide per constraint; never a blanket cascade change

*Target phase* **2A** | *Owner* Platform owner | *Trigger* Any attempt to generate schema from the model

**Acceptance evidence.** Test enumerating every rejected constraint plus model vs sys.foreign_keys comparison

### RISK-008 - Hand-written schema can diverge from the EF model with no failing test

*Legacy id:* `R-08` | *Probability* Medium | *Impact* High | *Domains* Platform

**Evidence.** Idempotent SQL not migrations; unit tests run on SQLite which does not enforce cascade restrictions

**Current control.** None

**Mitigation.** Model-vs-database comparison test on a scratch DB

*Target phase* **2A** | *Owner* Platform owner | *Trigger* A column or FK added in SQL only

**Acceptance evidence.** Divergence report empty or explicitly accepted

### RISK-009 - Item.Barcode column coexists with the ItemBarcode table

*Legacy id:* `R-09` | *Probability* Certain | *Impact* Medium | *Domains* Master Data Inventory POS Sales

**Evidence.** Both present in Models/Context/Inventory/Inventory.cs

**Current control.** None - two sources of one fact

**Mitigation.** IMP-004 ItemBarcode authoritative with read precedence; column retained in Phase 0; structural divergence test

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* Barcode lookup returns different answers

**Acceptance evidence.** Parity test on barcode resolution across both sources

### RISK-012 - Uploaded files may be reachable without authorization

*Legacy id:* `R-12` | *Probability* Medium | *Impact* High | *Domains* Documents HR Projects Communication

**Evidence.** Three file stores FileManager HR documents project files; no unified authorization

**Current control.** Per-module checks vary

**Mitigation.** 2B unified attachments with authorization inherited from parent and an authorizing download endpoint; never a guessable static path

*Target phase* **2B** | *Owner* Platform owner | *Trigger* A file URL is shared outside the app

**Acceptance evidence.** Test proving a non-permitted user cannot download by URL

### RISK-015 - Org hierarchy has no CompanyID so cross-company grafts are possible

*Legacy id:* `R-15` | *Probability* Medium | *Impact* High | *Domains* HR CRM Projects Workflow

**Evidence.** Hierarchical carries no CompanyID; C.1 closed two callers via IOrgHierarchy

**Current control.** IOrgHierarchy intersects with company and logs dropped nodes

**Mitigation.** Stage 3 org redesign adds company ownership

*Target phase* **Stage 3** | *Owner* HR owner | *Trigger* A new hierarchy consumer is added

**Acceptance evidence.** New consumers use IOrgHierarchy; defect handling asserted

### RISK-016 - A hierarchy defect could silently auto-approve a workflow

*Legacy id:* `R-16` | *Probability* Low | *Impact* Critical | *Domains* HR Workflow

**Evidence.** CreateAsync auto-approves on an empty chain; ApproverChain.HierarchyDefect now refuses

**Current control.** HierarchyDefect contract

**Mitigation.** 2B approval engine must inherit the same contract

*Target phase* **2B** | *Owner* Workflow owner | *Trigger* Any new approval chain builder

**Acceptance evidence.** Test broken chain refuses and never auto-approves

### RISK-018 - Master Data terminology is ambiguous

*Legacy id:* `R-18` | *Probability* Certain | *Impact* High | *Domains* Master Data Inventory Manufacturing Sales POS

**Evidence.** Four overlapping type signals ItemType IsComposite CompositeType ProductionMethod

**Current control.** None

**Mitigation.** 2C canonical definitions plus the 15-area Product Workspace

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* A new composite type is needed

**Acceptance evidence.** Terminology test; workspace covers every legacy field

### RISK-025 - Search could leak existence through ranked results

*Legacy id:* `R-25` | *Probability* Medium | *Impact* High | *Domains* Search Platform

**Evidence.** No search exists yet

**Current control.** None

**Mitigation.** Trim by company AND AccessScope BEFORE ranking never post-filter

*Target phase* **2B-late** | *Owner* Platform owner | *Trigger* Search ships

**Acceptance evidence.** Trimming test at query level

### RISK-028 - Analyzer false positives train developers to suppress diagnostics

*Legacy id:* `R-28` | *Probability* Medium | *Impact* High | *Domains* Engineering

**Evidence.** 18 speculative analyzers proposed only 2 evidence-backed

**Current control.** Scope limited to 2 analyzers

**Mitigation.** Warning-first rollout; dogfood gate reproducing 157/88/143; generous ambiguity; pragma suppression banned

*Target phase* **2A** | *Owner* Platform owner | *Trigger* A false positive is reported

**Acceptance evidence.** Analyzer agrees with the verified scanner before enforcing

### RISK-031 - PlatformRoleAssignments and ProjectMembers absent from CrossBuyDB2

*Legacy id:* `R-31` | *Probability* Certain | *Impact* Medium | *Domains* Platform Projects

**Evidence.** Stage 1 closure; four Batch C proof endpoints gated on deployment

**Current control.** Idempotent deploy scripts exist

**Mitigation.** Operator applies scripts before the Security Console

*Target phase* **2B** | *Owner* Operator | *Trigger* Security Console or Projects membership work begins

**Acceptance evidence.** Tables present and proof endpoints exercised

### RISK-033 - Composite items are load-bearing in the stock writer and POS selling path

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* Inventory POS Manufacturing Sales Master Data

**Evidence.** ItemComponent read by StockService lines 319 and 1189 (kit explosion) PricingService PosOrderService x2 ManufService PosSetupService

**Current control.** None - the dependency was undocumented

**Mitigation.** Any composite redesign must preserve StockService kit explosion and POS component behaviour; covered by the nine-invariant gate

*Target phase* **2C** | *Owner* Inventory owner | *Trigger* Any change to ItemComponent shape

**Acceptance evidence.** Kit explosion and POS component tests unchanged

### RISK-036 - Shared SQL test fixture schema pollution made suite results order-dependent

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* All testing Platform

**Evidence.** Adding three IMP-003 tests broke three PlatformSchemaDeploymentTests; the prior green suite was order-dependent because EnsureEfSchemaAsync pollutes the shared fixture database from TaskScopeQuerySqlTests

**Current control.** IMP-003 comparison test no longer pollutes the shared fixture; suite green twice at 766 of 766

**Mitigation.** Isolated probe database per schema-generating test family; deterministic cleanup; randomized-order verification where practical; an acceptance test that fails if a shared fixture gains non-kernel tables; no skipped SQL evidence tests

*Target phase* **2A** | *Owner* Platform owner | *Trigger* Any new schema-generating test is added

**Acceptance evidence.** Suite green under randomized order plus a shared-fixture pollution guard test

### RISK-038 - A legacy role writer continuing after Platform cutover reports success while producing no effective permission

*Legacy id:* `new` | *Probability* High | *Impact* High | *Domains* Accounting Inventory CRM Security

**Evidence.** AccountingController AssignAccRole and RemoveAccRole; InventoryController AssignRole and RemoveRole; CrmController AssignCrmRole all write legacy tables which the authoritative reader ignores after cutover; 7 writers inventoried in the shutdown matrix

**Current control.** None yet - legacy writers are live

**Mitigation.** Writer shutdown matrix with per-mode behaviour; each writer redirects to the Platform Grant Writer or is explicitly disabled with a user-facing message; no silent success; rollback restores legacy behaviour; a test per writer route

*Target phase* **2B at cutover** | *Owner* Security owner | *Trigger* Any company reaches Platform cutover

**Acceptance evidence.** Post-cutover test proving a legacy route cannot report success plus a LegacyWriterAfterCutover divergence alarm

### RISK-039 - A role administration UI can let an administrator grant rights exceeding their own authority

*Legacy id:* `mitigated` | *Probability* Medium | *Impact* High | *Domains* Security Platform All modules

**Evidence.** Grant Writer design: without an explicit ceiling a module administrator could grant Platform scope or a cross-company role - the classic self-escalation failure of role-management screens

**Current control.** MITIGATED IN STAGE 2A BATCH A: the ceiling is implemented in PlatformGrantWriter.CheckCeilingAsync and resolves the grantor authority through the REAL module access services rather than from storage. Self-escalation is refused below platform authority a module administrator may not confer a role they do not themselves hold and cross-company administration requires platform security authority. Critically module-level authority is NOT admissible while the scope is bootstrap-open so the first grant in a company can only be made by a real administrator. Four dedicated tests pass. NOT CLOSED because no grant administration UI exists yet - the API surface is the only caller

**Mitigation.** Grantor authority ceiling; company boundary; branch boundary; module scope enforcement; Platform scope restricted to the Platform Security Administrator; effective-right comparison through the REAL access services; mandatory self-escalation and cross-company tests

*Target phase* **2B with the Grant Writer** | *Owner* Security owner | *Trigger* Any grant administration surface ships

**Acceptance evidence.** Test proving no administrator can grant beyond their own effective rights and that cross-company grants are refused

### RISK-041 - High-impact bootstrap decisions are not audited

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* Accounting Inventory CRM Platform

**Evidence.** Mechanism A returns Allow without the structured logging ModuleAccessServiceBase performs at Information (ModuleAccessServiceBase.cs:112). The highest-impact bootstrap decisions are therefore invisible in operational logs

**Current control.** Mechanism B logs every bootstrap decision; Mechanism A logs nothing

**Mitigation.** Additive permission result contract carrying an explicit decision source; BusinessEvent and audit integration; log grouping to avoid noise; Security Console exposure; tests proving the decision reason is visible

*Target phase* **2B** | *Owner* Platform owner | *Trigger* Any bootstrap-open decision is taken on a financial module

**Acceptance evidence.** Test asserting the decision reason is AllowedByBootstrapOpen and appears in audit

### RISK-042 - Platform operations inherit bootstrap-open accounting manage

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* Platform Accounting

**Evidence.** PlatformOpsAttribute authorizes on an Identity admin role OR accounting manage; on an unconfigured company accounting manage allows through Mechanism A so platform operational surfaces become reachable through financial bootstrap behaviour. The attribute documents this limitation in its own comment

**Current control.** None - the fallback is by design and documented but unmeasured until now

**Mitigation.** Classify PlatformOps as Never Bootstrap Open; separate platform-operations authority from accounting bootstrap; keep restricted payload and retry operations closed; add direct tests for unconfigured companies; show the exposure in the Security Console

*Target phase* **2B** | *Owner* Platform owner | *Trigger* Any unconfigured company reaches a platform operations surface

**Acceptance evidence.** Test proving platform operations deny on a company with zero accounting roles

### RISK-043 - Bootstrap policy cannot be safely hardened before Grant Writer availability

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* HR Projects Security

**Evidence.** HrAccessService and ProjectsAccessService read PlatformRoleAssignments but no production Grant Writer exists (RISK-037). Setting their bootstrap state to Disabled before the writer exists would lock legitimate administrators and users out

**Current control.** RISK-037 already blocks Wave 2; this records the reverse dependency

**Mitigation.** Grant Writer first; explicit behaviour-preserving policies second; hardening only after real grants can be created; company and scope rollout; rollback; first-run recovery

*Target phase* **2B after the Grant Writer** | *Owner* Security owner | *Trigger* Any attempt to set HR or Projects bootstrap state to Disabled

**Acceptance evidence.** Grant Writer creates a real grant that HrAccessService honours before any hardening step runs

### RISK-046 - A barcode migration could change the resolved sold unit and therefore quantity

*Legacy id:* `new` | *Probability* Medium | *Impact* Critical | *Domains* Master Data Inventory POS Retail Accounting

**Evidence.** ItemBarcode.UoMId SELECTS THE SOLD UNIT (Hypermarket assessment, PosOrderService scan path). Changing barcode-to-UoM resolution changes sale quantity stock quantity COGS invoice quantity and receipt quantity

**Current control.** Current resolution is correct; ItemBarcode(Barcode,UoMId) is already authoritative for per-unit barcodes

**Mitigation.** ItemBarcode authoritative with IsPrimary seeded from Item.Barcode; preserve UoMId semantics byte-for-byte; migration comparison per barcode; no silent remapping; ambiguity rejection retained; non-skippable POS and Hypermarket quantity tests

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* Any barcode consolidation or import step runs

**Acceptance evidence.** Per-barcode before-and-after comparison proving the resolved UoM and quantity are identical

### RISK-047 - Composite history cannot be reproduced because ItemComponent has no revision history

*Legacy id:* `new` | *Probability* High | *Impact* High | *Domains* Master Data Inventory POS Manufacturing Accounting

**Evidence.** ItemComponent is consumed by StockService:319 and :1189 PricingService:311 PosOrderService:302 and :1746 ManufService and PosSetupService with 20 write sites, but carries no revision or effective date. A later component change makes a historical explosion impossible to reproduce

**Current control.** None - rows are mutable in place

**Mitigation.** Additive revisions with effective dates; existing rows become revision 1; historical snapshots; NO automatic reclassification; tests across Stock POS Pricing and Manufacturing

*Target phase* **2C** | *Owner* Inventory owner | *Trigger* Any composite component is edited after a sale referencing it

**Acceptance evidence.** Historical explosion reproduced from a revision snapshot and matching the original movement

### RISK-048 - Introducing a factor-1 fallback for a missing unit conversion would silently corrupt quantities

*Legacy id:* `new` | *Probability* Medium | *Impact* Critical | *Domains* Master Data Inventory POS Retail Manufacturing

**Evidence.** The current system REJECTS a sale line when no UoMConversion exists and never assumes factor 1 (Hypermarket assessment; PosOrderService:547; StockService:236). A cleanup that added a default factor would silently alter every affected quantity

**Current control.** CORRECT TODAY - the rejection is the existing behaviour, so this risk guards a future regression rather than a present defect. Severity High not Critical for that reason: impact is Critical but probability requires a deliberate change

**Mitigation.** Preserve the rejection; no default factor anywhere; explicit validation surfaced in the Unit Conversion Designer; deterministic conversion tests; POS Hypermarket Stock and Manufacturing evidence

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* Any change to conversion lookup or a new conversion designer

**Acceptance evidence.** Test proving a missing conversion still rejects and no factor-1 path exists

### RISK-049 - Weighted item and scale configuration has no production administration surface

*Legacy id:* `new` | *Probability* Certain | *Impact* High | *Domains* Master Data Retail POS

**Evidence.** IsWeighted and ScaleCode determine fresh-food checkout quantity but are writable only through SQL or a DevOnly endpoint; Views/Inventory/ItemForm.cshtml exposes neither field (Hypermarket finding F4)

**Current control.** ScaleBarcodeParser and the engine are implemented and correct; only the administration surface is missing

**Mitigation.** Production administration surface with permission controls; ScaleCode filtered uniqueness preserved; validation preview and sample barcode decoder; audit and elevated approval; Hypermarket test evidence

*Target phase* **2C** | *Owner* Retail owner | *Trigger* Any new weighted item or scale code is required in production

**Acceptance evidence.** Admin screen writes both fields with uniqueness validation and Hypermarket calculations unchanged

### RISK-050 - Barcode consolidation could introduce first-match guessing where the system currently rejects

*Legacy id:* `new` | *Probability* Medium | *Impact* High | *Domains* Master Data Retail POS

**Evidence.** The system EXPLICITLY REJECTS an ambiguous barcode match and never guesses (Hypermarket assessment, multi-barcode gated by the BarcodeMulti capability). A consolidation or cleanup could accidentally select the first match

**Current control.** Explicit rejection is the current behaviour

**Mitigation.** Preserve explicit rejection; conflict diagnostics; NO first-match behaviour; a Barcode Center conflict-resolution workflow; migration divergence report; tests proving ambiguity remains denied

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* Any barcode consolidation or duplicate-resolution step runs

**Acceptance evidence.** Test proving an ambiguous barcode is still denied after consolidation

## Medium (14)

### RISK-010 - Item.ImagePath coexists with the ItemImage table

*Legacy id:* `R-10` | *Probability* Certain | *Impact* Low | *Domains* Master Data Commerce Store

**Evidence.** Both present in Inventory.cs

**Current control.** None

**Mitigation.** IMP-004 ItemImage authoritative with primary designation; column retained in Phase 0

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* Storefront shows a different image than the workspace

**Acceptance evidence.** Rendering parity test

### RISK-011 - Storefront presentation fields live on the Item master

*Legacy id:* `R-11` | *Probability* Certain | *Impact* Medium | *Domains* Master Data Commerce Store

**Evidence.** Item has StoreOldPrice StoreBadge StoreRating StoreVendor StoreHoverImage among 39 properties

**Current control.** None

**Mitigation.** IMP-004 separate Commerce Presentation per channel with a read-compatible view; no data movement in Phase 0

*Target phase* **2C-3** | *Owner* Master Data owner | *Trigger* A second sales channel is added

**Acceptance evidence.** Storefront rendering unchanged after separation

### RISK-013 - Raw SQL can bypass EF global query filters

*Legacy id:* `R-13` | *Probability* Low | *Impact* High | *Domains* Platform Inventory Accounting

**Evidence.** Stage1RawSqlSafetyTests exists as an enforced inventory

**Current control.** Enforced test over the raw-SQL inventory

**Mitigation.** Keep the test and extend to any new raw SQL

*Target phase* **ongoing** | *Owner* Platform owner | *Trigger* New raw SQL is added

**Acceptance evidence.** Raw SQL inventory test green

### RISK-017 - UI patterns duplicated across 327 views with 11 layouts and 0 ViewComponents

*Legacy id:* `R-17` | *Probability* Certain | *Impact* Medium | *Domains* All UI

**Evidence.** crossbuy-brand.css is 211 lines of Bootstrap overrides; _DocTimeline and _DocEventTimeline duplicate

**Current control.** None

**Mitigation.** Design tokens plus Tier 1 components in 2B; layout consolidation 11 to 3 as its own batch

*Target phase* **2B** | *Owner* UX owner | *Trigger* A third divergent copy of a component appears

**Acceptance evidence.** Tier 1 components extracted; visual parity on a sample

### RISK-019 - WMS bin foundations are OPERATIONAL and underused not absent

*Legacy id:* `R-19` | *Probability* Certain | *Impact* Medium | *Domains* Warehouse Inventory

**Evidence.** BinLocationId is a field on StockService movement DTOs (destination source opening); 186 refs; ProcurementService SellingService WarehouseService InventoryController; 5 views; 13 write ops

**Current control.** Bins already participate in stock movements

**Mitigation.** Stage 4 EXTEND AND REDESIGN over the existing operational foundation

*Target phase* **Stage 4** | *Owner* WMS owner | *Trigger* Warehouse volume grows

**Acceptance evidence.** Stage 4 extends BinLocation/BinStock without changing StockService semantics

### RISK-020 - Manufacturing routing foundations underused

*Legacy id:* `R-20` | *Probability* Certain | *Impact* Medium | *Domains* Manufacturing

**Evidence.** ManufRoutingOp ManufWorkCenter ManufPlan ManufPlanDemand exist; no scheduling or capacity levelling

**Current control.** None

**Mitigation.** Stage 5 major extension scheduling capacity execution costing UX

*Target phase* **Stage 5** | *Owner* Manufacturing owner | *Trigger* Production volume grows

**Acceptance evidence.** Stage 5 extends existing routing entities

### RISK-021 - Unit conversions are operational but unit sets do not exist

*Legacy id:* `R-21` | *Probability* Certain | *Impact* Medium | *Domains* Master Data Inventory Procurement Sales POS

**Evidence.** UoMConversion read by StockService line 236 PosOrderService line 547 ItemService IntegrityCheckService InventoryApiController

**Current control.** IntegrityCheckService already checks conversions

**Mitigation.** 2C Unit Sets plus a conversion designer with cycle prevention; preserve existing conversion reads

*Target phase* **2C** | *Owner* Master Data owner | *Trigger* A conversion cycle is created

**Acceptance evidence.** Cycle-prevention and rounding tests; existing conversion paths unchanged

### RISK-023 - Reporting is limited with no self-service builder

*Legacy id:* `R-23` | *Probability* Certain | *Impact* Medium | *Domains* Reporting

**Evidence.** Statements and dashboards only

**Current control.** None

**Mitigation.** Stage 9 after search events and trimming

*Target phase* **Stage 9** | *Owner* Reporting owner | *Trigger* Report requests exceed development capacity

**Acceptance evidence.** Report builder with permission trimming

### RISK-027 - Hand-maintained catalogs become stale and are then trusted

*Legacy id:* `R-27` | *Probability* High | *Impact* Medium | *Domains* Documentation Platform

**Evidence.** 224 DbSets 39 controllers 289 API actions too large to maintain by hand

**Current control.** None

**Mitigation.** Generate Domain and API catalogs FROM SOURCE; enrich manually but never author ownership facts by hand

*Target phase* **2D** | *Owner* Platform owner | *Trigger* A catalog disagrees with source

**Acceptance evidence.** Regeneration produces no diff against the committed catalog

### RISK-029 - Parallel team work is uncommitted so our base can shift invisibly

*Legacy id:* `R-29` | *Probability* High | *Impact* Medium | *Domains* All

**Evidence.** 66 untracked and 65 modified files; a Razor break appeared then was fixed by them

**Current control.** Selective commits; baseline re-verified each phase

**Mitigation.** Re-derive the baseline at the start of every phase; request a commit

*Target phase* **ongoing** | *Owner* Platform owner | *Trigger* A shared file changes under us

**Acceptance evidence.** Baseline re-verified from source each phase

### RISK-030 - Customer experience and portal capabilities are absent

*Legacy id:* `R-30` | *Probability* Certain | *Impact* Medium | *Domains* Customer Experience Portals

**Evidence.** No case management SLA or portal in source

**Current control.** None

**Mitigation.** Stage 7; external principals out of scope until a portal exists

*Target phase* **Stage 7** | *Owner* CX owner | *Trigger* External users are required

**Acceptance evidence.** BusinessContext extended for external principals with tests

### RISK-035 - Only 2 concurrency tokens across 231 entities so most write paths are last-write-wins

*Legacy id:* `new` | *Probability* Medium | *Impact* Medium | *Domains* Master Data HR CRM Projects Platform

**Evidence.** IMP-003 measurement: 2 concurrency tokens over 231 entities; the stock path is protected separately by explicit UPDLOCK and HOLDLOCK proven in Stage 1 F4

**Current control.** Stock and financial critical paths ARE protected by explicit SQL locking - this risk covers ORDINARY entity edits only and must not be read as the stock writer being unprotected

**Mitigation.** Add RowVersion concurrency tokens to entities with concurrent-edit exposure starting with Item and Customer; surface the Design System conflict state in the UI

*Target phase* **2C** | *Owner* Platform owner | *Trigger* Two users edit the same master record

**Acceptance evidence.** Concurrent-edit test showing the second writer is refused rather than silently overwriting

### RISK-044 - Bootstrap-open masks role-migration divergence

*Legacy id:* `new` | *Probability* High | *Impact* Medium | *Domains* Security Accounting Inventory CRM HR Projects

**Evidence.** In IMP-001 Shadow mode the legacy and platform decisions may both return Allow solely because bootstrap-open is active, which prevents proving that migrated role data produces the same authorization result

**Current control.** Shadow mode decides on legacy so it cannot broaden access - but it also cannot prove correctness

**Mitigation.** BootstrapOpenMaskedDifference divergence type; block cutover while present; display migration and bootstrap state together; require explicit role-based comparison evidence; trigger review when the first real grant is configured

*Target phase* **2B** | *Owner* Security owner | *Trigger* Any company enters Shadow mode while bootstrap-open is active

**Acceptance evidence.** Cutover blocked test plus a masked-divergence detection test

### RISK-045 - Implicit bootstrap compatibility may remain enabled indefinitely

*Legacy id:* `new` | *Probability* High | *Impact* Medium | *Domains* All modules Security

**Evidence.** A behaviour-preserving LegacyCompatibility state is required initially to avoid changing production behaviour, but without expiry review and administrative visibility it can become permanent

**Current control.** None yet - today the behaviour is implicit with no state at all

**Mitigation.** Explicit policy rows; review date; expiry where applicable; acknowledgement; persistent warning; environment hardening; company-by-company transition; audit and notifications

*Target phase* **2B** | *Owner* Security owner | *Trigger* A LegacyCompatibility policy passes its review date

**Acceptance evidence.** Review-date enforcement test plus a persistent console warning for unreviewed policies

## Low (3)

### RISK-014 - Sanctioned POS company constants remain

*Legacy id:* `R-14` | *Probability* Certain | *Impact* Low | *Domains* POS

**Evidence.** PosAccessService.CatalogCompanyId and PosSetupService.PosCompanyId are live constants

**Current control.** Documented permanent exception

**Mitigation.** Keep as a declared exception; must not be copied into other modules

*Target phase* **none** | *Owner* POS owner | *Trigger* The constant appears outside POS

**Acceptance evidence.** Structural test scoping the exception to POS

### RISK-032 - BusinessEvent retention is undefined

*Legacy id:* `R-32` | *Probability* Low | *Impact* Medium | *Domains* Platform

**Evidence.** BusinessEvents grows unbounded; no retention policy

**Current control.** None

**Mitigation.** Per-object retention in 2B contracts; archive never purge for accounting-owned objects

*Target phase* **2B-later** | *Owner* Platform owner | *Trigger* Event table growth affects performance

**Acceptance evidence.** Retention applied with archive-not-purge for ledger objects

### RISK-034 - Variants-as-separate-items population is unmeasured so 2C sizing is unknown

*Legacy id:* `new` | *Probability* Medium | *Impact* Medium | *Domains* Master Data

**Evidence.** No variant model exists; conversion risk cannot be sized without data

**Current control.** None

**Mitigation.** Requires an APPROVED read-only assessment; blocked until then

*Target phase* **2C planning** | *Owner* Master Data owner | *Trigger* 2C scoping begins

**Acceptance evidence.** Approved read-only measurement of candidate variant groups

---

## Blocking risks

**RISK-037 (Critical) explicitly blocks** Wave 2 HR authorization rollout, Security Administration Console
activation, and Platform-source cutover. None may proceed before a production Grant Writer exists.

**RISK-022 (Critical)** gates any Master Data migration behind the nine-invariant stock-valuation evidence.

**RISK-024 (Critical)** requires permission trimming BEFORE AI retrieval, never post-filtering.
