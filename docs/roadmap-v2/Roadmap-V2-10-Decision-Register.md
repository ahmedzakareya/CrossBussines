# CrossBusiness Platform — Roadmap v2 — 10 Decision Register

**37 unresolved business decisions, 0 open.**

Machine-readable: `roadmap-v2-decisions.csv`.

---

## 1. Rule

**No decision here is silently chosen.** Each carries a recommendation because a recommendation
is useful, but the recommendation is not the decision and no work proceeds on it. Where a
decision blocks work, the blocked phase is named.

## 2. Register

| DecisionId | Area | Subject | Recommendation | BlockingWork | DueGate | Status |
|---|---|---|---|---|---|---|
| D-01 | Security | Accounting.read data breadth | B - require a branch/company-scoped policy rather than global unfiltered read. A compatibility read should never be broader than the role it replaced. | R6, R10 | R6 entry | OPEN |
| D-02 | Security | Inventory.read warehouse and cost breadth | B plus D - warehouse/branch-scoped visibility AND a separate stock-cost permission. Cost is the sensitive field; quantity usually is not. | R6, R9 | R6 entry | OPEN |
| D-03 | Security | CRM customer-data breadth | D, defaulting to A - require the policy to state assigned, team or company explicitly. The current behaviour is not a policy; it is the absence of one. | R6, R11 | R6 entry | OPEN |
| D-04 | Security | CRM unrestricted visible-owner scope | D then A - an unconfigured or error state must NEVER map silently to unrestricted null. Fail closed, then widen deliberately. | R6, R11 | R6 entry | OPEN |
| D-05 | Communication | Privacy ceiling | B - each entity registration DECLARES its maximum visibility. Employee: maximum Internal. Journal Entry: maximum Internal. Invoice: maximum Internal unless an external portal policy exists. Support Ticket: Internal or CustomerVisible by comment type. Public: only for explicitly approved external entities. | R2 | R2 entry | APPROVED - architecture direction |
| D-06 | Communication | Retention policy | B - each entity registration declares retention duration, archive behaviour, legal-hold behaviour, deletion/anonymization behaviour and attachment retention. | R2 | R2 entry | APPROVED - architecture direction |
| D-07 | Communication | Per-entity audit policy | C - each entity registration declares edit-history, deletion-history, read-receipt, attachment-audit and immutable-history requirements. | R2 | R2 entry | APPROVED - architecture direction |
| D-09 | Reporting | PDF runtime owner | B - a sidecar keeps a browser runtime out of the web host. | R4+ | Post-R4 | OPEN |
| D-10 | Reporting | Email delivery integration | A - one delivery path for the product, not two. | R4+ | Post-R4 | OPEN |
| D-11 | Reporting | Hosted scheduler ownership | A - a distinct worker with its own scope, matching the platform rule that a hosted service takes IServiceScopeFactory and scopes per run. | R4+ | Post-R4 | OPEN |
| D-12 | Communication | Unified timeline ownership | B for rendering, with business events as the canonical source of fact - Communication owns unified timeline rendering and notification generation contracts. Modules PUBLISH events; they do not build their own general timelines. | R5 | Decided | APPROVED |
| D-13 | Communication | Support external-principal model | C - a customer must NOT be treated as an employee BusinessContext. Requires a future ExternalPrincipalContext ADR before any implementation. | R11 | R11 entry | APPROVED - architecture direction, ADR required |
| D-14 | Communication | Customer-visible comments | B - two explicit comment types: InternalNote and CustomerVisibleReply. Explicit and auditable per comment. | R11 | R11 entry | APPROVED - architecture direction |
| D-15 | Communication | @Role reverse membership resolution | A - the platform role directory already exists and is the natural owner. | R2 | R2 entry | BLOCKED - pending implementation |
| D-16 | Reporting | SQL vocabulary CHECK constraints | B - a second copy of a vocabulary drifts, and it drifts in the dangerous direction. This is why the 15 Never-bootstrap-open actions were deliberately NOT duplicated as a CHECK constraint. | R4 | R4 entry | OPEN |
| D-17 | Reporting | Report Studio sequence | A - consumption before authoring. R4 delivers the Center; Studio follows. | R4 | R4 entry | OPEN |
| D-19 | Construction | DDL rollout environment | A - an approved non-production environment. The measurement must not run against shifting data. | R2, R7 | R2 entry | OPEN |
| D-20 | Construction | M3 and M9 measurement result | Run the script in the D-19 environment. Stop automatically if M3 > 0, per the existing rule. | R7 | R7 entry | OPEN |
| D-21 | Construction | D-08 default mode per company | B - ApprovalRequired for unconfigured companies: safer than Warning, less operationally disruptive than HardBlock, and it requires explicit authority for overspend. | R7 | R7 entry | OPEN - recommendation recorded |
| D-23 | Security | Inventory.purchase action split | B eventually - the single code cannot express three impacts. Kept closed whole for now, with the VOCABULARY DEFECT reason pinned by test so the decision cannot be reversed silently. | R6, R9 | R6 entry | OPEN - closed whole for now |
| D-24 | Construction | Satellite-table consolidation timing | A - consolidating after features are built on the current shape is far more expensive. | R7 | R7 entry | OPEN |
| D-25 | Construction | C6 approval and C7 variation reroute | A - as already assessed. | R7 | R7 entry | OPEN |
| D-26 | Construction | Legacy concurrency strategy | A - the C1 work already proved the pattern. | R7 | R7 entry | OPEN |
| D-27 | Platform | AI governance | A - AI reads the reporting dataset layer, never raw tables. | R12 | R12 entry | OPEN |
| D-28 | Platform | Search ownership | A - one index that honours per-module authorization AT QUERY TIME, not by filtering after retrieval. | R12 | R12 entry | OPEN |
| D-29 | Platform | Client portal isolation | B - an external user is not an employee with fewer rights. Aligns with D-13. | R11 | R11 entry | OPEN |
| D-30 | Platform | Mobile and offline policy | B - matches the POS sync precedent without introducing offline selling. | R14 | R14 entry | OPEN |
| D-31 | Platform | Master Data ownership | C - those four entities have genuinely different natural owners; a single MDM owner would be a fiction. | R6 | R6 entry | OPEN |
| D-32 | Platform | Task Management ownership and boundary | B - Tasks owns task entity, assignment, status, priority, due dates, dependencies, checklists, subtasks, escalation, time tracking and task-specific rules. Communication owns comments, mentions, attachments, reactions, followers, watchers, unified timeline rendering and notification generation contracts. Tasks PUBLISHES events to the shared timeline and must NOT maintain a second independent general timeline. | R5 | Decided | APPROVED |
| D-33 | Platform | Calendar ownership and boundary | B - Calendar Platform owns calendar events, recurrence, attendees, meeting metadata, availability, reminders, resource booking, schedule views and external calendar integration contracts. Tasks owns task due dates and task scheduling rules. Projects and Construction own their domain schedules, milestones and activities. Communication owns event comments, mentions and timeline entries. Calendar may DISPLAY tasks but is not their source of truth. | R5 | Decided | APPROVED |
| D-35 | Reporting | First production reporting dataset | D - the Business Events / Platform Operations dataset, READ-ONLY. Lower commercial sensitivity than Accounting, Inventory or CRM; proves the full pipeline (dataset layer, filters, parameters, catalog, HTML, CSV, Excel, history, archive, permissions, row caps) without exposing ledger balances, stock costs or customer data; delivers operational value immediately. | R4 | Decided | APPROVED |
| D-36 | Platform | Canonical platform visual identity | B - CrossBusiness Blue is the canonical platform identity. Platform shell, Workspace, Report Studio, administration consoles, Construction workspace and Communication UI all use blue. Green and gold remain permitted INSIDE financial charts, accounting reports, profit and loss indicators and domain-specific data visualizations - but are not the default shell identity. | R3 | Decided | APPROVED |
| D-37 | Platform | BRAND-IMPLEMENTATION - legacy CSS migration | B - apply CrossBusiness Blue to all NEW screens first, then migrate legacy pages through an approved visual rollout. A global override flip would restyle 327 views at once with no design review. | R3+ | R3 entry | OPEN |
| D-38 | Deployment | Canonical SQL deployment root | B - CrossBuy/deploy/sql is the canonical AUTHORED-slice root, because the governance tooling (scan-sql-manifest.ps1, report-schema-history.ps1) and manifest.json already live beside it. deploy/ is retained and RE-LABELLED as the deployment PACKAGE root: generated artifacts (fresh/), operational scripts (backup, purge) and the server README - not an authoring root. | R1 | R1 entry | APPROVED - migration deferred to R1 |
| D-39 | Deployment | Divergent same-name slice reconciliation | C then D - each pair is diffed and read by its owner, then superseded by ONE reconciled slice with a new SliceId. Choosing by mtime or by location would be guessing about schema. | R1 | R1 entry | APPROVED - direction |
| D-40 | Deployment | PlatformSchemaHistory canonical design | B - an applied record in each database: SliceId, filename, SHA-256, module, owner, dependencies, applied date, environment, result. The manifest is the authored registry; PlatformSchemaHistory is the applied registry. Neither replaces the other. | R1 | R1 entry | APPROVED - direction |
| D-41 | Product | CrossBusiness Workspace as the first integrated product | B - CrossBusiness Workspace is the first integrated user-facing product. It is NOT a new business module: it owns no Accounting, CRM, Task or Calendar data, orchestrates access through approved service contracts, must never bypass module permissions, uses CrossBusiness Blue, and requires an approved Metronic reference before UI implementation. | R3 | Decided | APPROVED |

## 3. Full detail

### D-01 — Accounting.read data breadth (Security)

**Evidence.** AFTER B6, Accounting.read returns ledger balances with NO branch filter. Exposed: full company trial balance, account values, customer and vendor statements. Affected: every employee of a company with no configured roles, plus every holder of any accounting role. Dimensions available but unused: Company (enforced), Branch (exists, not applied to read), Project (exists via dimensions), Cost centre.

**Options.** A) keep company-wide unfiltered; B) branch/company-scoped policy; C) role-tiered breadth (summary vs detail); D) separate permission for account values

**Recommendation.** B - require a branch/company-scoped policy rather than global unfiltered read. A compatibility read should never be broader than the role it replaced.

**Impact.** Changes what an unconfigured company sees and what a low-tier accounting role sees. Compatibility impact: existing role holders may lose cross-branch visibility. Migration: seed a branch-scoped policy per company. Tests: extend the 14 Accounting focused tests with branch dimensions. Screens: 66 Accounting views + 4 Currency views. · **Blocks:** R6, R10 · **Due:** R6 entry · **Status:** OPEN

### D-02 — Inventory.read warehouse and cost breadth (Security)

**Evidence.** Inventory.read exposes stock quantities AND unit costs with no branch or warehouse filter, even though B6 site 3 now enforces warehouse scope exactly for CanUseWarehouseAsync. Read and warehouse-scope therefore disagree. Affected: every employee of an unconfigured company; every inventory role holder. Dimensions: Company (enforced), Branch (exists), Warehouse (enforced for use, not for read).

**Options.** A) keep; B) warehouse/branch-scoped visibility; C) hide cost, show quantity; D) separate stock-cost permission

**Recommendation.** B plus D - warehouse/branch-scoped visibility AND a separate stock-cost permission. Cost is the sensitive field; quantity usually is not.

**Impact.** Read alignment with the enforced warehouse scope. Compatibility: buyers and clerks may lose cost visibility. Migration: seed scoped policy; introduce a cost permission code. Tests: extend the 8 Inventory focused tests. Screens: 78 Inventory views. · **Blocks:** R6, R9 · **Due:** R6 entry · **Status:** OPEN

### D-03 — CRM customer-data breadth (Security)

**Evidence.** CrmAccessService.cs:59 returns TRUE for EVERY CRM action on a company with no configured roles - read, edit, and every other action. Exposed: all accounts, contacts, leads, opportunities, custom field values and bilingual fields. Affected: every authenticated employee of an unconfigured company. Dimensions: Company (enforced), Owner (see D-04), Team (OrgHierarchy exists), Branch.

**Options.** A) mirror Accounting - read-only compatibility, all other actions Never; B) deny all; C) keep open; D) assigned/team/company policy explicitly selected

**Recommendation.** D, defaulting to A - require the policy to state assigned, team or company explicitly. The current behaviour is not a policy; it is the absence of one.

**Impact.** Unblocks CRM B6 site 1. Compatibility: unconfigured companies lose edit/delete. Migration: seed a CRM read policy per company. Tests: a CRM focused suite equivalent to the 14 Accounting tests. Screens: 32 Crm views. · **Blocks:** R6, R11 · **Due:** R6 entry · **Status:** OPEN

### D-04 — CRM unrestricted visible-owner scope (Security)

**Evidence.** CrmAccessService.cs:98 returns NULL, and null means unrestricted - the widest possible answer, returned precisely when the system knows least. An unconfigured or misconfigured state currently maps SILENTLY to full visibility. Affected: every CRM list and search. Dimensions: Owner, Team via OrgHierarchy, Branch, Company.

**Options.** A) own records only; B) team scope via OrgHierarchy; C) keep unrestricted; D) distinguish ConfigurationError from Unconfigured and fail closed on both

**Recommendation.** D then A - an unconfigured or error state must NEVER map silently to unrestricted null. Fail closed, then widen deliberately.

**Impact.** Unblocks CRM B6 site 2. Compatibility: users may lose visibility of records they do not own. Migration: an explicit scope decision per company. Tests: scope-exactness tests mirroring the 10 warehouse tests. Screens: all 32 Crm views. · **Blocks:** R6, R11 · **Due:** R6 entry · **Status:** OPEN

### D-05 — Privacy ceiling (Communication)

**Evidence.** No maximum visibility is defined for comm content on any entity.

**Options.** A) global internal-only ceiling; B) per-entity declared ceiling; C) per-thread choice

**Recommendation.** B - each entity registration DECLARES its maximum visibility. Employee: maximum Internal. Journal Entry: maximum Internal. Invoice: maximum Internal unless an external portal policy exists. Support Ticket: Internal or CustomerVisible by comment type. Public: only for explicitly approved external entities.

**Impact.** Gates Communication activation. Approved as architecture direction; production activation still requires R2. · **Blocks:** R2 · **Due:** R2 entry · **Status:** APPROVED - architecture direction

### D-06 — Retention policy (Communication)

**Evidence.** No retention is defined for threads, notifications, attachments or audit.

**Options.** A) indefinite; B) per-entity declared retention; C) one global period

**Recommendation.** B - each entity registration declares retention duration, archive behaviour, legal-hold behaviour, deletion/anonymization behaviour and attachment retention.

**Impact.** Gates activation and audit. Approved as architecture direction. · **Blocks:** R2 · **Due:** R2 entry · **Status:** APPROVED - architecture direction

### D-07 — Per-entity audit policy (Communication)

**Evidence.** CommAuditWriter exists but per-entity policy is undefined.

**Options.** A) audit everything; B) audit sensitive entities; C) per-entity declaration

**Recommendation.** C - each entity registration declares edit-history, deletion-history, read-receipt, attachment-audit and immutable-history requirements.

**Impact.** Gates the DocComments migration. Approved as architecture direction. · **Blocks:** R2 · **Due:** R2 entry · **Status:** APPROVED - architecture direction

### D-09 — PDF runtime owner (Reporting)

**Evidence.** PlaywrightPdfReportRenderer exists in code; no runtime is provisioned.

**Options.** A) Playwright in-host; B) sidecar service; C) third-party service

**Recommendation.** B - a sidecar keeps a browser runtime out of the web host.

**Impact.** Gates PDF export. Not required for the R4 pilot, which ships HTML, CSV and Excel. · **Blocks:** R4+ · **Due:** Post-R4 · **Status:** OPEN

### D-10 — Email delivery integration (Reporting)

**Evidence.** ReportDelivery has no transport.

**Options.** A) reuse Communication channels; B) direct SMTP; C) provider API

**Recommendation.** A - one delivery path for the product, not two.

**Impact.** Gates scheduled delivery. Depends on Communication activation. · **Blocks:** R4+ · **Due:** Post-R4 · **Status:** OPEN

### D-11 — Hosted scheduler ownership (Reporting)

**Evidence.** ReportScheduleService has no hosted worker.

**Options.** A) a new dedicated hosted worker; B) extend the dispatch worker; C) external scheduler

**Recommendation.** A - a distinct worker with its own scope, matching the platform rule that a hosted service takes IServiceScopeFactory and scopes per run.

**Impact.** Gates scheduling. Not required for the R4 pilot. · **Blocks:** R4+ · **Due:** Post-R4 · **Status:** OPEN

### D-12 — Unified timeline ownership (Communication)

**Evidence.** Both the platform timeline projection and the Comm aggregator can render activity; Tasks could implement a third.

**Options.** A) platform timeline canonical; B) Communication aggregator canonical; C) both with one canonical source

**Recommendation.** B for rendering, with business events as the canonical source of fact - Communication owns unified timeline rendering and notification generation contracts. Modules PUBLISH events; they do not build their own general timelines.

**Impact.** Resolved together with D-32. Tasks must not maintain a second independent general timeline. · **Blocks:** R5 · **Due:** Decided · **Status:** APPROVED

### D-13 — Support external-principal model (Communication)

**Evidence.** No model exists for a customer participating in a thread; a customer is currently representable only as an employee BusinessContext, which would be wrong.

**Options.** A) no external participation; B) mirrored portal identity; C) a distinct ExternalPrincipalContext

**Recommendation.** C - a customer must NOT be treated as an employee BusinessContext. Requires a future ExternalPrincipalContext ADR before any implementation.

**Impact.** Support must NOT be activated until this exists. Approved as architecture direction only. · **Blocks:** R11 · **Due:** R11 entry · **Status:** APPROVED - architecture direction, ADR required

### D-14 — Customer-visible comments (Communication)

**Evidence.** No distinction exists between internal notes and customer-facing replies.

**Options.** A) never customer-visible; B) explicit per comment; C) per thread

**Recommendation.** B - two explicit comment types: InternalNote and CustomerVisibleReply. Explicit and auditable per comment.

**Impact.** Gates portal support. Approved as architecture direction. · **Blocks:** R11 · **Due:** R11 entry · **Status:** APPROVED - architecture direction

### D-15 — @Role reverse membership resolution (Communication)

**Evidence.** Mention by role requires reverse role-to-user resolution, which no component owns.

**Options.** A) platform role directory; B) per-module; C) cached projection

**Recommendation.** A - the platform role directory already exists and is the natural owner.

**Impact.** @Role mentions remain BLOCKED until reverse role-membership lookup exists. · **Blocks:** R2 · **Due:** R2 entry · **Status:** BLOCKED - pending implementation

### D-16 — SQL vocabulary CHECK constraints (Reporting)

**Evidence.** Reporting vocabulary is not constrained in SQL.

**Options.** A) add CHECK constraints; B) enforce in code only; C) both

**Recommendation.** B - a second copy of a vocabulary drifts, and it drifts in the dangerous direction. This is why the 15 Never-bootstrap-open actions were deliberately NOT duplicated as a CHECK constraint.

**Impact.** Schema hardening. · **Blocks:** R4 · **Due:** R4 entry · **Status:** OPEN

### D-17 — Report Studio sequence (Reporting)

**Evidence.** Whether authoring precedes or follows consumption.

**Options.** A) Report Center first; B) Studio first; C) together

**Recommendation.** A - consumption before authoring. R4 delivers the Center; Studio follows.

**Impact.** Sequencing only. · **Blocks:** R4 · **Due:** R4 entry · **Status:** OPEN

### D-19 — DDL rollout environment (Construction)

**Evidence.** construction_c1 DDL (8 tables) is authored and applied nowhere, while the C1 services ARE registered in Program.cs.

**Options.** A) a dedicated non-production environment; B) shared dev; C) a production window

**Recommendation.** A - an approved non-production environment. The measurement must not run against shifting data.

**Impact.** Gates R2 construction activation and all of R7. No SQL executed in this increment. · **Blocks:** R2, R7 · **Due:** R2 entry · **Status:** OPEN

### D-20 — M3 and M9 measurement result (Construction)

**Evidence.** The measurement script has not been run.

**Options.** Measure, then decide

**Recommendation.** Run the script in the D-19 environment. Stop automatically if M3 > 0, per the existing rule.

**Impact.** Gates C2 scope definition. · **Blocks:** R7 · **Due:** R7 entry · **Status:** OPEN

### D-21 — D-08 default mode per company (Construction)

**Evidence.** The default commercial policy mode per company is unconfirmed. Modes: Warning, ApprovalRequired, HardBlock.

**Options.** A) Warning; B) ApprovalRequired; C) HardBlock; D) per-company explicit with no default

**Recommendation.** B - ApprovalRequired for unconfigured companies: safer than Warning, less operationally disruptive than HardBlock, and it requires explicit authority for overspend.

**Impact.** RECOMMENDATION ONLY - not approved. A silent default is how bootstrap-open happened. · **Blocks:** R7 · **Due:** R7 entry · **Status:** OPEN - recommendation recorded

### D-23 — Inventory.purchase action split (Security)

**Evidence.** One action gates a PO draft, a goods receipt (creates stock) and a PO-to-invoice conversion (accounting effect) - three impact classes, one permission code.

**Options.** A) keep closed whole; B) split into three codes; C) split into two

**Recommendation.** B eventually - the single code cannot express three impacts. Kept closed whole for now, with the VOCABULARY DEFECT reason pinned by test so the decision cannot be reversed silently.

**Impact.** Permission vocabulary. · **Blocks:** R6, R9 · **Due:** R6 entry · **Status:** OPEN - closed whole for now

### D-24 — Satellite-table consolidation timing (Construction)

**Evidence.** When to consolidate construction satellite tables.

**Options.** A) before C2; B) during C4; C) never

**Recommendation.** A - consolidating after features are built on the current shape is far more expensive.

**Impact.** Schema shape. · **Blocks:** R7 · **Due:** R7 entry · **Status:** OPEN

### D-25 — C6 approval and C7 variation reroute (Construction)

**Evidence.** Header-only subcontract approval enforcement and the live VariationOrderService approval reroute are deferred.

**Options.** A) as assessed - C6 and C7; B) earlier; C) later

**Recommendation.** A - as already assessed.

**Impact.** Sequencing. · **Blocks:** R7 · **Due:** R7 entry · **Status:** OPEN

### D-26 — Legacy concurrency strategy (Construction)

**Evidence.** Five legacy base tables lack concurrency control.

**Options.** A) rowversion on all five; B) optimistic checks in services; C) accept the risk

**Recommendation.** A - the C1 work already proved the pattern.

**Impact.** Data integrity under concurrent commercial edits. · **Blocks:** R7 · **Due:** R7 entry · **Status:** OPEN

### D-27 — AI governance (Platform)

**Evidence.** AiService and AiInsightsService exist with NO AI-specific authorization, behind a context that still has 143 unprotected actions.

**Options.** A) governed dataset-only access; B) full read; C) disable until governed

**Recommendation.** A - AI reads the reporting dataset layer, never raw tables.

**Impact.** Gates all AI work. · **Blocks:** R12 · **Due:** R12 entry · **Status:** OPEN

### D-28 — Search ownership (Platform)

**Evidence.** No search implementation exists; zero source files match.

**Options.** A) platform-owned index; B) per-module search; C) external engine

**Recommendation.** A - one index that honours per-module authorization AT QUERY TIME, not by filtering after retrieval.

**Impact.** Gates search. · **Blocks:** R12 · **Due:** R12 entry · **Status:** OPEN

### D-29 — Client portal isolation (Platform)

**Evidence.** The portal currently uses the constrained public catalog scope only.

**Options.** A) extend the public scope; B) a dedicated ExternalPrincipalContext; C) a separate application

**Recommendation.** B - an external user is not an employee with fewer rights. Aligns with D-13.

**Impact.** Gates portals. · **Blocks:** R11 · **Due:** R11 entry · **Status:** OPEN

### D-30 — Mobile and offline policy (Platform)

**Evidence.** No agreed offline capability or conflict policy beyond POS sync.

**Options.** A) read-only offline; B) capture offline, sync online; C) full offline

**Recommendation.** B - matches the POS sync precedent without introducing offline selling.

**Impact.** Gates mobile. · **Blocks:** R14 · **Due:** R14 entry · **Status:** OPEN

### D-31 — Master Data ownership (Platform)

**Evidence.** No owner for master entities shared across modules: Employee, Customer, Item, Supplier.

**Options.** A) a platform-owned MDM; B) the originating module owns; C) per-entity ownership with a register

**Recommendation.** C - those four entities have genuinely different natural owners; a single MDM owner would be a fiction.

**Impact.** Gates Master Data work. · **Blocks:** R6 · **Due:** R6 entry · **Status:** OPEN

### D-32 — Task Management ownership and boundary (Platform)

**Evidence.** Tasks already has a controller, 5 views, an access service, 7 SQL slices and 2 registered hosted services - it is not greenfield.

**Options.** A) Tasks owns everything including its own timeline; B) split - Tasks owns state and rules, Communication owns collaboration; C) Communication absorbs Tasks

**Recommendation.** B - Tasks owns task entity, assignment, status, priority, due dates, dependencies, checklists, subtasks, escalation, time tracking and task-specific rules. Communication owns comments, mentions, attachments, reactions, followers, watchers, unified timeline rendering and notification generation contracts. Tasks PUBLISHES events to the shared timeline and must NOT maintain a second independent general timeline.

**Impact.** Prevents a second timeline implementation. Requires assessment of legacy task activity data before any migration. Work is integration and remediation, NOT greenfield task creation. · **Blocks:** R5 · **Due:** Decided · **Status:** APPROVED

### D-33 — Calendar ownership and boundary (Platform)

**Evidence.** Calendar already has a service, controller and SQL slice - it is not greenfield.

**Options.** A) Calendar owns all scheduling everywhere; B) Calendar owns calendar events and unified presentation only; C) each module owns its own calendar

**Recommendation.** B - Calendar Platform owns calendar events, recurrence, attendees, meeting metadata, availability, reminders, resource booking, schedule views and external calendar integration contracts. Tasks owns task due dates and task scheduling rules. Projects and Construction own their domain schedules, milestones and activities. Communication owns event comments, mentions and timeline entries. Calendar may DISPLAY tasks but is not their source of truth.

**Impact.** Calendar provides unified presentation and coordination, not ownership of all scheduling data. Requires assessment of the existing service, controller and schema before modification. Do not rebuild from zero. · **Blocks:** R5 · **Due:** Decided · **Status:** APPROVED

### D-35 — First production reporting dataset (Reporting)

**Evidence.** No production module is bound as a live dataset. Business Events infrastructure and the Business Event Monitor already exist and are verified.

**Options.** A) financial statements; B) stock valuation; C) sales analysis; D) Business Events / Platform Operations

**Recommendation.** D - the Business Events / Platform Operations dataset, READ-ONLY. Lower commercial sensitivity than Accounting, Inventory or CRM; proves the full pipeline (dataset layer, filters, parameters, catalog, HTML, CSV, Excel, history, archive, permissions, row caps) without exposing ledger balances, stock costs or customer data; delivers operational value immediately.

**Impact.** Pilot fields: EventId, EventType, EventVersion, EntityType, EntityId, CompanyId, BranchId where available, OccurredAt, ActorId, CorrelationId, consumer states, failure state, retry count, dispatch timing, non-sensitive operational metadata. Full sensitive payload is NOT exposed by default. · **Blocks:** R4 · **Due:** Decided · **Status:** APPROVED

### D-36 — Canonical platform visual identity (Platform)

**Evidence.** crossbuy-brand.css currently overrides Metronic blue with ledger green and gold; CLAUDE.md records the contradiction as open.

**Options.** A) green and gold; B) CrossBusiness Blue; C) selectable themes

**Recommendation.** B - CrossBusiness Blue is the canonical platform identity. Platform shell, Workspace, Report Studio, administration consoles, Construction workspace and Communication UI all use blue. Green and gold remain permitted INSIDE financial charts, accounting reports, profit and loss indicators and domain-specific data visualizations - but are not the default shell identity.

**Impact.** All new screens use blue. Existing production UI and crossbuy-brand.css are NOT modified in this increment. Implementation path is D-37. · **Blocks:** R3 · **Due:** Decided · **Status:** APPROVED

### D-37 — BRAND-IMPLEMENTATION - legacy CSS migration (Platform)

**Evidence.** crossbuy-brand.css globally overrides Metronic blue with green and gold; 327 existing views render under it. D-36 makes blue canonical but changes no CSS.

**Options.** A) replace the current global green override now; B) apply blue to new screens first, then migrate legacy pages through an approved visual rollout; C) support selectable themes; D) retain legacy pages until individually redesigned

**Recommendation.** B - apply CrossBusiness Blue to all NEW screens first, then migrate legacy pages through an approved visual rollout. A global override flip would restyle 327 views at once with no design review.

**Impact.** Do NOT modify crossbuy-brand.css, existing production UI or CLAUDE.md in this increment. Specialized operational screens (POS, KDS, hyper lane, mobile attendance, storefront) keep their domain UX. · **Blocks:** R3+ · **Due:** R3 entry · **Status:** OPEN

### D-38 — Canonical SQL deployment root (Deployment)

**Evidence.** Two trees: deploy/sql (57 slices, plus the generated fresh/ build and the operational purge and backup scripts) and CrossBuy/deploy/sql (60 slices, plus manifest.json). The manifest GENERATOR and the schema-history reporter both live at CrossBuy/deploy/. deploy/README.md states the authoritative provisioning method is BACKUP/RESTORE of CrossBuyDB2, not script replay, because EF migrations are broken and ~90 ad-hoc scripts in C:/temp built the schema.

**Options.** A) deploy/sql canonical; B) CrossBuy/deploy/sql canonical; C) keep both with a manifest; D) a new third root

**Recommendation.** B - CrossBuy/deploy/sql is the canonical AUTHORED-slice root, because the governance tooling (scan-sql-manifest.ps1, report-schema-history.ps1) and manifest.json already live beside it. deploy/ is retained and RE-LABELLED as the deployment PACKAGE root: generated artifacts (fresh/), operational scripts (backup, purge) and the server README - not an authoring root.

**Impact.** No file is moved or deleted in this increment. Migration of the 55 root-only module slices into the canonical root is an R1 action, executed slice by slice with hash verification. · **Blocks:** R1 · **Due:** R1 entry · **Status:** APPROVED - migration deferred to R1

### D-39 — Divergent same-name slice reconciliation (Deployment)

**Evidence.** manifest.json already records 4 duplicateNames with identical=false and distinctHashes=2: pos_hold_recall, pos_order_guests, pos_quickmenu, pos_setup. It also flags 1 script as review (hm16_rename_grni.sql - unguarded, unread, DO NOT DEPLOY) and 1 as not-deployable (script.sql).

**Options.** A) keep the newer by mtime; B) keep the canonical-root copy; C) diff and merge each pair deliberately; D) supersede both with a new reconciled slice

**Recommendation.** C then D - each pair is diffed and read by its owner, then superseded by ONE reconciled slice with a new SliceId. Choosing by mtime or by location would be guessing about schema.

**Impact.** Until reconciled, 'apply pos_setup.sql' is ambiguous - the manifest says so explicitly. The review and not-deployable scripts must be resolved or formally excluded before any environment promotion. · **Blocks:** R1 · **Due:** R1 entry · **Status:** APPROVED - direction

### D-40 — PlatformSchemaHistory canonical design (Deployment)

**Evidence.** platform_schema_history.sql is authored but applied nowhere, so the table meant to answer 'which slices are applied here' cannot answer it. manifest.json answers the AUTHORED side (115 scripts with SHA-256 and apply rank) but has no database-side counterpart.

**Options.** A) trust the manifest alone; B) database-side applied record keyed by SliceId + SHA-256; C) external deployment log

**Recommendation.** B - an applied record in each database: SliceId, filename, SHA-256, module, owner, dependencies, applied date, environment, result. The manifest is the authored registry; PlatformSchemaHistory is the applied registry. Neither replaces the other.

**Impact.** Enables drift detection: missing slice, changed applied slice, duplicate SliceId, same filename with a different hash, out-of-order dependency. Must be the FIRST slice applied in every environment. · **Blocks:** R1 · **Due:** R1 entry · **Status:** APPROVED - direction

### D-41 — CrossBusiness Workspace as the first integrated product (Product)

**Evidence.** Three platforms have zero user reach; the product has no unified front door.

**Options.** A) module-by-module UI improvement; B) a unified workspace as the first product; C) defer any UI until all platforms are activated

**Recommendation.** B - CrossBusiness Workspace is the first integrated user-facing product. It is NOT a new business module: it owns no Accounting, CRM, Task or Calendar data, orchestrates access through approved service contracts, must never bypass module permissions, uses CrossBusiness Blue, and requires an approved Metronic reference before UI implementation.

**Impact.** Defines R3. Workspace adds no permissions of its own - every read delegates to the owning module's access service. · **Blocks:** R3 · **Due:** Decided · **Status:** APPROVED
