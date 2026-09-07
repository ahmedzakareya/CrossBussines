# ADR-026 — One shared platform RBAC model

**Status:** Accepted (Stage 1 Batch C)
**Date:** 2026-08-04
**Supersedes:** the per-module role-table pattern for NEW modules. It does not change any existing module.
**Depends on:** ADR-010 (session-free permission evaluation), ADR-022 (BusinessContext), ADR-023 (bypass).
**Proposal reviewed and approved before implementation:** `Stage-001-Batch-C-RBAC-Architecture-Proposal.md`.

---

## 1. The measurement that decided it

| Table | Company | Principal | Role column | Extra scope | Active | Created |
|---|---|---|---|---|---|---|
| `AccountingUserRoles` | `CompanyID` | `EmployeeId` | `Role` | — | — | `CreatedAt?` |
| `CrmUserRoles` | `CompanyID` | `EmployeeId` | `Role` | — | — | `CreatedAt?` |
| `InventoryUserRoles` | `CompanyID` | `EmployeeId` | `Role` | `ScopeBranchId?` | — | `CreatedAt?` |
| `BranchUserRoles` (POS) | **none** | `EmployeeId` | **`PosRole`** | `BranchId` (required) | `IsActive` | `CreatedAt?` |

**Three of the four are the same shape.** They were copied, not designed. Adding `HrUserRoles`,
`ProjectUserRoles`, `TaskUserRoles` and `CommUserRoles` would have been the 4th–7th repetition.

## 2. Decision

**One table, `PlatformRoleAssignments`**, keyed by `(CompanyID, Scope, PrincipalType, PrincipalId, Role,
ScopeBranchId)`, read **only** through `IPlatformRoleDirectory`.

Four columns are not in any existing table, and each earns its place:

| Column | Why now |
|---|---|
| `Scope` | What makes the table shared. It is the string `IModuleAccessService.Scope` **already returns** — no new vocabulary. An industry pack (Hospital, Hotel) then ships **rows**, never DDL. |
| `PrincipalType` / `PrincipalId` | **The expensive retrofit.** Every existing table keys on `EmployeeId`; a Customer Portal contact is not an employee. Adding it later means altering the primary access path of every module at once. |
| `IsActive` | Revocation without deleting the row. `BranchUserRoles` already has it; the other three destroy the audit trail to revoke. |
| `ValidFrom`/`ValidTo` | Holiday cover and secondment. Nullable, so it costs nothing until used. **Not a delegation record** — see §6. |

## 3. What stays module-specific, and why

| | Placement | Reason |
|---|---|---|
| `PlatformRoleAssignments` | **shared** | "which capabilities in this module" is the same question everywhere |
| **`ProjectMembers`** (new) | **module-specific** | *Who is on this project* is a **business relationship** with its own lifecycle (`JoinedAt`/`LeftAt`), allocation and reporting meaning. It is not a permission, and per-project rows have no business in the table every module reads on every request. |
| `ConversationMember` | **module-specific, untouched** | Already exactly that: `Role = Owner\|Member`, a read high-water mark, a mute setting. |
| `Hierarchical` | **shared substrate, reused** | Already serves CRM and Leave. Reused via `IOrgHierarchy` — **with the company intersection it never had** (§5). |

## 4. What Batch C deliberately did NOT do

* **No migration of `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles`.** That changes the authorization
  path of three working modules and needs its own dual-read batch. Their services are untouched.
* **POS stays out, permanently.** `BranchUserRoles` is read *before* any `BusinessContext` exists and has no
  `CompanyID` by design (B1 §2.2). Folding it in would mean giving POS login a company semantic it lacks, or making
  `CompanyID` nullable here and weakening the isolation key for everyone. It is a **sanctioned permanent
  exception**, like `StockBalance` is to query filtering — revisitable only by a dedicated POS architecture review.
* **No field-level permissions.** No field columns exist on the table: field security needs serialization, UI
  masking, API response shaping, Search and AI decisions first. Planned, not guessed at.
* **No Role Templates or Role Groups.** The schema does not prevent them (they would be rows or a thin grouping
  table referencing `Role` + `Scope`); nothing is built. Planned.

## 5. `IOrgHierarchy` — a latent cross-company leak, closed for new code

`Hierarchical` has **no `CompanyID`**. Two existing implementations walk it —
`CrmAccessService.TeamOwnerIdsAsync` and `LeaveWorkflowService` — and **neither intersects the result with the
caller's company**, because on a single-company install the difference is invisible. On a multi-company install a
manager whose org node sits above a node belonging to company 2 would reach company 2's records.

`IOrgHierarchy` reuses the verified, cycle-guarded walk and adds the intersection: every returned employee is
confirmed against `Employee.EmpCompanyID`, and a dropped cross-company node is logged at **Warning** (a silent trim
would hide the very thing being guarded).

**The two existing callers are NOT repointed.** Doing so would change working CRM and Leave authorization inside a
batch that is adding new modules. Recorded as a Batch D item and in Architecture Risks — carried openly, not fixed
quietly.

## 6. Honest limits of what the columns prove

* `ValidFrom`/`ValidTo` give temporal **validity**. They are **not** a delegation record: there is no
  `DelegatedFrom`/`To`, reason, approver, revocation or audit trail. A Delegation Framework is Planned.
* `PrincipalType` carries only `'Employee'` today, and `IPlatformRoleDirectory` filters to it in SQL. An
  unsupported kind is therefore **not returned** rather than evaluated by a service with no rule for it — asserted
  by a test, so the day a portal principal is supported the change is deliberate.
* **Two models coexist**: legacy tables for four modules, the shared table for four others. That is a real,
  temporary inconsistency. It is mitigated by `IPlatformRoleDirectory` being the only read path for the new ones,
  and by §4's defined end state — but it should be called what it is rather than described as "unified".

## 7. Startup validation

`PermissionScopeStartupValidator` **fails boot** when a registered `IModuleAccessService.Scope` is not in
`EntityRegistry.PermissionScopes`, or when two modules claim the same scope. An unknown scope could never match a
role assignment, so the module would sit **silently bootstrap-open forever**; a duplicate scope would make
`FirstOrDefault(m => m.Scope == …)` resolve one module's policy for another. Both must be boot failures, not
runtime surprises. `IPlatformRoleDirectory` likewise **throws** on an unknown scope rather than answering "nothing
granted" — under bootstrap-open, "nothing granted" reads as "leave the module wide open".

## 8. Deployment

`deploy/sql/platform_role_assignments.sql` and `deploy/sql/project_members.sql` — additive, idempotent, no
backfill, no `ALTER` on an existing table. **Verified twice** on a disposable SQL Server database (identical
results both passes) plus **13 constraint tests**; the scratch database was dropped. Registered in the manifest with
SHA-256 hashes. **Neither script has been executed against CrossBuyDB2 or any other real database.**

## 9. Tests

`BatchCAccessServiceTests` (70) · `BatchCAdapterForwardingTests` (41) · 2 DI-container tests in `Stage1DiWiringTests`. Company intersection, active/inactive,
future/expired, branch-scoped, unknown scope, unsupported principal type, bootstrap-open per company **and** per
scope, and the cross-company hierarchy case. Suite: **606 passed / 0 failed / 0 skipped on SQL Server.**