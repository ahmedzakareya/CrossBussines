# 20 — Findings Register

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


Severity reflects impact on Phase 4/5, not general code quality. "Blocks" means the phase cannot be
completed correctly without addressing it.

| ID | Sev | Area | Finding | Evidence | Impact | Owner | Blocks P4 | Blocks P5 |
|---|---|---|---|---|---|---|---|---|
| **F-01** | **CRITICAL** | Integration | The whole Task foundation is unmerged. `tasks/phase4` is 37 ahead / 0 behind `master` | `git rev-list --count master..tasks/phase4` = 37 | Nothing built in Phases 1–4 is in the integrated product. Every other tab integrates into `master` and cannot see it | Integration Owner | **YES** | **YES** |
| **F-02** | **CRITICAL** | Tasks / Events | Automated tasks raise no business event | `TaskService.cs:345` is the only `TaskCreatedAsync` caller; generator, doc-expiry and orchestration inject no publisher (0 refs each) | Automated work is invisible to Timeline, Notification and AI projections — precisely the data management control needs | TAB-1 | **YES** | **YES** |
| **F-03** | **HIGH** | Security | TM-2: `EntityRegistry` does not company-filter Employee, so `BelongsToCompanyAsync("Employee", …)` answers yes for any company | `PlatformPermissionProvider.cs:121`; `TaskEscalationService.cs:216` documents the workaround | Any consumer trusting it for Employee inherits a cross-tenant hole. One caller works around it; others may not | TAB-1 | no | **YES** |
| **F-04** | **HIGH** | Org | `Hierarchical` has no `CompanyID`; the org tree is global across 14 companies | `Models/Context/Admin/Hierarchical.cs` — full entity, 7 columns, none is company | Every manager walk is cross-tenant by construction; correctness depends on each caller re-filtering | TAB-1 | no | **YES** |
| **F-05** | **HIGH** | Tasks | Four creation doors, three bypassing `TaskService.SaveAsync`, enforcing different invariants | file 06 matrix | No single place guarantees lifecycle, assignment validity or eventing | TAB-1 | **YES** | **YES** |
| **F-06** | **HIGH** | Communication | `CommNotificationDispatcher` has no worker driving it | no hosted service references it; its own header says "pending production wiring" | Comm deliveries are claimed by nothing in production | TAB-3 | no | no |
| **F-07** | **HIGH** | Events | Accounting and Manufacturing event families are declared but no producer was located | `BusinessEventTypes.cs:84–176`; sweep of `BL/*Service.cs` found no raise site | If unproduced, the two highest-volume modules cannot drive orchestration at all | TAB-2 | **YES** | **YES** |
| **F-08** | **MEDIUM** | Accounting | Exception flows that are plainly work create no tasks | no Accounting service injects `ITaskService` | Period-close blockers, failed postings and overdue receivables are invisible as work | TAB-2 | no | **YES** |
| **F-09** | **MEDIUM** | Workers | `IWorkerGate` adoption verified for one of seven workers | `CrmReminderHostedService` only | Duplicate writes on multi-instance deployment for any worker lacking it | TAB-1 | no | **YES** |
| **F-10** | **MEDIUM** | Reporting | No Task dataset in Report Studio; `TaskManagementQueryService` wired to nothing | 5 dataset providers, none for Tasks; `Program.cs:683` is the only reference | Management control has no surface and no reportability | TAB-2 | **YES** | **YES** |
| **F-11** | **MEDIUM** | Security | 93 of 439 mutating actions unprotected | `engineering/authorization-baseline.json` | 21% of the write surface has no proven authorization | TAB-1 | no | no |
| **F-12** | **MEDIUM** | Isolation | `AccountingController` still pins `DefaultCompanyId = 1` (191 uses); `AdminController` (`HrCompanyId`), `InventoryApiController`, `BrandController`, `CurrencyController`, `HolidayService` likewise | file 15 table | Same class of defect already repaired in CRM and Inventory; Accounting is now the largest | TAB-2 | no | **YES** |
| **F-13** | **MEDIUM** | Approvals | Three unrelated approval mechanisms, no approval events, no task integration | file 09 | A pending approval is work the platform cannot see | TAB-3 | no | **YES** |
| **F-14** | **MEDIUM** | CRM | No CRM business events, no CRM entity codes in `EntityRegistry` | `BusinessEventTypes.cs` has no `Crm.*`; registry has module `"Crm"` only | CRM cannot drive orchestration and tasks cannot link back to CRM records | TAB-4 | no | **YES** |
| **F-15** | **LOW** | Tasks | `Task.Cancelled` is a reserved contract: event type, contract registration and notification kind exist; no state, no producer | `BusinessEventTypes.cs:188`; `TaskCalendarIntegrationContracts.cs:341`; `TaskStatuses.All` | Deliberate and documented in three places. A product decision, not a defect | Product | no | no |
| **F-16** | **LOW** | Escalation | 24 / 24 / 2 are technical defaults, not confirmed policy | `TaskEscalationTiming.cs:30` — "still PRODUCT POLICY DEBT" | Escalation cadence is unowned | Product | no | **YES** |
| **F-17** | **LOW** | Tasks | No employee *active-state* check on any assignment path | only `EmpCompanyID` is verified | Work can be assigned to a departed employee | TAB-1 | no | no |
| **F-18** | **INFO** | Repo | 772 dirty files, 18 worktrees, 3 stashes, 0 remotes | `git status`, `git worktree list` | Analysis must read committed refs; nothing is backed up off-machine | Integration Owner | no | no |
| **F-19** | **INFO** | Projects | Projects has **no** competing task concept | only `ProjectActivityType`, an accounting dimension | Projects is the cleanest candidate for Task rollout | TAB-6 | no | no |
| **F-20** | **INFO** | Documents | Document expiry was not migrated onto the orchestrator; verification was | file 09 | Two document→work paths coexist by design | TAB-3 | no | no |
