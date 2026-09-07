# Stage 1 Batch C — shared RBAC architecture proposal

**Status: PROPOSAL FOR REVIEW. Nothing implemented. No schema created. No SQL executed anywhere.**
Requested in response to the Batch C schema decision: *"investigate whether HR, Projects, Tasks and Communication
can share a unified RBAC foundation instead of each module owning an independent role table."*

Prerequisite reading: [Stage-001-Batch-C-Analysis.md](Stage-001-Batch-C-Analysis.md) — in particular §1.1 (no role
table exists for any of the three), §1.3 (no project membership exists), §1.5 (the hierarchy has no CompanyID).

---

## 1. The measurement that decides it

The four existing role tables, exactly as they are:

| Table | ID | Company | Principal | Role column | Extra scope | Active flag | Created |
|---|---|---|---|---|---|---|---|
| `AccountingUserRoles` | ✔ | `CompanyID` | `EmployeeId` | `Role` | — | — | `CreatedAt?` |
| `CrmUserRoles` | ✔ | `CompanyID` | `EmployeeId` | `Role` | — | — | `CreatedAt?` |
| `InventoryUserRoles` | ✔ | `CompanyID` | `EmployeeId` | `Role` | **`ScopeBranchId?`** | — | `CreatedAt?` |
| `BranchUserRoles` (POS) | ✔ | **none** | `EmployeeId` | **`PosRole`** | **`BranchId`** (required) | **`IsActive`** | `CreatedAt?` |

**Three of the four are byte-for-byte the same shape.** Inventory adds one nullable branch scope. POS is the only
genuine outlier — branch-keyed with no company, a differently-named role column, and an active flag — and it is
different **for a documented reason**: `PosAccessService.ResolveByUserIdAsync` reads it *before any BusinessContext
exists*, which is why B1 §2.2 classifies it `SecuritySensitive` and excludes it from query filtering.

**Conclusion: a unified model is not merely practical, it is the shape the codebase already converged on
independently three times.** Adding four more copies of the same five columns would be the fourth, fifth, sixth and
seventh repetition of a table that was never intentionally duplicated — it was copied.

## 2. Proposed model

### 2.1 One shared role assignment table

```
PlatformRoleAssignments
  ID              int identity PK
  CompanyID       int      NOT NULL     -- the tenant. Always present (POS's absence is not copied)
  Scope           nvarchar(40) NOT NULL -- IModuleAccessService.Scope: Hr | Projects | Tasks | Communication | …
  PrincipalType   nvarchar(20) NOT NULL -- Employee | CustomerContact | VendorContact | ServiceAccount
  PrincipalId     int      NOT NULL     -- Employee.ID today; a portal contact id later
  Role            nvarchar(60) NOT NULL -- module vocabulary: HrManager | PayrollOfficer | ProjectManager | …
  ScopeBranchId   int      NULL         -- optional narrowing, as InventoryUserRoles.ScopeBranchId already does
  IsActive        bit      NOT NULL DEFAULT 1
  ValidFrom       datetime2 NULL        -- optional temporal grant (delegation, cover, secondment)
  ValidTo         datetime2 NULL
  CreatedBy/At, UpdatedBy/At
  UNIQUE (CompanyID, Scope, PrincipalType, PrincipalId, Role, ScopeBranchId)
  INDEX  (CompanyID, Scope, PrincipalType, PrincipalId) INCLUDE (Role, ScopeBranchId, IsActive)
```

Four columns are **not** in any existing table, and each earns its place by a forward requirement rather than by
speculation:

| Column | Why now, not later |
|---|---|
| `Scope` | It is what makes the table shared. It is the string `IModuleAccessService.Scope` **already returns** — no new vocabulary. |
| `PrincipalType` / `PrincipalId` | **The single most expensive thing to retrofit.** Every existing table keys on `EmployeeId`; a Customer Portal contact, a supplier contact and a service account are *not* employees. Adding this later means altering the primary access path of every module at once. Adding it now costs one column and a constant. |
| `IsActive` | `BranchUserRoles` already has it; the other three cannot revoke a grant without deleting the row, which destroys the audit trail. |
| `ValidFrom`/`ValidTo` | Workflow delegation and holiday cover need "approve on X's behalf until Friday". Without it, that is modelled by inserting and later deleting rows — again destroying history. Nullable, so it costs nothing until used. |

### 2.2 Module-specific tables — only where the thing is a business relationship, not a role

The distinction the review asked for, applied:

| Table | Shared or module-specific | Why |
|---|---|---|
| `PlatformRoleAssignments` | **shared** | "which capabilities does this principal hold in this module" is the same question everywhere |
| **`ProjectMembers`** *(new)* | **module-specific** | *Who is on this project* is a **business relationship** with its own lifecycle, allocation and reporting meaning — not a permission. It is also the missing data that makes C4 implementable (Analysis §1.3). Shape: `CompanyID, ProjectId, EmployeeId, RoleOnProject (Manager\|Member\|Observer), AllocationPct?, JoinedAt, LeftAt?, IsActive` |
| `ConversationMember` *(exists)* | **module-specific** | Already a business relationship with `Role = Owner\|Member`, a read high-water mark and a mute setting. **Not touched.** |
| `Hierarchical` *(exists)* | **shared substrate** | The org tree already serves CRM and Leave. Reused, never duplicated — **but every read must be intersected with the caller's company**, because it has no `CompanyID` (Analysis §1.5). |
| `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles`, `BranchUserRoles` | **left exactly as they are** | See §4. |

**No `HrUserRoles`, `ProjectUserRoles`, `TaskUserRoles` or `CommUserRoles` is created.** Four tables become one, and
the one that remains module-specific (`ProjectMembers`) is the one that is genuinely not a role.

### 2.3 One read path, so the schema stops being an access-service concern

```csharp
public interface IPlatformRoleDirectory
{
    // The principal's roles in one module scope, company-intersected, active and in-date.
    Task<IReadOnlyList<RoleGrant>> RolesAsync(
        BusinessContext context, string scope, CancellationToken cancellationToken = default);

    // "Has any role been configured for this scope in this company?" — the bootstrap-open probe every
    // existing module already performs against its own table.
    Task<bool> AnyConfiguredAsync(int companyId, string scope, CancellationToken cancellationToken = default);
}
public sealed record RoleGrant(string Role, int? ScopeBranchId);
```

The four new access services depend on **this**, never on a `DbSet`. That is what makes §4's migration a data move
instead of a code change, and it is where the company intersection and the `ValidFrom/ValidTo` filter live **once**
rather than four times.

### 2.4 The seam that Unified Inbox, Search and AI actually need

`CanAsync(context, action, target)` answers one record at a time. **An inbox over 50 000 tasks cannot ask 50 000
questions**, and if it re-implements the rules in a query predicate the rules will diverge — which is exactly how the
~1 100 hand-written `CompanyID` predicates happened.

So the proposal adds a **set-shaped** companion to the yes/no method:

```csharp
public sealed record AccessScope(
    AccessBreadth Breadth,                 // None | Own | Team | Branch | Company | CrossCompany
    IReadOnlyCollection<int>? PrincipalIds, // for Team: the company-intersected hierarchy walk, resolved ONCE
    int? BranchId);

Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action, CancellationToken ct = default);
```

A consumer translates one `AccessScope` into one `WHERE` clause. Same rules, one evaluation, no N+1, and the
translation is the only thing a new consumer writes. **Not built in Batch C** beyond the contract plus the Tasks
implementation needed by its own tests — but designing it now is what keeps Unified Inbox from re-deriving
authorization later.

## 3. How each future capability is served

| Capability | What it needs | How this model provides it |
|---|---|---|
| **Workflow / Approval** | "who may approve step N", delegation, cover | `Role` + `ValidFrom/ValidTo` for delegation; the existing `LeaveWorkflowService` approver chain stays the record-level rule; no workflow-specific role table |
| **Action Governance / Escalation** | escalate to the approver's manager | `Hierarchical` walk, company-intersected, already shared |
| **Business Workspace** | one "what may this actor see here" answer per entity | `IPlatformPermissionProvider` + `PermissionTarget` (the additively-extended adapter base) |
| **Unified Work Inbox** | cross-module *set* filtering | `ResolveScopeAsync` → one predicate per module (§2.4) |
| **AI Context / Copilot / Memory** | the same set filtering **plus** visibility tiers | `ResolveScopeAsync` + the existing `BusinessEventVisibility` tiers the adapters already map |
| **Enterprise Search** | index-time and query-time trimming | `AccessScope` is serialisable into a filter; `Scope` + `PrincipalType` make an ACL row expressible |
| **External Collaboration / Customer & Supplier Portals** | **non-employee principals** | `PrincipalType = CustomerContact \| VendorContact` — the reason that column is proposed now (§2.1) |
| **Industry packs (Hospital, Hotel)** | new modules and new roles without schema change | A pack ships **rows**: new `Scope` values and new `Role` values, plus its own access service. **No DDL.** A hospital "Ward Nurse" or a hotel "Front Desk" grant is data |
| **SaaS enablement** | tenant above company | `CompanyID` stays the tenant key; `BusinessContext.TenantId` already exists and is reserved. One nullable column later, no model change |

Two of these are the real justification for a shared table: an **industry pack cannot ship DDL per module**, and a
**portal principal is not an employee**. Per-module role tables make both of them a migration.

## 4. Migration strategy — three phases, and Batch C is only the first

**Phase 1 — Batch C (additive, zero risk to working code).**
`PlatformRoleAssignments` + `ProjectMembers` created by idempotent scripts in `deploy/sql`, **not executed**. Only the
four **new** modules read them, through `IPlatformRoleDirectory`. The four existing tables and their services are
**not touched**. Every new service is **bootstrap-open per company** — dormant until at least one row exists for that
scope — exactly as `AccountingAccessService.AnyRoleConfiguredAsync` already behaves. **Nothing that works today
changes behaviour.**

**Phase 2 — later, optional: fold in Accounting, Inventory, CRM.**
Because those three are the identical shape, the fold-in is a data copy (`INSERT … SELECT` with
`Scope='Accounting'`, `PrincipalType='Employee'`) plus repointing their `RolesAsync` at `IPlatformRoleDirectory`.
Dual-read first (union of legacy table and shared table), verify equality on real data, then stop reading the legacy
table, then drop it. **Not proposed for Batch C** — it changes the authorization path of three working modules and
deserves its own batch and its own acceptance.

**Phase 3 — POS: probably never, and that is a decision, not an omission.**
`BranchUserRoles` is read *before* a `BusinessContext` exists and has no `CompanyID` by design. Folding it in would
mean either giving it a company (a semantic change to POS login) or making the shared table's `CompanyID` nullable
(weakening it for everyone). Recommendation: **leave POS alone permanently** and document `BranchUserRoles` as the one
sanctioned exception, the same way `StockBalance` is the sanctioned exception to query filtering.

## 5. Compatibility impact

| Area | Impact |
|---|---|
| Accounting / Inventory / CRM / POS access services | **None.** Not modified, not read differently, tables untouched. |
| Existing 168 permission attributes | **None.** They call the legacy services, unchanged. |
| Existing views and `MainMenu` gating | **None.** No signature changes. |
| The four **new** modules | New restrictions, but **bootstrap-open**: with no rows configured, behaviour is identical to today (Analysis §1.2 — they have no authorization at all today). |
| `ModulePermissionAdapterBase` | Additively extended to forward `PermissionTarget` (default `null`), so all five existing adapters behave identically — with regression tests proving it, as instructed. |
| Deployment | Two idempotent scripts, reviewed before anyone runs them. No backfill, no data change, no `ALTER` on an existing table. |
| Rollback | Drop two unused tables; revert additive code. Nothing to undo in data. |

## 6. Risks of this design, stated plainly

1. **One table becomes the single point of authorization for every future module.** A defect in
   `IPlatformRoleDirectory` is a defect everywhere. Mitigation: it is small, has one query, and gets the heaviest test
   coverage in the batch — including the company-intersection case that the `Hierarchical` shape makes easy to get
   wrong.
2. **`Scope` is a string.** A typo (`"Hr"` vs `"HR"`) silently grants nothing. Mitigation: `Scope` values come from the
   existing `EntityRegistry.Scope*` constants and are validated against the registered `IModuleAccessService.Scope`
   set — an unknown scope must **throw at startup**, not deny quietly at runtime.
3. **`PrincipalType` is unused on day one.** It is a column carrying only `'Employee'` until a portal exists. That is
   the deliberate cost of not retrofitting it later (§2.1).
4. **Two models coexist through Phase 1/2.** Legacy tables for four modules, the shared table for four others. That
   is genuinely two things to understand. Mitigated by `IPlatformRoleDirectory` being the *only* read path for the new
   ones and by Phase 2 having a defined end state — but it is a real, temporary inconsistency and should be called
   what it is rather than described as unified.
5. **`ResolveScopeAsync` could drift from `CanAsync`.** Two methods answering the same question in different shapes is
   exactly how the `~1 100` hand-written predicates diverged. Mitigation: implement `CanAsync` **in terms of**
   `ResolveScopeAsync` wherever the action is set-shaped, and test that the two agree on the same fixture.

## 7. What I propose to implement in Batch C, if this is approved

1. `PlatformRoleAssignments` + `ProjectMembers` — idempotent scripts in `deploy/sql`, **not executed**.
2. `IPlatformRoleDirectory` + implementation (company-intersected, active, in-date, bootstrap probe).
3. Four access services on the vocabularies in Analysis §9 — session-free, `BusinessContext`-explicit, deny-by-default.
4. Record-level rules **only where data supports them**: HR self + approver chain; Projects via `ProjectMembers`;
   Tasks via assignee/creator/hierarchy/linked-entity; Communication via `ConversationMember`.
5. Four adapters + the additive `PermissionTarget` forwarding in the shared base, with the regression tests specified.
6. `ResolveScopeAsync` contract, implemented for Tasks only (the C10 matrix needs it; the other three return the
   breadth their rules imply).
7. Four module attributes, applied to the **C9 proof endpoints only**.
8. The C10 test matrix, the C11 backlog reclassification, four ADRs, and the maturity record.

## 8. Decision requested

1. **Approve the shared `PlatformRoleAssignments` + module-specific `ProjectMembers`** as proposed — or tell me to
   keep per-module role tables after all.
2. **Confirm Phase 2 (folding in Accounting/Inventory/CRM) is explicitly out of scope for Batch C**, and Phase 3
   (POS) is a permanent exception.
3. **Confirm the four extra columns** (`Scope`, `PrincipalType`/`PrincipalId`, `IsActive`, `ValidFrom`/`ValidTo`) are
   wanted now rather than retrofitted.
4. **Confirm the `AccessScope` / `ResolveScopeAsync` seam** should be introduced now (contract + Tasks
   implementation) rather than deferred to the Unified Inbox stage.

No schema will be written to `deploy/sql`, and no code will be changed, until this is reviewed.
