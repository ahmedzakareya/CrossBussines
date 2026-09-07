# IMP-001 — Role Source Consolidation — Design

**Stage 2B gate. Design only — no migration executed, no production behaviour changed, no SQL against `CrossBuyDB2`.**

---

## 1. THE FINDING THAT REFRAMES THIS DESIGN

**`PlatformRoleAssignments` has no production writer. It is written only by tests.**

| Source | Production readers | Production **writers** | Real grant surface |
|---|---|---|---|
| **`PlatformRoleAssignments`** | `PlatformRoleDirectory`, `HrAccessService`, `ProjectsAccessService` | **NONE** — writes exist in exactly 4 **test** files | **none** |
| `AccountingUserRoles` | `AccountingAccessService` | `AccountingController` (add + remove) | real admin UI |
| `InventoryUserRoles` | `InventoryAccessService` | `InventoryController` (add + remove, **with `ScopeBranchId`**) | real admin UI |
| `CrmUserRoles` | `CrmAccessService` | `CrmController` (add) | real admin UI |
| `BranchUserRoles` | `PosAccessService`, `PosSetupService` | `PosSetupService` (add + remove) | real service |
| **`ProjectMembers`** | `ProjectsAccessService` | **NONE** — one test file only | **none** |

### 1.1 The consequence, which is a new CRITICAL risk

`HrAccessService` and `ProjectsAccessService` read **only** `PlatformRoleAssignments`. In production that table is
**empty and unfillable**. `PlatformRoleDirectory.AnyConfiguredAsync` therefore returns **false**, which means those
modules are permanently **bootstrap-open** in production.

**So HR and Projects authorization is open to every employee today**, except the two tiers `HrAccessService`
explicitly excludes from bootstrap-open (`payroll-manage`, `confidential-view`) and project `billing`, which delegates
to accounting.

**And it has a direct sequencing consequence for Wave 2:** Wave 2 gates 52 High-risk HR/identity endpoints on
`HrAccessService`. With no grant surface, **those gates would evaluate bootstrap-open and allow everyone** — Wave 2
would ship authorization that always says yes, and its tests would pass because they seed grants directly.

**Recorded as RISK-037 (Critical). IMP-001 must therefore deliver a grant writer, and it must land before Wave 2.**

### 1.2 What IMP-001 actually is

Not "migrate data between two populated systems". It is three things, in order:

1. **Build the missing grant surface** for `PlatformRoleAssignments` (service + audit + validation). Without it the
   shared table can never hold anything.
2. **Copy** the three consolidatable legacy sources into it (they are small, admin-maintained sets).
3. **Cut the readers over** per module, per company, with the legacy source retained.

This is a smaller data problem and a larger *missing-capability* problem than the risk register implied.

---

## 2. Complete role-source inventory, classified

The brief's ten categories, applied. **The separation matters more than the migration** — three of these must never
become role rows.

| # | Category | Sources | Migrate? |
|---|---|---|---|
| 1 | **Platform authorization grants** | `PlatformRoleAssignments` (scope + role + `ScopeBranchId` + validity + `IsActive`) | **target** |
| 2 | **Identity roles** | AspNet roles, read via `IsInRole` in **exactly one place** (`PlatformOpsAttribute`: `Admin`/`Administrator`/`SuperAdmin`/`PlatformOps`) | **No** — platform-operator identity, not a business grant. Surface it in the console, keep it separate |
| 3 | **Module role assignments** | `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles` | **Yes** — these three |
| 4 | **Branch-scoped POS roles** | `BranchUserRoles` (`PosRole`, `IsActive`, branch-owned) | **No** — sanctioned exception, §4 |
| 5 | **Business memberships** | `ProjectMembers`, `ConversationMembers` | **Never.** A membership is a business fact with its own lifecycle that *affects* access; it is not a grant |
| 6 | **Hierarchy-derived access** | `IOrgHierarchy` over `Hierarchical` | **No** — derived at query time, nothing to store |
| 7 | **Bootstrap-open** | `AnyConfiguredAsync` per company+scope | **No** — IMP-002's subject, but see §1.1 |
| 8 | **System-context policy** | `SystemContextPolicy`, `BusinessContext.IsSystem` | **No** — code policy |
| 9 | **Direct ownership** | `AssigneeEmployeeId`, `CreatedByEmployeeId`, CRM owner, `Conversation.CreatedByEmployeeId` | **No** — row data |
| 10 | **Legacy ad hoc** | `[SessionValidation]` on 257 mutating actions, `PosLaneActivityGuard` | **No** — not authorization; renaming is IMP-009 |

**Three sources migrate. Two are explicitly protected as business memberships. Five are not storage at all.**

---

## 3. Per-module migration mapping

### Accounting — straightforward
`AccountingUserRoles(CompanyID, EmployeeId, Role)` → `PlatformRoleAssignments(CompanyID, Scope="Accounting",
PrincipalType=Employee, PrincipalId=EmployeeId, Role, IsActive=1, SourceSystem="AccountingUserRoles")`.
Roles `ChiefAccountant`/`Accountant`/`Cashier`/`Auditor` are preserved **verbatim**; the action vocabulary
(`read`/`post`/`pay`/`manage`/`currency-override`) stays **inside `AccountingAccessService`**. Nothing about
`post`, `pay`, `manage` or `currency-override` changes.

### Inventory — the one with real scope semantics
`InventoryUserRoles` carries **`ScopeBranchId`**, used by `InventoryAccessService` for `WarehouseKeeper`
(`keeperScopes`). `PlatformRoleAssignments` **already has `ScopeBranchId`**, and `ModuleAccessServiceBase` already
honours it (`g.ScopeBranchId == null || g.ScopeBranchId == branchId`).

**So branch scope is representable with no model change** — the single most important compatibility fact in this
design. Migration maps `ScopeBranchId` one-to-one. **A scoped grant must never be flattened to company-wide**;
asserted by test.

### CRM — migrate the role, keep the scope logic
`CrmUserRoles` → assignments with roles `SalesManager`/`SalesRep`/`Marketing`/`CrmViewer` preserved.
**`VisibleOwnerIdsAsync` stays entirely in `CrmAccessService`** — owner/team/company breadth is *computed* from the
role plus `IOrgHierarchy`, not stored. Only the role row moves. Owner/team scope is never expressed as a role.

### HR — already native, but see §1.1
Reads `PlatformRoleAssignments` today with `HrRoles` (`HrManager`/`HrOfficer`/`PayrollOfficer`/`HrViewer`).
**No migration needed; a grant surface is.**

### Projects — already native; keep `ProjectMembers` separate
Company-level project roles (`ProjectsAdministrator`/`ProjectsFinance`/`ProjectsViewer`) live in the shared table.
`ProjectMembers` is per-project membership and **stays a business relationship**. The boundary: *a role says what you
may do across the company's projects; a membership says which project you are on.*

### Tasks — no migration
Native. `AccessScope` breadth (Own/Team/Company) is **computed** by `ResolveScopeAsync`, never stored. Consolidation
touches nothing.

### Communication — no migration
Native. `ConversationMembers` stays a business membership — Batch C.1 proved membership is the control that protects a
conversation, and turning it into role rows would put message-level access into role storage.

---

## 4. POS — assessed, and the exception is upheld

**`BranchUserRoles` must remain outside the shared table.** Not for neatness — for a mechanical reason:

POS lane login resolves rights **before a `BusinessContext` exists**. `PosAccessService.ResolveByUserIdAsync(userId)`
maps an AspNet user → employee → branch roles at login time, and the lane's identity then lives in a session blob
(`HyperCtx`/`PosCtx`). `PlatformRoleAssignments` is company-scoped and read through `PlatformRoleDirectory`, which
expects a resolved `BusinessContext` carrying a company.

Forcing POS into the shared table would require resolving a company **before** login has established one — a circular
dependency. `BranchUserRoles` is also **branch-owned, not company-owned** (`BranchId`, no `CompanyID`), so the company
would have to be derived from the branch at login, adding a lookup to the most latency-sensitive path in the product.

**Decision: retain as the documented sanctioned exception.** The console shows POS roles in a **separate view** (§6),
never merged into the platform-grant list. Revisit only if lane login is redesigned.

---

## 5. Coexistence — read precedence, and the trap avoided

**The rule: never union. Union broadens silently.**

Per **(company, scope)** a `RoleSourceCutover` flag selects **exactly one** authoritative source:

| Flag state | Reader behaviour |
|---|---|
| `Legacy` (default) | read the legacy table **only**; platform rows ignored |
| `Shadow` | read legacy **for the decision**; also read platform, **compare, log divergence**, decide by legacy |
| `Platform` | read `PlatformRoleAssignments` **only**; legacy ignored |

`Shadow` is where migration correctness is proven — it produces divergence evidence under real traffic while the
decision stays on the known-good source.

**The ten required answers:**

1. **Same grant in both?** No double effect — a decision is boolean and one source is authoritative.
2. **Sources disagree?** In `Shadow`, legacy wins and divergence is logged as a `RoleSourceDivergence` event. Cutover
   to `Platform` is **blocked while divergences are open**.
3. **Which wins?** Whichever the flag names. Never both.
4. **Can an old role revoke a new assignment?** No — in `Platform` the legacy row is not read. In `Legacy`/`Shadow` the
   platform row has no effect. Revocation is never cross-source.
5. **Per company?** Yes — the flag is keyed by company.
6. **Per module?** Yes — keyed by scope.
7. **Inactive employees?** Denied upstream of roles: `ForEmployeeAsync` returns null for an inactive employee, so no
   grant is ever consulted. Unchanged.
8. **Expired assignments?** `ValidFrom`/`ValidTo`/`IsActive` already honoured by `PlatformRoleDirectory`. Legacy tables
   have **no validity columns** — so migration sets `IsActive=1`, `ValidFrom=NULL`, and *gains* a capability rather than
   losing one.
9. **External principals?** `PrincipalType` already exists. `Employee` only for now; `ExternalUser`/`ServicePrincipal`
   remain **unused until a portal exists** — designing for them now would be designing for an unbuilt requirement.
10. **Audit?** Every write goes through the new grant service, which raises `RoleAssignment.Granted/Revoked` and stamps
    `CreatedBy`/`RevokedBy`. Legacy tables have no audit today; this is a gain.

**Unknown scope or role code must fail validation, not create a no-op grant** — `PlatformRoleDirectory` already throws
`ArgumentException` on an unknown scope, and the grant service must validate the role against the module's declared set.

---

## 6. Security Console data contract — seven separate views

The console must **never** merge these into one list; that is what would make it misleading.

| View | Content | Source |
|---|---|---|
| 1 **Assigned roles** | direct grants: company, scope, role, `ScopeBranchId`, validity, `IsActive`, `SourceSystem`, cutover state | `PlatformRoleAssignments` (+ legacy while in `Legacy`/`Shadow`) |
| 2 **Effective permissions** | per module+action allow/deny **with the reason** | the access services themselves — never recomputed in the console |
| 3 **Business memberships** | project and conversation membership, clearly labelled *not a role* | `ProjectMembers`, `ConversationMembers` |
| 4 **Bootstrap-open exposure** | which company+scope pairs are open and what that grants | `AnyConfiguredAsync` (IMP-002) |
| 5 **Identity roles** | AspNet platform-operator roles | `IsInRole` set |
| 6 **Branch POS roles** | `BranchUserRoles`, labelled as the sanctioned exception | `PosAccessService` |
| 7 **Conflicts & migration diagnostics** | divergences, unmapped legacy rows, duplicates, cutover state per company+scope | shadow-read log |

**View 2 must call the access services**, not re-implement their rules — otherwise the console becomes a second
permission engine that can disagree with production. A test asserts console output equals the access-service decision
for a matrix of principals.

DTOs are API-safe (`ApiPerm`, 401/403 JSON), and the console itself requires `PlatformOps` — **it is an attack map**.
Output is **permission-trimmed**: an administrator sees only companies they administer.

---

## 7. Migration plan (not executed)

Ten stages: inventory → mapping validation → duplicate analysis → **dry run** → **shadow read** → reconciliation →
controlled cutover (per company+scope) → legacy read disablement → legacy **writer** disablement → cleanup in a later
phase.

Per row: `SourceSystem`, source PK, `MigrationBatchId`, and a source hash for traceability. Properties: **additive
first · idempotent · resumable · auditable · reversible before cleanup · safe per company · safe per module.**

**Deployment order:** SQL (columns already exist) → grant service + validation → console read-only → shadow read per
company → reconcile → cutover → disable legacy readers → disable legacy writers (**the last irreversible step**).
**Legacy tables are not dropped in the first implementation phase.**

**Rollback:** flip the cutover flag back to `Legacy`. Because migration is additive and legacy rows are untouched,
rollback is a flag change with no data restore. **Trigger:** any divergence after cutover, or any authorization
regression report.

**Critically: the legacy admin UIs must stop writing at cutover**, or a grant added in `AccountingController` after
cutover would be invisible. That is the sharpest failure mode in this design — **RISK-038**.

---

## 8. Test plan (mandatory before implementation)

Legacy-only grant works pre-cutover · platform-only works post-cutover · equivalent duplicate does not double-authorize
· divergence blocks cutover · revoked denies · expired denies · inactive employee denies · company mismatch denies ·
**`ScopeBranchId` survives (WarehouseKeeper scoped to one branch is denied at another)** · CRM owner/team semantics
unchanged · `ProjectMembers` and `ConversationMembers` remain separate · **POS login still works** · console effective
decision **equals** the access-service decision · rollback restores legacy behaviour · **no cross-company widening** ·
no privilege widening in any coexistence state.

**Every test seeds real grants** and must not pass via bootstrap-open — and given §1.1, an HR/Projects test that
forgets to seed will pass vacuously in production configuration. A dedicated guard asserts the seeded-vs-unseeded
difference per module, exactly as Wave 1 did for accounting.

SQL Server migration verification on **isolated probe databases** (RISK-036 rule), carrying `[RequiredEvidence]` so it
cannot report skipped.

---

## 9. Delivery status — honest

| Required output | Status |
|---|---|
| `Stage-002-Role-Source-Consolidation-Design.md` | **Completed** (this document — inventory, per-module mapping, POS assessment, coexistence, migration, console contract, tests, deployment/rollback all present) |
| `Stage-002-EF-Relationship-Inventory.csv` | **Completed** — **generated** from the verified IMP-003 test (53 rows, 42 Cascade, 1 rejection flagged) |
| `Stage-002-Role-Source-Inventory.csv` | **Not Started** — content is §2; needs emitting as CSV |
| `Stage-002-Role-Migration-Matrix.csv` | **Not Started** — content is §3 |
| `Stage-002-Role-Coexistence-Plan.md` | **Completed in §5** — not split out; splitting would create two documents to keep in sync |
| `Stage-002-Security-Console-Data-Contract.md` | **Completed in §6** |
| `Stage-002-Role-Consolidation-Test-Plan.md` | **Completed in §8** |
| `Stage-002-Role-Consolidation-Deployment-and-Rollback.md` | **Completed in §7** |
| Updated Risk Register (MD + CSV) | **Not Started** — RISK-035/036/037/038 identified here and in IMP-003, not yet merged into the canonical generator |
| `Stage-002-IMP-001-Delivery-Report.md` | **Not Started** — superseded by this section |

**New risks raised by IMP-001:** **RISK-037 (Critical)** — `PlatformRoleAssignments` has no writer, so HR and Projects
are bootstrap-open in production and Wave 2's gates would always allow; **RISK-038 (High)** — legacy admin UIs writing
after cutover would create invisible grants.

**Completion gate: 15 of 18 met.** Outstanding: the three CSV artifacts and the risk-register merge. **No migration
executed, no production behaviour changed, no SQL against `CrossBuyDB2`, Stage 2A not started.**
