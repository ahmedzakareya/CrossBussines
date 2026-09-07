# Stage 006 — Stage 1 Batch C result

**Measured:** 2026-08-04 · **Scope:** Stage 1 Batch C (missing module access services: HR, Projects, Tasks,
Communication) · **Basis:** `Permission-Coverage.csv` regenerated and independently reconciled · 604 tests passing on
SQL Server · ADR-026/027/028/029.
**Immutable:** Stages 000-005 are unchanged. This record adds one row; it rewrites none.

---

## 1. Scores

| # | Dimension | Weight | Level | % | Score | Δ vs 005 |
|---|---|---|---|---|---|---|
1 | ERP Business Coverage | 20 | 3 | 60 | 12.00 | — |
2 | Security and Company Isolation | 15 | 3+ | 70 | 10.50 | **—** |
3 | Platform Kernel and Audit | 10 | 4+ | 90 | 9.00 | — |
4 | Business Object Coverage | 10 | 1 | 20 | 2.00 | — |
5 | Task and Work Management | 10 | 3 | 60 | 6.00 | — |
6 | Workflow and Approvals | 10 | 2 | 40 | 4.00 | — |
7 | Communication and Collaboration | 10 | 2+ | 50 | 5.00 | — |
8 | Search, AI and Intelligence | 7 | 1 | 20 | 1.40 | — |
9 | Testing and Reliability | 5 | 2+ | 50 | 2.50 | — |
10 | Deployment and Operations | 3 | 3 | 60 | 1.80 | — |
| | **Total** | **100** | | | **54.20** | **0.00** |

| | |
|---|---|
| Batch completion | **~85%** — see §5 for the two Partial items |
| Maturity **before** | **54.20%** |
| Maturity **after** | **54.20%** |
| Net percentage-point gain | **0.00 pp** |
| Relative improvement | **0.00%** |
| Changed dimensions | **none** |
| Unchanged dimensions | **all ten** |

## 2. Why the foundations batch scores zero

Batch C created the authorization foundation three modules never had: one shared RBAC table, a role directory, four
session-free access services, four adapters, record-level rules for HR / Projects / Tasks / Communication, and 112
tests. It also closed a latent cross-company hierarchy leak for new code.

It still moves no score, and the instruction says why: *"Do not award points merely because access-service classes
were added. Security maturity may increase only when real production paths are enforced and tested."*

**Four production paths are enforced.** Four of 118 in-scope mutating actions — the C9 proof set, deliberately narrow
by instruction. Dimension 2's level 80 also requires *"no hard-coded company anywhere"*, and that is still false:
`AccountingController` (MVC) uses `DefaultCompanyId`, `ProjectController` and `TasksController` use it in the very
actions Batch C did not touch, and `PosCompanyPolicy.CatalogCompanyId = 1` remains. The model moves in 10-point
steps, so the only positions are 70 and 80.

**One access contract** is now *closer* — `IModuleAccessService` covers eight modules instead of four — but "one
contract" is not the level's only clause, and inventing a 75 to reward progress would be exactly the flattery the
model exists to prevent. **Dimension 2 stays at 70.**

Dimension 9 stays at 50: level 3 requires *"the financial writers and business flows tested"*. 114 new tests cover
authorization, not `JournalEntryService` or `StockService` costing.

Dimension 5 (Task and Work Management) and 7 (Communication) stay put: Batch C added **authorization** to those
modules, not capability. Neither dimension measures authorization.

## 3. What WOULD move dimension 2 to 80

1. Remove `DefaultCompanyId` from `AccountingController`, `ProjectController`, `TasksController` and the remaining
   MVC controllers; resolve or retire `PosCompanyPolicy.CatalogCompanyId`.
2. **Batch D** — remediate the 183-action backlog now that the foundations exist.
3. Repoint `CrmAccessService.TeamOwnerIdsAsync` and `LeaveWorkflowService` at `IOrgHierarchy` so the company
   intersection covers existing code too (ADR-026 §5).

## 4. Correction versus implementation impact — and a coincidence disclosed

**Zero correction impact.** No published figure was found wrong; nothing is withdrawn.

The backlog reads **183 before and 183 after**, and that is a **COINCIDENCE, not an absence of change**:

```
183  before
 -2  Batch C proof endpoints authorized (PeopleController.DecideLeave, TasksController.ConfirmMatch)
 +1  HyperPosController.StampInvoiceCustomer — a new mutating lane action with NO in-body check,
     added CONCURRENTLY by the parallel team (Batch C touched neither POS controller)
 +1  a further mutating action in DevSeedController, likewise concurrent
183  after
```

Mutating total moved **384 → 388** and in-body **50 → 54** for the same reasons. The lane-guarded population moved
**44 → 47** with in-body **40 → 42**. All of it is reconciled against source, the pinned test now asserts the new
figures, and **Batch C's own contribution is −2**. Reporting "183 unchanged" without this breakdown would imply the
batch achieved nothing; reporting "−2" without the +2 would take credit for someone else's tree.

**No measurement mechanism changed.** The scanner's in-body detection already recognised the access-service call
shape from CORRECTION-004, so the four proof endpoints were detected without touching it.

## 5. Requirement completeness — two Partial items, no silent gaps

| Item | Status |
|---|---|
| C10/15 — a test proving `AccessScope` needs no per-record permission call | **Partial.** The contract, the Tasks implementation and the `CanAsync`/`ResolveScopeAsync` **agreement** test all exist. The query-translation demonstration was not added. |
| C6/12 + C10/19 — SignalR joins use the same membership rule | **Partial.** `IsConversationParticipantAsync` is implemented and exposed for the hubs; **no hub was modified**, so hub authorization is unchanged. |

Both are stated in the delivery report with exact missing work, reason, affected files and next action.

## 6. Explicitly awarded no points

* the two SQL schemas and the entities — schema is not enforcement;
* the four access-service classes and four adapters — classes are not enforcement;
* `IPlatformRoleDirectory`, `IOrgHierarchy`, `AccessScope` — infrastructure;
* the four ADRs and the roadmap entries (Role Templates, Role Groups, Delegation Framework, field-level
  permissions, Workflow/AI/Reports/Search/Unified Inbox) — **all Planned / Not Implemented**.

## 7. Evidence

| Claim | Where |
|---|---|
| Role directory: company intersection, active/inactive, future/expired, branch, unknown scope, unsupported principal | `BatchCAccessServiceTests` §1 (11 cases) |
| Shared gates on all four services (context, action, company, worker, system, identity) | `BatchCAccessServiceTests` §2 (24 cases) |
| HR record-level: self, confidential-not-self, cross-company, approver chain, inactive | `BatchCAccessServiceTests` §3-4 |
| Cross-company hierarchy exclusion | `The_hierarchy_walk_never_returns_another_companys_employee` |
| Projects: membership, ended membership, budget denied to members, project-row company checked first, billing delegation | `BatchCAccessServiceTests` §5 |
| Tasks: assignee/creator/unrelated, reassign stronger, linked entity, casing guard, scope agreement | `BatchCAccessServiceTests` §6 |
| Communication: participant-only, group management, cross-company, outbox never bootstrap-open, branch publisher | `BatchCAccessServiceTests` §7 |
| Five existing adapters unchanged with a null target; four new adapters forward it; malformed target denies | `BatchCAdapterForwardingTests` (41) |
| Startup validation of scopes | `BatchCAdapterForwardingTests` §4 |
| SQL verified twice + 13 constraint tests on a disposable database, then dropped | Delivery report §3 |
| Suite | **606 passed / 0 failed / 0 skipped** on SQL Server; 564 passed / 42 skipped on SQLite alone |
