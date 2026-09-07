# IMP-001 — Platform Grant Writer — Design

**Design only. Not implemented. No migration executed. No SQL against `CrossBuyDB2`.**

The missing capability that reframed IMP-001. `PlatformRoleAssignments` has **4 production readers and 0 production
writers** — writes exist only in 4 test files. This designs the writer.

**Blocking:** Wave 2 · Security Administration Console · role migration cutover · bootstrap-open removal in HR and
Projects.

---

## 1. Why this must precede Wave 2 (RISK-037, Critical)

`HrAccessService` and `ProjectsAccessService` read **only** `PlatformRoleAssignments`. In production it is empty and
unfillable, so `AnyConfiguredAsync` returns false and both modules sit in **bootstrap-open**.

Wave 2 gates **52 High-risk HR/identity endpoints** on `HrAccessService`. Without a writer those gates evaluate
bootstrap-open and **allow everyone**, while Wave 2's tests pass because they seed grants directly.

**A gate over an unfillable grant store is not a gate.** The writer is therefore a Wave 2 prerequisite, not a
convenience.

---

## 2. Contracts

Names follow the codebase's existing conventions (`I…Service`, `…Result`, `Async` suffix, `record` DTOs).

```
IPlatformGrantWriter
    Task<GrantResult> GrantAsync   (GrantCommand cmd, CancellationToken ct)
    Task<GrantResult> ReviseAsync  (ReviseValidityCommand cmd, CancellationToken ct)
    Task<GrantResult> RevokeAsync  (RevokeCommand cmd, CancellationToken ct)
    Task<GrantResult> ReactivateAsync (ReactivateCommand cmd, CancellationToken ct)

IPlatformGrantReader                      // read side kept separate: the console needs reads without write rights
    Task<IReadOnlyList<GrantView>> ListAsync (GrantQuery q, CancellationToken ct)
    Task<GrantView?>               GetAsync  (int assignmentId, CancellationToken ct)
```

```
record GrantCommand(
    int CompanyId, string Scope, string RoleCode,
    string PrincipalType, int PrincipalId,
    int? ScopeBranchId, DateTime? ValidFrom, DateTime? ValidTo,
    string? Reason, string IdempotencyKey)

record RevokeCommand(int AssignmentId, string? Reason, string IdempotencyKey)
record ReviseValidityCommand(int AssignmentId, DateTime? ValidFrom, DateTime? ValidTo, string IdempotencyKey)

record GrantResult(bool Ok, int? AssignmentId, GrantFailure Failure, string? Reason)

enum GrantFailure {
    None, Unauthorized, ContextUnresolved, UnknownScope, UnknownRoleCode,
    CompanyNotFound, PrincipalNotFound, PrincipalWrongCompany, PrincipalInactive,
    InvalidValidityRange, DuplicateActiveGrant, BranchNotInCompany,
    AlreadyRevoked, NotFound, Conflict }
```

| Aspect | Rule |
|---|---|
| **Authorization** | Every method authorizes the **acting administrator** first (§4). Fails **before** validation, so an unauthorized caller learns nothing about scope or role validity. |
| **Company semantics** | `CompanyId` resolved via `IRequestCompanyResolver` (CORRECTION-005). A request-supplied company is **compatibility only**, validated, mismatch **refused not coerced**. |
| **Branch semantics** | `ScopeBranchId` must belong to `CompanyId`. A branch grant **never** widens to company-wide. |
| **Transaction** | Owns its own `ScopedTx`. The event is recorded **in-transaction immediately before commit with no swallowing catch** (ADR-001). |
| **Idempotency** | `IdempotencyKey` unique per (company, scope, principal, role, key). A replay returns the **original** `AssignmentId` with `Ok = true` and writes nothing. |
| **Failure** | Returns `GrantResult`; never throws for a business rejection. API surface uses `ApiPerm` → **401/403 JSON, never an MVC redirect**. |
| **Audit** | `CreatedBy`/`RevokedBy` from the resolved `BusinessContext` — never a posted id. |
| **Events** | `RoleAssignment.Granted` / `.Revoked` / `.ValidityChanged` via `RecordAsync`. |
| **Notification** | Only on **revocation of a privileged role**; a grant notification per row would be noise. |

---

## 3. Domain rules — all twenty, mapped to a failure code

| # | Rule | Failure |
|---|---|---|
| 1 | Unknown `Scope` rejected — validated against `EntityRegistry` scopes | `UnknownScope` |
| 2 | Unknown `RoleCode` rejected — validated against the **module's own declared role set** | `UnknownRoleCode` |
| 3 | Company must exist | `CompanyNotFound` |
| 4 | Employee principal must exist | `PrincipalNotFound` |
| 5 | Employee must belong to the assignment company (no cross-company policy exists today) | `PrincipalWrongCompany` |
| 6 | Inactive employee cannot receive a new effective grant | `PrincipalInactive` |
| 7 | `ValidTo` ≥ `ValidFrom` | `InvalidValidityRange` |
| 8 | Duplicate **active** grant rejected (same company+scope+role+principal+branch) | `DuplicateActiveGrant` |
| 9 | Revocation sets `IsActive=0`, `RevokedAt`, `RevokedBy` — **never hard-deletes** | — |
| 10 | Revoked grants cannot authorize | enforced by `PlatformRoleDirectory` |
| 11 | Expired grants cannot authorize | already enforced |
| 12 | A branch grant cannot silently widen to company scope — changing `ScopeBranchId` to null is **revoke + new grant** | `Conflict` |
| 13 | Inventory branch scope exact — `ScopeBranchId` copied one-to-one | asserted by test |
| 14 | CRM owner/team breadth stays in `CrmAccessService` — never storable | writer rejects any breadth field |
| 15 | Project membership not writable here | `UnknownScope` for a membership-shaped write |
| 16 | Conversation membership not writable here | same |
| 17 | POS `BranchUserRoles` outside this writer | scope `POS` **not accepted** |
| 18 | Bootstrap-open not settable via a grant row (IMP-002 owns it) | `UnknownScope` |
| 19 | System-context rights not creatable as employee grants — `PrincipalType=System` rejected | `UnknownScope` |
| 20 | **Fails closed when `BusinessContext` is unresolved** | `ContextUnresolved` |

Rules 14–19 are all "this writer refuses to be the wrong tool" — the single most important property, because a writer
that accepts a membership or a POS role would silently undo the separation IMP-001 exists to establish.

---

## 4. Administration authorization — five distinct administrators

**No broad generic admin check.** A module administrator must never gain platform-wide or cross-company granting.

| Capability | Required right |
|---|---|
| View grants (own company) | `PlatformOps` **or** module `manage` for that scope |
| **Assign / revoke a module role** | module `manage` for **that scope in that company** — e.g. `AccPerm("manage")` grants accounting roles only |
| **Assign a `Platform`-scope role** | `PlatformOps` **only** — never a module admin |
| Change validity | same right as assign for that scope |
| View effective permissions | `PlatformOps` or module `manage` |
| View bootstrap-open exposure | `PlatformOps` (it is an exposure map) |
| **Manage migration state / cutover flags** | `PlatformOps` **only** |
| View divergence diagnostics | `PlatformOps` |

| Administrator | Scope of power |
|---|---|
| **Identity administrator** | AspNet roles only — cannot grant business roles |
| **Platform security administrator** (`PlatformOps`) | all scopes, own companies, cutover flags |
| **Module administrator** | one scope, one company — the common case |
| **Company administrator** | all scopes within one company; **cannot** grant `Platform` scope |
| **Branch POS administrator** | `BranchUserRoles` only, via `PosSetupService` — outside this writer |

**Escalation guard:** no administrator may grant a role that would exceed their own rights — asserted by test, because
self-escalation through a grant screen is the classic failure of role-management UIs.

---

## 5. Storage behaviour, and the additive schema change required

Existing columns: `CompanyID`, `Scope`, `PrincipalType`, `PrincipalId`, `Role`, `ScopeBranchId`, `IsActive`,
`ValidFrom`, `ValidTo`, `CreatedAt`, `CreatedBy`.

**The current schema cannot support the required audit behaviour.** Missing: `RevokedAt`, `RevokedBy`,
`SourceSystem`, `MigrationBatchId`, `IdempotencyKey`, `UpdatedAt`/`UpdatedBy`, `Reason`.

**Proposed additive change** (not executed): add those eight nullable columns, plus

* a **filtered unique index** on `(CompanyID, Scope, Role, PrincipalType, PrincipalId, ScopeBranchId)
  WHERE IsActive = 1` — enforcing rule 8 in the database, not only in code;
* a unique index on `IdempotencyKey` where not null.

Both need `sqlcmd -I` (`QUOTED_IDENTIFIER ON`) — the project already has 6 filtered indexes and that deploy rule.
All SQL **additive, idempotent, migration-safe, rollback-documented**; revoked rows are **retained indefinitely**
(history), and nothing is hard-deleted.

---

## 6. Shadow divergence contract

Shadow mode **decides on legacy** and evaluates platform in parallel, recording differences. It can never broaden
access, because the platform answer is not used.

| Divergence | Severity | Blocks cutover? | Remediation |
|---|---|---|---|
| `MissingPlatformGrant` | High | **Yes** | migrate the missing row |
| `ExtraPlatformGrant` | **Critical** | **Yes** — it would widen at cutover | revoke it |
| `ScopeMismatch` (branch vs company) | **Critical** | **Yes** | correct `ScopeBranchId` |
| `RoleMappingMismatch` | High | **Yes** | fix the mapping row |
| `ExpiredGrantMismatch` | Medium | No | expected: legacy has no validity |
| `RevocationMismatch` | High | **Yes** | re-revoke on the platform side |
| `PrincipalMismatch` | High | **Yes** | investigate |
| `CompanyMismatch` | **Critical** | **Yes** | investigate immediately |
| `BranchMismatch` | **Critical** | **Yes** | correct branch |
| `BootstrapOpenMaskedDifference` | High | **Yes** | **a difference invisible because bootstrap-open allowed both** — must be resolved before cutover, or migration defects hide behind it |
| `LegacyWriterAfterCutover` | **Critical** | post-cutover alarm | disable the writer (§7) |

**Rule: any authorization-widening divergence blocks cutover.** `BootstrapOpenMaskedDifference` is the subtle one and
the reason IMP-002 and IMP-001 interlock — a company in bootstrap-open cannot prove its migration is correct.

Each divergence records: company, scope, principal, legacy decision, platform decision, both source rows, the reason
strings, and a correlation id. Surfaced in console view 7 (§7 of the consolidation design).

---

## 7. Legacy writer shutdown

Inventoried in `Stage-002-Legacy-Role-Writer-Shutdown-Matrix.csv` — **7 writers** across 4 files, with per-mode
behaviour for all three cutover states.

**RISK-038 (High):** at `Platform` cutover a legacy admin UI that keeps writing produces a grant the authoritative
reader ignores, while the screen reports success. **An admin UI must never report success after writing to an ignored
source.** Each of W-001…W-005 therefore either **redirects to the Grant Writer** or is **explicitly disabled with a
user-facing message**. W-006 (DevSeed) must switch to the platform table or dev data diverges from production
authority. W-007 (POS) is never disabled — excluded by design.

---

## 8. Test plan additions (all 23 mandated cases)

Authorized administrator grants · unauthorized cannot · company mismatch denied · branch mismatch denied · unknown
scope rejected · unknown role rejected · duplicate active rejected · revoked history preserved · expired denied ·
inactive employee denied · validity range enforced · **idempotent command does not duplicate** · audit actor recorded ·
BusinessEvent emitted · **failed grant leaves no partial row** · revoked no longer authorizes · **console decision
equals access-service decision** · shadow divergence recorded · **cutover blocked by widening divergence** · legacy
writer blocked after cutover · rollback restores legacy authority · **bootstrap-open cannot make a migration test pass
falsely** · **no administrator can grant beyond their own rights**.

**SQL Server disposable databases** for: the filtered unique index, validity constraints, transaction rollback,
migration, and concurrent grant attempts. Carrying `[RequiredEvidence]` so they **cannot report skipped** (RISK-026),
and using **isolated probe databases** (RISK-036).

The bootstrap-open guard is not optional here: given §1, an HR test that forgets to seed grants passes vacuously in
production configuration.

---

## 9. Formal IMP-001 redefinition

```
1. Grant Management Capability   ← this document; blocks Wave 2
2. Legacy Role Migration        ← 3 sources, 10 eligible role mappings
3. Authoritative Cutover        ← per company + scope; blocked by widening divergence
4. Legacy Writer Shutdown       ← 7 writers; RISK-038
```

**New risk from this design — RISK-039 (High):** a grant screen that lets an administrator assign a role exceeding
their own rights is a self-escalation path. Mitigated by the §4 escalation guard and its test.

---

## 10. Delivery status

| Output | Status |
|---|---|
| `Stage-002-Platform-Grant-Writer-Design.md` | **Completed** (this document) |
| `Stage-002-Role-Source-Inventory.csv` | **Completed** — generated, 13 rows, validated |
| `Stage-002-Role-Migration-Matrix.csv` | **Completed** — generated, 18 rows (10 Eligible, 4 Already-Native, 4 Excluded) |
| `Stage-002-Legacy-Role-Writer-Shutdown-Matrix.csv` | **Completed** — generated, 7 writers |
| `Stage-002-EF-Relationship-Inventory.csv` | **Accepted** — verified 53 rows / 42 Cascade / 1 rejection / action present |
| Shadow divergence contract | **Completed in §6** |
| Security Console contract update | **Completed** — 7 views in the consolidation design §6; decision-explanation vocabulary in §11 there |
| Test plan update | **Completed in §8** |
| Risk Register merge (RISK-035…039) | **Not Started** — identified, not yet merged into the canonical generator |
| `Stage-002-IMP-001-Final-Delivery-Report.md` | **Not Started** — superseded by this section and the consolidation design §9 |

**Gate: 18 of 20 met.** Outstanding: the canonical risk-register merge (RISK-035/036/037/038/039) and the separate
final delivery report. No migration executed, no production behaviour changed, no SQL against `CrossBuyDB2`,
Stage 2A not started.
