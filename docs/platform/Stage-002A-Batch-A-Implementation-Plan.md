# Stage 2A — Batch A — Implementation Plan (A1 inspection findings)

Written **before** any code change, as A1 requires. Records what the live source tree actually contains and where it
diverges from the frozen design.

---

## 1. What exists today

| Artefact | State |
|---|---|
`Models/Context/Platform/PlatformRoleAssignment.cs` | entity exists; comment states *"nothing writes it in Batch C"* |
`CrossDbContext.PlatformRoleAssignments` | DbSet exists, mapped, indexed |
`deploy/sql/platform_role_assignments.sql` | table + 5 CHECK constraints + 5 indexes, additive and idempotent |
`IPlatformRoleDirectory` / `PlatformRoleDirectory` | **the only permitted reader**; company-intersected, active + in-date, one UTC clock |
`ModuleAccessServiceBase` | 7 gates, then `EvaluateAsync`; computes `bootstrapOpen` |
`HrAccessService` · `ProjectsAccessService` · `TasksAccessService` · `CommunicationAccessService` | native readers of the grant store |
`PlatformPermissionProvider` | scope adapters over `IModuleAccessService` |
`BusinessContext` | `required int CompanyId`, `int? EmployeeId`, `Source`, `IsSystem` |
`IBusinessEventService.RecordAsync` | validates `EntityCode` against `EntityRegistry` (**throws** on unknown) and the event name |
`ScopedTx.BeginOrJoinAsync` | ambient-transaction helper |

**Confirmed: `PlatformRoleAssignments` has no production writer.** RISK-037 is real and unmitigated.

## 2. Findings that change the plan's assumptions

### F1 — The filtered unique index already exists, with exactly the required key

A2 asks to *add* it. It is already there:

```sql
CREATE UNIQUE INDEX UX_PlatformRoleAssignments_ActiveGrant
    ON dbo.PlatformRoleAssignments (CompanyID, Scope, PrincipalType, PrincipalId, Role, ScopeBranchId)
    WHERE IsActive = 1;
```

That matches the brief's effective uniqueness key exactly, including `ScopeBranchId` and `IsActive = 1`. Batch A must
therefore **verify** it, not create it — creating a second index with the same key would be a duplicate. The Batch A
SQL asserts its presence and definition instead.

### F2 — The column is `Role`, not `RoleCode`

The brief allows "Role or GrantCode". The live column is `Role nvarchar(60)`. Batch A uses `Role` and does not rename
anything.

### F3 — Two of the eight audit fields already exist

Present: `CreatedBy`, `CreatedAt`, `UpdatedBy`, `UpdatedAt`.
**Missing, to be added:** `RevokedAt`, `RevokedBy`, `SourceSystem`, `MigrationBatchId`, `IdempotencyKey`, `Reason`.

### F4 — Business event names are constrained by the kernel and cannot be `PlatformGrantCreated`

`BusinessEventTypes.TryValidate` enforces `<EntityCode>.<Action>` with a PascalCase action, and `RecordAsync` rejects
an `EntityCode` that is not in `EntityRegistry`. The brief's conceptual names therefore map as:

| Brief | Actual event name |
|---|---|
`PlatformGrantCreated` | `PlatformRoleAssignment.Created` |
`PlatformGrantRevoked` | `PlatformRoleAssignment.Revoked` |
`PlatformGrantValidityChanged` | `PlatformRoleAssignment.ValidityChanged` |
`PlatformGrantConflictDetected` | `PlatformRoleAssignment.ConflictDetected` |
`PlatformGrantAdministrationDenied` | `PlatformRoleAssignment.AdministrationDenied` |

**Consequence:** `EntityRegistry.cs` must gain an entity definition. That file is a **parallel-team shared file**;
the change is additive (one `EntityDefinition`) and needs selective-commit plumbing. Emitting an event without it
throws at runtime, so this is not optional.

### F5 — `RecordAsync` requires an ambient transaction

It throws without one, by design. The writer opens `ScopedTx.BeginOrJoinAsync` and records the event **before**
commit, satisfying A10's atomicity requirement structurally rather than by convention.

### F6 — `payroll-manage` is the correct first-proof action

`HrActions.PayrollManage` requires `PayrollOfficer` **and is explicitly never bootstrap-open**. That matters for A14:
if the proof used a bootstrap-eligible action, revoking the only grant would return the company to bootstrap-open and
authorization would come *back* — the revocation proof would be ambiguous. With `payroll-manage` the chain is clean:

```
no grant      -> DENIED   (never bootstrap-open)
writer grants -> ALLOWED  (role-driven)
writer revokes-> DENIED   (authorization removed)
```

The bootstrap-open → role-driven transition is proven separately, on a bootstrap-eligible action.

### F7 — Only `IPlatformRoleDirectory` may read the grant store for decisions

The writer may read for administration queries, but **effective-permission decisions in the privilege ceiling (A6)
must go through the real access services**, never reconstructed from storage. This matches A6's instruction and the
existing architectural rule.

### F8 — No concurrency token exists, and one is proposed but NOT added

A9 requires that concurrent updates not silently overwrite each other, and asks that an additive token be justified
before being added.

**Proposal: do not add `rowversion` in Batch A.** The project already has an established concurrency idiom — a
locked read inside a transaction (`UPDLOCK`), with the rule *"a locked read is a fact"*. Batch A uses that: validity
update and revoke re-read the row `WITH (UPDLOCK, ROWLOCK)` inside the transaction, so a concurrent writer serialises
rather than racing. This needs **no schema change** and no EF mapping change.

`rowversion` would be the better answer if grant administration ever becomes an optimistic-concurrency UI (edit form
held open, then saved). Recorded as a future option with its trigger, not added on speculation.

## 3. Concurrent changes found

None affecting this batch. `EntityRegistry.cs`, `PlatformRoleDirectory.cs` and the four access services are unchanged
from the frozen design's description. The parallel team's uncommitted work is present throughout the tree (65 modified
tracked files at session start) but does not touch the grant store, its reader, or the access services.

Nothing was overwritten. `EntityRegistry.cs` is the only parallel-team file Batch A modifies, additively.

## 4. Order of work

1. Additive SQL slice (A2) — columns + idempotency index; **verify** the existing active-grant index
2. Entity additive fields + EF mapping
3. `IPlatformGrantWriter` contract, typed commands and outcomes (A3)
4. Validation rules (A4)
5. Administration authorization + privilege ceiling through real access services (A5, A6)
6. Idempotency (A7), revocation (A8), validity (A9)
7. `EntityRegistry` entity definition + transactional events (A10)
8. API-safe controller (A11)
9. Acceptance + SQL Server tests (A14, A15), manifest registration
10. Risk register, documents

**Deferred by instruction:** UI (A12 — API is sufficient for production writability, so UI defers to Batch M),
legacy coexistence beyond metadata (A16), no migration, no reader cutover, no bootstrap hardening.
