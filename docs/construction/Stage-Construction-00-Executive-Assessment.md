# Stage-Construction-00 — Executive Assessment

**Product:** CrossBusiness Platform · **This layer:** CrossBusiness Construction & Contracting
**Owning tab:** FOURTH · **Increment:** assessment and architecture only — no production implementation
**Date:** 2026-08-05

---

## 1. The one-paragraph answer

CrossBusiness already contains a **thin but genuine contracting spine**: BOQ, cumulative progress measurement, client
progress certificates (المستخلصات) with retention and advance recovery, subcontractor certificates, variation orders,
project material issue, project labor and equipment-depreciation allocation — 16 tables, 12 services, 46 controller
actions, 16 screens, 10 SQL scripts. It is **not** a construction management system, and it was never claimed to be:
`Project` is an *accounting analytic dimension* (`CrossBuy/Models/Context/Accounting/Dimensions.cs:23`) with nine
nullable contracting columns added, and the entire cost chain is keyed on `ProjectId` alone. There is **no WBS, no cost
code, no budget entity, no commitment, no site, no daily site report, no RFI, no inspection, no document control, no
claim, no delay event, no cash-flow projection, no concurrency token and no field-level audit** anywhere in the model.
The correct move is therefore **neither to keep extending the existing Projects screens nor to build a parallel ERP**:
it is to add a **Construction Layer** that owns construction structure and construction documents, carries the missing
dimensions (Site, WBS, Cost Code), and orchestrates Accounting, Inventory, Purchasing, HR and Projects through
contracts — while first repairing three defects in the spine that already ships.

## 2. What already exists (source-backed)

| Area | Verdict | Evidence |
|---|---|---|
| BOQ | exists, inadequate | `Models/Context/Accounting/Boq.cs` (33 lines), `BL/BoqService.cs` (151), `deploy/sql/boq.sql` |
| Progress measurement | exists, needs extension | `Models/Context/Accounting/ProjectProgress.cs`, `BL/ProgressService.cs` (291) |
| Client certificate | exists, needs extension | `Models/Context/Accounting/ProgressBilling.cs`, `BL/ProgressBillingService.cs` (272) |
| Subcontract + certificate | exists, inadequate | `Models/Context/Accounting/Subcontract.cs`, `BL/SubcontractBillingService.cs` (185) |
| Variation orders | exists, inadequate | `Models/Context/Accounting/VariationOrder.cs`, `BL/VariationOrderService.cs` (164) |
| Retention / advance / recovery | **correct — reuse unchanged** | `BL/ContractService.cs` (1104 / 2104 / 2105, balances read from posted GL) |
| Project material issue | **correct — reuse, extend dimensions** | `BL/ProjectMaterialIssueService.cs` (issues via `StockService` only) |
| Project labor | exists, inadequate | `BL/ProjectLaborService.cs` (derived from `TaskItem.EntityType == "Project"` + timesheets) |
| Equipment cost | exists, inadequate | `Models/Context/Accounting/EquipmentDepreciationAllocation.cs` (a depreciation reclass, not a usage log) |
| Budget | exists as a **report only** | `BL/ProjectBudgetService.cs` (85 lines, read-only; `Project.Budget` is read by nothing) |
| Project record-level access | exists (first tab) | `BL/ProjectsAccessService.cs` (264 lines, 8 actions, `ProjectMembers` relationship) |

The commercial arithmetic in the spine is, where it exists, **sound**: the certificate computes period value as
`cumulative − previously posted` (`BL/ProgressBillingService.cs:110`), advance recovery is capped at the money actually
received on 2104 (line 120), retention and sub-retention balances are read from posted GL lines rather than kept in a
shadow table, and every money movement goes through `ReceivableService` / `PayableService` / `JournalEntryService` and
every stock movement through `StockService`. **No new accounting or stock writer was introduced, and construction must
not introduce one either.**

## 3. What is missing, in one list

Structure: **WBS**, project hierarchy, **Cost Codes**, **Site**.
Commercial: **contract entity**, subcontract scope allocation, bonds, guarantees, insurance, DLP, **claims**, **delay
events**, EOT, approval chains, certification limits.
Control: **budget entity and versions**, **commitments**, forecast, ETC/**EAC**, cost transfer, cash flow, schedule
variance.
Site: **material requests**, **daily site report**, manpower log, equipment usage/downtime/fuel, waste, photos, H&S.
Technical office: **RFI**, **inspection**, **submittal**, method statement, NCR, **drawing register and revision
control**, as-built, punch list, handover, closeout.
Cross-cutting: **RowVersion / concurrency**, **field-level audit**, database integrity (4 FKs and **zero** CHECK
constraints across the whole construction schema), construction tests (**none exist**).

## 4. The three defects that must be fixed before anything is added

These are not design preferences. Each one currently permits a wrong number in a real contract.

1. **CR-01 — the BOQ can be deleted and re-created under a certified history.**
   `BL/BoqService.ReplaceAllAsync` removes every `BoqItem` of the project and re-inserts with new identities
   (`BoqService.cs:108-109`), while previously-billed value is keyed by `BoqItemId`
   (`ProgressBillingService.cs:77-85`). After one BOQ re-save, work already billed looks unbilled, and the next
   certificate bills it again. The endpoint is reachable today (`ProjectController.SaveBoq`, gated on
   `budget-manage`).

2. **CR-02 — a subcontractor can be certified without limit.**
   `SubcontractBillingService.Compute` (line 80) computes period work from a free-typed cumulative figure with no
   comparison to `Subcontract.ContractValue` and no scope lines at all — `SubcontractBilling` has **no line detail**.

3. **CR-03 — a variation rewrites contractual rates in place.**
   `VariationOrderService.ApproveAsync` (line 128) assigns the new quantity and rate straight onto the live
   `BoqItem`. Certificates already posted against the old rate keep no revision snapshot, so their basis silently
   changes.

Full evidence, plus 17 more risks, in `Stage-Construction-15-Risk-Register.md`.

## 5. Reuse decision (the target picture)

```
                    CrossBusiness Construction & Contracting  (this tab)
                    ────────────────────────────────────────────────────
   OWNS:  Site · WBS · Cost Code · BOQ revisions · Budget versions · Commitments
          Client & subcontract contracts · Certificates · Variations · Claims · Delays
          Material requests · Daily site report · Manpower & equipment logs
          RFI · Inspection · Submittal · Drawing register & revisions
          Progress records · Cash-flow plan · Construction audit
                                        │
        ┌────────────┬─────────────┬────┴────────┬────────────┬──────────────┐
        ▼            ▼             ▼             ▼            ▼              ▼
   Accounting    Inventory    Purchasing        HR        Projects      Platform
   (GL, AR/AP,  (items,      (PR, PO,      (employees,  (project      (events, files,
    payments,    warehouses,  GRN,          attendance,  master,       approvals,
    tax, FX,     stock,       supplier      payroll,     team,         timeline,
    periods)     valuation)   invoice)      cost rates)  milestones)   reporting)
        ▲            ▲             ▲             ▲            ▲              ▲
        └────────────┴─────────────┴─────────────┴────────────┴──────────────┘
                        referenced through contracts — never duplicated
```

Nothing in the left-hand column is re-implemented. Concretely: **`JournalEntryService` stays the only GL writer and
`StockService` stays the only stock writer**, retention/advance stay in `ContractService`, material issue keeps going
through `StockService`, employees and cost rates stay in HR, and files stay in `LibraryItem`.

## 6. Where the work is not ours

| Concern | Owner | What this tab does |
|---|---|---|
| Project create/edit/delete is ungated and hardcoded to company 1 (`ProjectController.cs:180-203`, `DefaultCompanyId` used 61×, 24 of 46 actions gated) | **FIRST tab** (Authorization) | Reported as CR-09 with evidence. No file touched. |
| Certificate posting not atomic; ids recovered by `MAX(ID)`; arbitrary currency selection (5 sites) | **Accounting owner** | Reported as CR-04, CR-05, CR-10. Construction must not work around them by posting itself. |
| Timeline, comments, notifications for construction entities | **THIRD tab** | Defines events and payloads only (`Stage-Construction-12`). |
| Report rendering, Report Studio | **SECOND tab** | Defines read-only data-source contracts and DTOs only (`Stage-Construction-13`). |

## 7. Recommendation

Approve a **C1 → C2 foundation-first sequence**: Site + WBS + Cost Code + audit + concurrency + DB integrity, then BOQ
revisions + budget versions + commitments + cost allocation — because those two phases are what make the certificates
and variations that **already ship** safe. Contracts (C3) and certificates (C6) follow; site operations (C4/C5),
quality and document control (C8), reporting integration (C9) and mobile (C10) after that. Full ordering and gates:
`Stage-Construction-16-Implementation-Roadmap.md`.

Fifteen business decisions block parts of this and are listed with options, a recommendation and consequences in
`Stage-Construction-18-Decision-Register.md`. The most urgent three are **D-01** (one or many client contracts per
project), **D-07** (subcontractor over-certification policy) and **D-08** (does budget overspend block or warn).

## 8. Deliverables in this increment

| # | File |
|---|---|
| 00 | `Stage-Construction-00-Executive-Assessment.md` (this file) |
| 01 | `Stage-Construction-01-Repository-Inventory.md` |
| 02 | `Stage-Construction-02-Current-Capability-Matrix.md` *(generated)* |
| 03 | `Stage-Construction-03-Domain-Boundaries.md` |
| 04 | `Stage-Construction-04-WBS-and-BOQ-Design.md` |
| 05 | `Stage-Construction-05-Cost-Control-Design.md` |
| 06 | `Stage-Construction-06-Contracts-and-Certificates.md` |
| 07 | `Stage-Construction-07-Site-Operations.md` |
| 08 | `Stage-Construction-08-Quality-and-Document-Control.md` |
| 09 | `Stage-Construction-09-Variations-Claims-and-Delays.md` |
| 10 | `Stage-Construction-10-Progress-and-Cash-Flow.md` |
| 11 | `Stage-Construction-11-Permissions-and-Isolation.md` |
| 12 | `Stage-Construction-12-Business-Events.md` *(generated)* |
| 13 | `Stage-Construction-13-Reporting-Requirements.md` |
| 14 | `Stage-Construction-14-Mobile-and-Site-UX-Contract.md` |
| 15 | `Stage-Construction-15-Risk-Register.md` *(generated)* |
| 16 | `Stage-Construction-16-Implementation-Roadmap.md` *(generated)* |
| 17 | `Stage-Construction-17-Screen-Inventory.md` *(generated)* |
| 18 | `Stage-Construction-18-Decision-Register.md` *(generated)* |
| — | `Stage-Construction-Final-Delivery-Report.md` |
| — | 7 CSV catalogs + `_generator/generate_construction_catalogs.py` (canonical source of all six generated docs) |

**Not done, by instruction:** no production code changed, no SQL executed against any database, no UI built, no
authorization modified, no files of tabs 1–3 touched.