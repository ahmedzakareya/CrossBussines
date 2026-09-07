# 00 — Executive Summary

> **Read-only evidence.** Every claim below cites a file and, where it matters, a line. Nothing in
> this pack was inferred from product expectation. Where the repository does not prove something, it
> says so rather than filling the gap.
>
> Refs analysed: `master` = **60dc115**, `tasks/phase4` = **7c6fcea** (tip of the Task foundation,
> superset of phase1–3). Generated 2026-09-02.


## Repository reality

| | |
|---|---|
| Current branch | `master` |
| `master` HEAD | **60dc115** — *Docs: append the launch note for the next phase* (2026-09-01) |
| `tasks/phase3` | **5eda60b** (2026-09-02) — 29 ahead of master, **0 behind, UNMERGED** |
| `tasks/phase4` | **7c6fcea** (2026-09-02) — 37 ahead of master, **0 behind, UNMERGED** |
| Worktrees | 18 |
| Stashes | 3 |
| Dirty files in shared tree | **772** (482 modified, 290 untracked), 0 staged |
| Remotes | 0 — nothing has ever been pushed |

The brief named `tasks/phase3 = 5eda60b` as the Task foundation baseline. That is accurate as a branch
tip, **but it is not the checked-out branch and it is not merged**. Analysis therefore covers both
refs and reports the delta explicitly.

The shared working tree carries 772 dirty files from at least six concurrent tabs, so **no conclusion
in this pack is drawn from the working tree**. Everything is read from committed refs via
`git show`/`git grep` against `60dc115` and `7c6fcea`.

## Counts

| Metric | Count |
|---|---|
| Modules with a UI surface | 21 |
| Projects in solution | 4 (`CrossBuy`, `CrossBuy.Tests`, `CrossBuy.Analyzers`, `CrossBuy.Analyzers.Tests`) |
| DbContexts | **1** (`CrossDbContext`) |
| Controllers | 57 |
| Views | 361 |
| BL files | 306 |
| Hosted/background workers | **7** |
| Business-event families declared | 8 |
| Business-event types declared | 34 |
| Registered outbox consumers | **4** on phase4, **3** on master |
| Production TaskItem creation doors | **4** on phase4, **3** on master |
| Orchestration rules implemented | **1** (`DocumentVerificationRule`) |
| Report dataset providers | 5 — none for Tasks |
| Tracked test files | 194 + 11 analyzer |
| Authorization gaps (frozen baseline) | **93** of 439 mutating actions |

## Top findings

1. **The Task foundation is unmerged.** 37 commits, zero on master. Phase 4 is not a design awaiting
   implementation — it is implemented on a branch nothing integrates from.
2. **Automated tasks raise no business event.** `TaskService.cs:345` is the only caller of
   `TaskCreatedAsync`. The generator, document-expiry and orchestration doors each write
   `TaskItems.Add` directly and inject no publisher, so tasks they create are invisible to the
   Timeline, Notification and AI projections.
3. **Four creation doors, three of which bypass `TaskService.SaveAsync`** — verdict **FRAGMENTED**.
4. **`Hierarchical` has no `CompanyID`.** The org tree is global across all 14 companies; every
   manager walk is cross-tenant by construction and each caller must re-filter by
   `Employee.EmpCompanyID` itself.
5. **TM-2 confirmed live:** `EntityRegistry` does not company-filter Employee, so
   `BelongsToCompanyAsync("Employee", …)` answers yes for anybody. `TaskEscalationService:216`
   documents this and works around it.
6. **`Task.Cancelled` is a reserved contract, not a state.** Declared in `BusinessEventTypes:188`,
   registered in `TaskCalendarIntegrationContracts:341`, has a notification kind — and has **no
   lifecycle state and no producer**.
7. **Escalation 24/24/2 are technical defaults, not business policy** — `TaskEscalationTiming.cs:30`
   says so in terms.
8. **`TaskManagementQueryService` is headless.** DI-registered at `Program.cs:683`, wired to no
   controller, view or report.
9. **Reporting cannot see Tasks.** Five dataset providers exist (Accounting, BusinessEvents, CRM,
   HrRoster, Inventory); there is no Task dataset.
10. **Three unrelated approval mechanisms** (`InventoryApproval`, `LeaveApprovalStep`,
    `EmployeeRequest`) with a read-only inbox over them, and **no approval business events**.

## Phase-4 recommendation

**REVISE** — not because the design is wrong, but because it is already partly built and the
integration question, not the design question, is what is now blocking. See
`21-PHASE4-PHASE5-DECISION-SUPPORT.md`.
