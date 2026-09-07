# Stage 2A — Platform Grant Writer — Architecture

The production writer for `PlatformRoleAssignments`. Closes RISK-037.

---

## 1. The four rules it is built around

1. **Session-free.** It takes a `BusinessContext`; it never reads `HttpContext`, session or claims. That is what makes
   the same writer usable from a controller, a future console, a migration and a test — and it is why the Identity
   lookup sits behind `IPlatformAdminIdentity` rather than a `UserManager` injection.
2. **Fail closed.** Unresolved company, unresolved actor, unknown scope, unknown role, unsupported principal kind, POS
   scope — all deny. Nothing defaults; there is no company fallback and no `CompanyID = 1`.
3. **No second authorization engine.** The privilege ceiling asks the **real** access services what the actor may do.
   A permission recomputed from the grant table would be a second engine, and it would eventually disagree with the
   first.
4. **The database owns uniqueness.** Duplicate-active and idempotency are unique indexes. Two concurrent requests both
   read "not found" and both insert; only the engine can arbitrate, so the code reports what the engine decided.

## 2. Contract

`IPlatformGrantWriter` — `CreateGrantAsync`, `RevokeGrantAsync`, `UpdateValidityAsync`, `GetGrantAsync`,
`ListDirectGrantsAsync`.

Typed outcomes, never `bool`: `Success · IdempotentReplay · ValidationFailed · Forbidden · NotFound · Duplicate ·
Conflict · Expired · AlreadyRevoked`. A caller that cannot distinguish "you may not" from "already exists" from "the
same request already succeeded" either retries a forbidden operation forever or treats a duplicate as a failure.

## 3. Administration authority — three tiers

Resolved from mechanisms that already exist, because IMP-001 decided Identity roles stay separate and coexistence
never unions sources.

| Tier | Source | May administer |
|---|---|---|
| **PlatformSecurityAdministrator** | Identity role `SuperAdmin` / `PlatformOps` | any company, any scope — the only cross-company tier |
| **CompanyAdministrator** | Identity role `Admin` / `Administrator` | own company, any module scope |
| **ModuleAdministrator** | holds the module's manage action, proven by that module's **real** access service | own company, **that one scope**, and only roles they themselves hold |

Nine refusals, each tested: worker/system context · no employee · inactive employee · employee-company mismatch ·
unknown admin action · cross-company without platform authority · scope-less request below company tier · unknown
scope · no manage action published for the scope.

### The rule that matters most

**A ModuleAdministrator claim is inadmissible while the scope is bootstrap-open.** A module's access service answers
*true* for its manage action when nothing is configured — that is compatibility, not authority. Crediting it would let
any employee of an unconfigured company grant themselves a role. So the **first** grant in a company can only be made
by a real Identity administrator. This is A5 rule 6, and it is why RISK-043 does not widen here.

## 4. Privilege ceiling (RISK-039)

Before creating or widening a grant: resolve the grantor's authority tier; verify the company; verify the scope;
verify the role. Then:

* **Self-escalation refused** below platform authority. Granting yourself a role is the shortest path from "may
  administer" to "may do anything", and it needs a second person.
* **A module administrator may not confer a role they do not hold**, read through `IPlatformRoleDirectory` so the
  comparison uses the same active/in-date policy every authorization decision uses.
* **A company administrator is exempt from holding the role** — requiring it would mean an administrator could never
  configure a module they do not personally work in.
* Where a module publishes **no** manage-level action, the writer **fails safely** and requires platform authority
  rather than guessing comparability.

Manage actions are named per scope, not inferred: a heuristic ("the action containing 'manage'") would pick
`attendance-manage` for HR, which is not the module's administrative right.

## 5. Idempotency, revocation, validity

**Idempotency** — same key + same payload returns the original as `IdempotentReplay`; same key + different payload is
`Conflict`, because returning the original would tell the caller their new intent succeeded. Uniqueness is a
per-company filtered unique index: scoped per company so a key collision across tenants cannot return another
company's grant as "the original result".

**Revocation** — `IsActive = false`, `RevokedAt`, `RevokedBy`, `Reason`, audit fields; **the row survives**.
Re-revoking returns `AlreadyRevoked` and changes nothing, because overwriting `RevokedBy` would destroy who actually
did it. `CK_PlatformRoleAssignments_Revocation` enforces revoked ⇒ inactive, so the authorization outcome and the
audit record can never disagree.

**Validity** — a revoked grant is not re-dated back to life, and an expired grant cannot be made effective by a date
change. Both are reactivation, which is a separate decision with its own audit history; letting a date change do it
would be reactivation by side effect.

**Concurrency** — a locked read (`WITH (UPDLOCK, ROWLOCK)`) inside the ambient transaction. Concurrent revoke and
validity updates serialise rather than racing. No schema token was added; see the implementation plan F8 for the
`rowversion` proposal and its trigger.

## 6. Events

Inside the caller's `ScopedTx`, immediately before commit, with no swallowing catch — the kernel's contract, so
atomicity is structural rather than conventional.

| Event | When |
|---|---|
`PlatformRoleAssignment.Created` | a grant is created |
`PlatformRoleAssignment.Revoked` | a grant is revoked |
`PlatformRoleAssignment.ValidityChanged` | dates change (carries the previous window) |

`AdministrationDenied` and `ConflictDetected` are **structured logs, not events** — see the delivery report §6 for the
structural reason and the recorded gap.

The registry entry sets `SupportsTimeline`/`Comments`/`Files`/`Followers` all **false** and is absent from the record
picker: a role grant is a security artefact, not a collaboration subject. A timeline would render who-granted-what to
anyone who can open a record. `PermissionScope` is `ScopeNone` deliberately — no single module owns grant
administration, and attributing it to one would let that module's roles decide who may read security events.

## 7. Boundaries

* Writes **only** `PlatformRoleAssignments` (+ the kernel's event tables, through the kernel).
* Does **not** touch `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles` or `BranchUserRoles`.
* **Refuses POS scope** outright — `BranchUserRoles` remains the documented permanent exception.
* Reads the grant store for administration queries only; every authorization decision goes through the access
  services or `IPlatformRoleDirectory`.
* Registered `AddScoped`, and **not** as `IModuleAccessService` — it injects `IEnumerable<IModuleAccessService>`, and
  registering it as one would be the circular dependency the platform rules forbid.
