# CrossBusiness Platform — Roadmap v2 — 07 Implementation Roadmap

**Phases re-derived from dependencies, not inherited from the old stage numbering.**

Machine-readable: `roadmap-v2-phase-plan.csv`, `roadmap-v2-dependencies.csv`.

---

## 1. Why the numbering changed

The old sequence assumed a single tree with one team. Four tabs now build concurrently, three
platforms are complete-but-unusable, and the deployment tree has split in two. The binding
constraint is no longer feature scope — it is **integration**. So R1 is not a feature phase:
it makes the tree deployable before anything else ships.

R2, R3 and R4 then convert the three existing foundations into product, because a foundation
with no consumer decays: it drifts from real data shapes and cannot be validated.

## 2. Phase overview

| PhaseId | Name | Objective | Prerequisites | Owner | Complexity | ParallelSafety |
|---|---|---|---|---|---|---|
| R0 | Roadmap Reconciliation | Establish one verified account of what exists and freeze an owner-approved plan. | B6 closed | First Tab | Low | Safe - documentation only |
| R1 | Repository, Ownership and Deployment Governance | Make the repository committed, owned and deployable before anything is activated. | R0, D-38, D-39, D-40 | Integration Owner | High | Requires all four tabs to adopt simultaneously |
| R10 | Financial Operations Advancement | Deepen accounting beyond posting. | R1, R4, D-01 | To be assigned | High | Safe |
| R11 | Customer and Partner Portals | Extend the platform outside the company. | R4, R6, D-13, D-14, D-29 | To be assigned | Very High | Requires the isolation decision first |
| R12 | AI, Search and Automation | Add intelligence over a governed data surface. | R4, R10, D-27, D-28 | To be assigned | High | Safe once governance is decided |
| R13 | Industry Packs | Package vertical configurations. | R7, R9, R10 | To be assigned | Medium | Safe |
| R14 | Mobile and Field Operations | Deliver a real field client. | R5, R7, D-30 | To be assigned | High | Safe |
| R15 | Ecosystem and Developer Platform | Open the platform to integrators. | R12 | To be assigned | High | Safe |
| R2 | Platform Activation Foundation | Controlled activation of the three built-but-dark foundations. No broad UI. | R1, D-05, D-06, D-07, D-19 | Integration Owner with Second, Third and Fourth Tabs | High | Safe once R1 shared-file rules hold |
| R3 | CrossBusiness Workspace Foundation | Deliver the first integrated user-facing product. | R2 | To be assigned | High | Safe - consumes contracts, owns no module |
| R4 | Report Center and First Dataset | Bind Reporting to real data and deliver report consumption. | R2, R3, D-35 | Second Tab | High | Safe - Second Tab owns Reporting |
| R5 | Tasks and Calendar Integration | Integrate two EXISTING systems. Not greenfield. | R3, D-32, D-33, D-12 | To be assigned | Medium | Safe once Communication is activated |
| R6 | Security Console, CRM Conversion and Master Data | Close the last bootstrap-open exposure behind an operator surface. | R1, D-01, D-02, D-03, D-04, D-31 | First Tab | High | Safe - First Tab owns these files |
| R7 | Construction C2-C8 | Deliver the construction platform beyond the commercial foundation. | R2 (C1 schema applied), D-20, D-21, D-24, D-25, D-26 | Fourth Tab | Very High | Safe - Fourth Tab owns Construction |
| R8 | HR and Workforce | Modernise the broadest legacy module set. | R1, R6 | To be assigned | Medium | Safe |
| R9 | WMS and Manufacturing Advancement | Advance stock and production beyond transactional basics. | R1, D-02 | To be assigned | High | Safe |

## 3. Dependency edges

| DependencyId | From | To | Type | Reason |
|---|---|---|---|---|
| DEP-01 | R1 | R0 | Sequential | Cannot govern the repository before agreeing what it contains. |
| DEP-02 | R2 | R1 | Sequential | No schema is activated until there is an authoritative applied record. |
| DEP-03 | R3 | R2 | Sequential | The workspace surfaces platforms that must first be reachable. |
| DEP-04 | R4 | R2 | Sequential | Report Center needs the reporting foundation activated. |
| DEP-05 | R4 | R3 | Capability | Saved and recent reports surface inside the workspace. |
| DEP-06 | R5 | R3 | Capability | Task and calendar views are workspace surfaces. |
| DEP-07 | R5 | R2 | Capability | Task comments and timeline require Communication activation. |
| DEP-08 | R6 | R1 | Sequential | CRM conversion and the console need a stable integration branch. |
| DEP-09 | R7 | R2 | Sequential | C2+ requires the C1 schema applied and measured. |
| DEP-10 | R8 | R6 | Capability | HR debt retirement follows the security console. |
| DEP-11 | R9 | R1 | Sequential | WMS slices need the canonical SQL root. |
| DEP-12 | R10 | R4 | Capability | Financial statements should be served by the reporting platform. |
| DEP-13 | R11 | R4 | Capability | Portals need report and document surfaces. |
| DEP-14 | R11 | R6 | Capability | Portal customers need the converted CRM and Support model. |
| DEP-15 | R12 | R10 | Data | AI needs a governed and audited data surface. |
| DEP-16 | R12 | R4 | Data | Search and AI consume the dataset layer. |
| DEP-17 | R13 | R7 | Capability | Contracting pack needs construction. |
| DEP-18 | R13 | R9 | Capability | Retail pack needs WMS advancement. |
| DEP-19 | R14 | R5 | Capability | Mobile work surface mirrors My Work. |
| DEP-20 | R14 | R7 | Capability | Site capture is a construction requirement. |
| DEP-21 | R15 | R12 | Capability | Public API follows the automation surface. |
| DEP-22 | R1 | D-38 | Decision | Canonical SQL root must be chosen before consolidation. |
| DEP-23 | R1 | D-40 | Decision | PlatformSchemaHistory design gates the applied record. |
| DEP-24 | R2 | D-05 | Decision | Communication activation needs the privacy ceiling. |
| DEP-25 | R2 | D-19 | Decision | Construction C1 schema needs an approved environment. |
| DEP-26 | R4 | D-35 | Decision | Report Center needs its pilot dataset - APPROVED. |
| DEP-27 | R5 | D-32 | Decision | Task ownership - APPROVED. |
| DEP-28 | R5 | D-33 | Decision | Calendar ownership - APPROVED. |
| DEP-29 | R6 | D-03 | Decision | CRM conversion blocked on customer-data breadth. |
| DEP-30 | R6 | D-04 | Decision | CRM conversion blocked on visible-owner scope. |
| DEP-31 | R11 | D-13 | Decision | Portals need the external principal model. |

## 4. Phase detail

### R0 — Roadmap Reconciliation

**Objective.** Establish one verified account of what exists and freeze an owner-approved plan.

**Business value.** Removes planning based on stale assumptions.

| Field | Value |
|---|---|
| Included | Capability catalog, screen inventory, architectures, registers, decision register, product vision, deployment governance design. |
| Excluded | Any production code change. |
| Prerequisites | B6 closed |
| Owner | First Tab |
| Screens | None |
| Services | None |
| Database work | None |
| API work | None |
| Security gates | No production change |
| Migration gates | None |
| Test gates | Build stays green |
| Complexity | Low |
| Parallelization safety | Safe - documentation only |

**Completion criteria.** COMPLETE after this owner-review increment.

### R1 — Repository, Ownership and Deployment Governance

**Objective.** Make the repository committed, owned and deployable before anything is activated.

**Business value.** Every later phase inherits this or inherits its absence.

| Field | Value |
|---|---|
| Included | Commit and preserve current platform work; per-tab worktrees; protected integration branch; tab/shared-file/ADR/SQL-slice registries; canonical SQL root; PlatformSchemaHistory; integration gate; CI validation; stale-build prevention. |
| Excluded | Any feature work. Any schema activation. |
| Prerequisites | R0, D-38, D-39, D-40 |
| Owner | Integration Owner |
| Screens | None |
| Services | No new services |
| Database work | Design and apply PlatformSchemaHistory; adopt manifest.json as the authored-slice registry; reconcile the 4 divergent duplicates; resolve the 1 review and 1 not-deployable script |
| API work | None |
| Security gates | No new debt; CBA gates 0/0/0 enforced in CI |
| Migration gates | Slice inventory reconciled; every applied slice recorded with SHA-256 |
| Test gates | Full suite green on the integration branch |
| Complexity | High |
| Parallelization safety | Requires all four tabs to adopt simultaneously |

**Completion criteria.** One canonical authored root, an authoritative applied record, integration gate enforced in CI, zero build breaks for one full cadence, all tab work committed.

### R2 — Platform Activation Foundation

**Objective.** Controlled activation of the three built-but-dark foundations. No broad UI.

**Business value.** Converts 533 passing tests and 34 tables from zero reach to reachable capability.

| Field | Value |
|---|---|
| Included | Reporting foundation activation; Communication platform activation (Program.cs registration + hosted worker); Construction C1 schema applied in an approved non-production environment; DocComments migration; notification-path reconciliation. |
| Excluded | Report Center UI; Communication UI; Workspace; Construction production deployment. |
| Prerequisites | R1, D-05, D-06, D-07, D-19 |
| Owner | Integration Owner with Second, Third and Fourth Tabs |
| Screens | None |
| Services | COM-08 activation; REP-01/02/03 activation |
| Database work | Apply communication slice 001 and reporting slice via the R1 gate; apply Construction C1 DDL in the approved environment only |
| API work | Activation endpoints only |
| Security gates | Every new endpoint attribute- or in-body-secured; debt does not grow |
| Migration gates | DocComments migration reversible and measured with before/after counts; Construction M3/M9 recorded |
| Test gates | 221/221 and 274/274 retained plus activation tests |
| Complexity | High |
| Parallelization safety | Safe once R1 shared-file rules hold |

**Completion criteria.** All three foundations reachable in a running environment, migrations measured, one notification path per fact.

### R3 — CrossBusiness Workspace Foundation

**Objective.** Deliver the first integrated user-facing product.

**Business value.** The product gains a front door; platform investment becomes visible to users.

| Field | Value |
|---|---|
| Included | Workspace shell and navigation; My Work; notifications and mentions centre; approvals inbox; recent activity and timeline; personal dashboard. |
| Excluded | Team dashboard if OrgHierarchy scope is unresolved; Report Studio; module rewrites. |
| Prerequisites | R2 |
| Owner | To be assigned |
| Screens | SCR-30, SCR-36, SCR-44, SCR-45, SCR-46, SCR-48, SCR-49 |
| Services | Workspace orchestration only - no new business services |
| Database work | None - Workspace owns no tables |
| API work | Read-only aggregation contracts |
| Security gates | Workspace adds NO permissions; every read delegates to the owning module's access service; hiding a control is not a control |
| Migration gates | None |
| Test gates | Workspace suite plus per-module delegation tests |
| Complexity | High |
| Parallelization safety | Safe - consumes contracts, owns no module |

**Completion criteria.** A user opens one surface and sees their tasks, approvals, mentions and activity across modules, with every item authorized by its owning module.

### R4 — Report Center and First Dataset

**Objective.** Bind Reporting to real data and deliver report consumption.

**Business value.** First user-visible return on the reporting investment, at low data sensitivity.

| Field | Value |
|---|---|
| Included | Business Events / Platform Operations pilot dataset (read-only); Report Center; parameters; HTML preview; CSV and Excel; history; archive; saved reports; favourites. |
| Excluded | Report Studio; PDF runtime; email delivery; hosted scheduler; any ledger, stock-cost or CRM dataset. |
| Prerequisites | R2, R3, D-35 |
| Owner | Second Tab |
| Screens | SCR-33, SCR-47 |
| Services | REP-07, REP-09 |
| Database work | Reporting slice already applied in R2 |
| API work | Report APIs |
| Security gates | Dataset-level authorization; no sensitive payload exposed by default; row caps enforced |
| Migration gates | Pilot dataset read-only, no write path |
| Test gates | 221/221 retained plus dataset and UI tests |
| Complexity | High |
| Parallelization safety | Safe - Second Tab owns Reporting |

**Completion criteria.** The Business Events dataset renders in HTML, CSV and Excel from the Report Center with parameters, history, archive and permissions proved.

### R5 — Tasks and Calendar Integration

**Objective.** Integrate two EXISTING systems. Not greenfield.

**Business value.** Turns weak-but-real capability into daily-use capability.

| Field | Value |
|---|---|
| Included | Audit existing Tasks and Calendar implementation; consolidate ownership per D-32/D-33; register Tasks as a Comm entity surface; unified Task/Calendar views; Workspace integration; assess legacy task activity data before migration. |
| Excluded | Rebuilding Tasks or Calendar from zero; Support external principal. |
| Prerequisites | R3, D-32, D-33, D-12 |
| Owner | To be assigned |
| Screens | SCR-37 |
| Services | TSK-01..05, CAL-01..02, COM-06 |
| Database work | Assess the 7 existing task slices and the calendar slice before any schema change |
| API work | Task and calendar APIs |
| Security gates | Task authorization reviewed against the debt list |
| Migration gates | Legacy task activity assessed before migration; Tasks must not keep a second general timeline |
| Test gates | Task and calendar suites plus integration tests |
| Complexity | Medium |
| Parallelization safety | Safe once Communication is activated |

**Completion criteria.** Tasks owns task state and rules; Communication owns comments, mentions and unified timeline; Calendar owns events and presentation; all three visible in one workspace.

### R6 — Security Console, CRM Conversion and Master Data

**Objective.** Close the last bootstrap-open exposure behind an operator surface.

**Business value.** Retires the most significant remaining security gap.

| Field | Value |
|---|---|
| Included | CRM B6 conversion (both sites); Security Console; Master Data foundation; authorization debt burn-down start; Support workspace. |
| Excluded | AI governance; portal external identity. |
| Prerequisites | R1, D-01, D-02, D-03, D-04, D-31 |
| Owner | First Tab |
| Screens | SCR-31, SCR-32, SCR-40 |
| Services | SEC-04, SEC-08, MDM-01, SUP-01 |
| Database work | Apply bootstrap policy slice via the R1 gate |
| API work | Grant and policy APIs |
| Security gates | CBA gates stay 0/0/0; debt strictly decreases with reconciliation |
| Migration gates | Bootstrap policy seed per company |
| Test gates | Focused CRM suite plus full suite |
| Complexity | High |
| Parallelization safety | Safe - First Tab owns these files |

**Completion criteria.** Both CRM sites converted with B6-grade evidence, console delivered, debt below 143 and reconciled.

### R7 — Construction C2-C8

**Objective.** Deliver the construction platform beyond the commercial foundation.

**Business value.** Closes the assessed gap between Projects and a real construction platform.

| Field | Value |
|---|---|
| Included | WBS, cost codes, budgets, commitments, site operations, DSR, RFI, document control, claims, delays, cash flow, construction UI. |
| Excluded | Primavera/MS Project integration; resource levelling. |
| Prerequisites | R2 (C1 schema applied), D-20, D-21, D-24, D-25, D-26 |
| Owner | Fourth Tab |
| Screens | SCR-38, SCR-39 |
| Services | CON-05, FIL-02 |
| Database work | C2+ slices through the R1 gate |
| API work | Construction APIs |
| Security gates | Project-scoped authorization |
| Migration gates | M3 and M9 recorded; D-08 default confirmed per company |
| Test gates | 38/38 retained plus C2+ suites |
| Complexity | Very High |
| Parallelization safety | Safe - Fourth Tab owns Construction |

**Completion criteria.** C1 measured and confirmed; C2-C8 delivered with UI.

### R8 — HR and Workforce

**Objective.** Modernise the broadest legacy module set.

**Business value.** HR is wide but shallow and carries significant authorization debt.

| Field | Value |
|---|---|
| Included | HR debt retirement; workflow integration; employee self-service; mobile attendance. |
| Excluded | Payroll engine replacement. |
| Prerequisites | R1, R6 |
| Owner | To be assigned |
| Screens | HR workspaces |
| Services | HR-01..HR-07 |
| Database work | HR slice review |
| API work | HR APIs |
| Security gates | HR debt retired with evidence |
| Migration gates | None |
| Test gates | HR suites |
| Complexity | Medium |
| Parallelization safety | Safe |

**Completion criteria.** HR actions authorized, self-service delivered, attendance on mobile.

### R9 — WMS and Manufacturing Advancement

**Objective.** Advance stock and production beyond transactional basics.

**Business value.** Unlocks warehouse efficiency and production costing.

| Field | Value |
|---|---|
| Included | Advanced WMS (wave picking, putaway, cycle-count planning); manufacturing variance and planning. |
| Excluded | Full APS. |
| Prerequisites | R1, D-02 |
| Owner | To be assigned |
| Screens | Inventory workspaces |
| Services | INV-05, MFG-02 |
| Database work | WMS slices |
| API work | Inventory APIs |
| Security gates | Warehouse scope enforced post-B6 |
| Migration gates | None |
| Test gates | Inventory suites |
| Complexity | High |
| Parallelization safety | Safe |

**Completion criteria.** Wave picking and putaway live; production variance reported.

### R10 — Financial Operations Advancement

**Objective.** Deepen accounting beyond posting.

**Business value.** Financial control and compliance.

| Field | Value |
|---|---|
| Included | Generic workflow engine; platform-wide audit; consolidated statements; tax depth. |
| Excluded | IFRS reporting packs. |
| Prerequisites | R1, R4, D-01 |
| Owner | To be assigned |
| Screens | Accounting workspaces |
| Services | WFL-02, AUD-03, ACC-07 |
| Database work | Accounting slices |
| API work | Accounting APIs |
| Security gates | Accounting debt retired |
| Migration gates | None |
| Test gates | Accounting suites |
| Complexity | High |
| Parallelization safety | Safe |

**Completion criteria.** Approval routing live, audit unified, statements served from the reporting platform.

### R11 — Customer and Partner Portals

**Objective.** Extend the platform outside the company.

**Business value.** New service and revenue channel.

| Field | Value |
|---|---|
| Included | ExternalPrincipalContext; portal isolation; self-service; partner access; customer-visible replies. |
| Excluded | Payment gateway. |
| Prerequisites | R4, R6, D-13, D-14, D-29 |
| Owner | To be assigned |
| Screens | SCR-41 |
| Services | PRT-01, PRT-02, SUP-02 |
| Database work | Portal slices |
| API work | Portal APIs |
| Security gates | External principal isolation proved; a customer is never an employee context |
| Migration gates | None |
| Test gates | Portal suites |
| Complexity | Very High |
| Parallelization safety | Requires the isolation decision first |

**Completion criteria.** External users transact under a proved isolation boundary distinct from employee context.

### R12 — AI, Search and Automation

**Objective.** Add intelligence over a governed data surface.

**Business value.** Differentiation once the data platform is trustworthy.

| Field | Value |
|---|---|
| Included | Search index; AI governance; assistant surfaces; automation. |
| Excluded | Autonomous financial actions. |
| Prerequisites | R4, R10, D-27, D-28 |
| Owner | To be assigned |
| Screens | SCR-42 |
| Services | SRC-01, AI-01..AI-03 |
| Database work | Search index |
| API work | AI APIs |
| Security gates | AI reads the reporting dataset layer, never raw tables; search honours per-module authorization at query time |
| Migration gates | None |
| Test gates | AI and search suites |
| Complexity | High |
| Parallelization safety | Safe once governance is decided |

**Completion criteria.** Search across modules respects authorization; AI actions governed and audited.

### R13 — Industry Packs

**Objective.** Package vertical configurations.

**Business value.** Faster time to value per industry.

| Field | Value |
|---|---|
| Included | Hyper retail pack; contracting pack; services pack. |
| Excluded | New core modules. |
| Prerequisites | R7, R9, R10 |
| Owner | To be assigned |
| Screens | Pack-specific |
| Services | Configuration only |
| Database work | Seed data |
| API work | None |
| Security gates | Per-pack authorization review |
| Migration gates | None |
| Test gates | Pack suites |
| Complexity | Medium |
| Parallelization safety | Safe |

**Completion criteria.** Each pack installs as configuration with no core fork.

### R14 — Mobile and Field Operations

**Objective.** Deliver a real field client.

**Business value.** Field capture is where the data originates.

| Field | Value |
|---|---|
| Included | Mobile workspace; offline policy; site capture; POS mobility. |
| Excluded | Full offline selling. |
| Prerequisites | R5, R7, D-30 |
| Owner | To be assigned |
| Screens | SCR-21 expansion |
| Services | MOB-01, POS-04 |
| Database work | Sync schema |
| API work | Mobile APIs |
| Security gates | Mobile authorization equals web authorization |
| Migration gates | Offline conflict policy proved |
| Test gates | Mobile suites |
| Complexity | High |
| Parallelization safety | Safe |

**Completion criteria.** Field users capture offline and sync under a proved conflict policy.

### R15 — Ecosystem and Developer Platform

**Objective.** Open the platform to integrators.

**Business value.** Ecosystem leverage.

| Field | Value |
|---|---|
| Included | Integration hub; public API; webhooks; developer documentation. |
| Excluded | Marketplace billing. |
| Prerequisites | R12 |
| Owner | To be assigned |
| Screens | Developer portal |
| Services | INT-01 |
| Database work | Integration tables |
| API work | Public API |
| Security gates | External API authorization and rate limiting |
| Migration gates | None |
| Test gates | Integration suites |
| Complexity | High |
| Parallelization safety | Safe |

**Completion criteria.** Third parties integrate through a documented, authorized public API.
