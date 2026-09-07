# Stage-Construction-16 — Implementation Roadmap

> **Generated file — do not edit by hand.**
> Source of truth: `docs/construction/_generator/generate_construction_catalogs.py`.
> Regenerate with `python docs/construction/_generator/generate_construction_catalogs.py`.
> The paired CSV in this folder is emitted from the same dataset in the same run.

Dependency-ordered. The `DependsOn`/`Blocks` columns are the contract; the order is not a preference.

## Why this order and not the obvious one

The obvious order is "build the visible documents first". That is what produced the current defects: certificates and
variations exist while WBS, cost codes, budget, immutability and concurrency do not — so a certificate can be raised
against a BOQ that can be deleted (CR-01) and a variation can silently rewrite a rate a certificate already used
(CR-03).

So the foundation phases (C1, C2) exist to make the documents that *already ship* safe, before anything new is added
on top of them.

## Hard dependency rules carried from the brief

- WBS before BOQ allocation · Cost codes before cost analysis · BOQ before progress certificates
- Contracts before certificates · Variations before revised BOQ values
- Document control before site drawing enforcement · Reporting DTOs before reporting integration
- Permissions before production screens · Evidence before migration · Migration before cleanup

## Phases

| Phase | Title | Contents | DependsOn | Blocks | Gate | Status |
|---|---|---|---|---|---|---|
| C0 | Assessment and decisions | This increment: inventory, matrix, boundaries, design, catalogs, decisions | none | C1..C10 | Owner review of the decision register; referral of CR-09 to the first tab | Delivered by this increment |
| C1 | Construction foundation | ConstructionSite, project extensions, WbsNode, CostCode, ConstructionAudit, RowVersion, DB integrity | C0 decisions D-01,D-02,D-13 | C2,C4,C5 | WBS depth + re-parent policy agreed; additive SQL only | Not started |
| C2 | BOQ, Budget, Cost Control | BoqRevision + immutability, ProjectBudget versions, ProjectCommitment, CostAllocation, forecast/EAC | C1 (WBS + CostCode must exist first) | C3,C6 | CR-01 closed; budget control policy decided (D-08) | Not started |
| C3 | Client and subcontract contracts | ClientContract, SubcontractScope, bonds, insurance, DLP, approval chain | C2 (BOQ revisions exist) | C6 | CR-02 capped; retention/advance methods decided (D-04,D-05) | Not started |
| C4 | Site material operations | MaterialRequest, site store binding, issue/return with WBS+CostCode, waste | C1 (site + cost code) | C5,C6 | No second inventory engine: StockService remains the only stock writer | Not started |
| C5 | Daily site reports, manpower, equipment | DSR, ManpowerLog, EquipmentLog, fuel, downtime, photos, incidents | C1, C4 | C6,C9 | HR and asset masters referenced, never duplicated | Not started |
| C6 | Progress certificates | Client + subcontractor certificates with the full review chain and revision snapshots | C2, C3 (BOQ + contract), C4/C5 for MOS and quantities | C7,C9 | CR-04, CR-05, CR-08 resolved with the Accounting owner | Not started |
| C7 | Variations, claims, delays | Variation states + BOQ revision emission, Claim, DelayEvent, EOT, schedule variance | C6 (certificates must snapshot revisions first) | C9 | CR-03 closed | Not started |
| C8 | RFIs, inspections, submittals, document control | Quality documents + DocumentRegister/Revision, punch list, handover, closeout | C1; C5 for site linkage | C9,C10 | CR-13 closed before any site drawing enforcement | Not started |
| C9 | Reporting and dashboards integration | Read-only data sources + DTOs handed to the second tab; timeline payloads handed to the third | C2,C3,C6,C7,C8 (data must exist) | C10 | Report DTOs defined before integration; no renderers built here | Not started |
| C10 | Mobile site workspace | Offline DSR, inspection, RFI, punch list, photo, receipt | C5,C8; CR-18 idempotency contract | C11 | Privacy and permission review passed | Not started |
| C11 | Schedule baselines and planning interchange | Activity + baseline entities; Primavera P6 XER import; Microsoft Project import where technically feasible (MPP is a closed binary — via MSPDI/XML export, or XER only, decided after a spike); schedule variance against an approved baseline | C1 (WBS identity), C7 (delay/EOT need a baseline to measure against) | C12 | A spike report states exactly which XER/MSPDI fields map to WbsNode/Activity and which are dropped, BEFORE any importer is written; no import may create or re-parent a WBS node that carries cost | Not started — deferred by the C1 brief |
| C12 | Resource leveling | Labour and equipment demand curves from the schedule; over-allocation detection; leveling proposals for planner approval | C11 (a baseline schedule), C5 (manpower and equipment actuals) | none | Leveling never writes an actual: it proposes a plan a human approves | Not started — deferred by the C1 brief |

## Gate discipline

No phase starts while the previous phase's `Gate` column is unmet, and every phase ends with: additive idempotent SQL
in `deploy/sql` (never an EF migration), tests for its own invariants, and a re-read-from-a-new-context acceptance.
