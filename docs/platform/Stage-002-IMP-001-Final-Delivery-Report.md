# IMP-001 — Role Source Consolidation — Final Delivery Report

**IMP-001 = Completed** (design). **IMP-004 = Not Started. IMP-002 = Not Started. Stage 2A = Not Started.**

No migration executed · no production behaviour changed · no SQL executed against `CrossBuyDB2`.

---

## 1. Executive summary

IMP-001 set out to consolidate four legacy role tables into `PlatformRoleAssignments`. Source inspection found that the
target table **has no production writer** — it is written only by tests — which changed IMP-001 from a data-migration
problem into a **missing-capability** problem and produced a Critical risk against Wave 2. The delivery is a complete
design for four sequenced stages: **Grant Management Capability → Legacy Role Migration → Authoritative Cutover →
Legacy Writer Shutdown**, plus five new risks now in the canonical register.

## 2. Original objective

Consolidate `AccountingUserRoles`, `InventoryUserRoles`, `CrmUserRoles` and applicable module role sources into
`PlatformRoleAssignments` so a future Security Administration Console can show the complete, authoritative security
state. A console showing only some role sources is unacceptable.

## 3. The finding that changed the scope

`PlatformRoleAssignments` has **4 production readers** (`PlatformRoleDirectory`, `HrAccessService`,
`ProjectsAccessService`, `EntityRegistry`) and **0 production writers** — writes exist only in 4 test files.
`ProjectMembers` is the same.

Because the table is empty and unfillable in production, `AnyConfiguredAsync` returns false and **HR and Projects
resolve applicable actions through bootstrap-open**. `payroll-manage`, `confidential-view` and project `billing` keep
their explicit closed behaviour.

## 4. Production Grant Writer requirement

Wave 2 intends to gate **52 High-risk HR/Identity endpoints** on `HrAccessService`. Without a writer those gates
evaluate bootstrap-open and **allow broadly**, while Wave 2's tests pass because fixtures seed grants directly.
**A gate over an unfillable grant store is not a gate.**

Design: `Stage-002-Platform-Grant-Writer-Design.md` — contracts, 20 domain rules each mapped to a failure code, five
administrator types, storage semantics, and the additive audit schema.

## 5. Sources that migrate — 3

`AccountingUserRoles` · `InventoryUserRoles` (with `ScopeBranchId` preserved exactly) · `CrmUserRoles`.
**10 eligible role mappings**; module action vocabularies stay inside their access services, so `post`, `pay`,
`manage`, `currency-override` and confidential rules are unchanged.
Matrix: `Stage-002-Role-Migration-Matrix.csv` (18 rows — 10 Eligible, 4 Already-Native, 4 Excluded).

## 6. Sources that do not migrate

Business memberships (§7) · Identity roles (§8) · POS (§9) · hierarchy-derived access · record ownership ·
bootstrap-open policy · system-context policy · `SessionValidation`/`PosLaneActivityGuard` (not authorization at all).
Full classification: `Stage-002-Role-Source-Inventory.csv` (13 sources).

## 7. Business memberships — permanently separate

`ProjectMembers` and `ConversationMembers` are business facts with their own lifecycles that *affect* access. Batch C.1
proved conversation membership is the control protecting a conversation; turning it into role rows would move
message-level access into role storage. The Grant Writer **refuses** membership-shaped writes.

## 8. Identity roles — separate

AspNet roles (`Admin`/`Administrator`/`SuperAdmin`/`PlatformOps`), read through `IsInRole` at **one** call site
(`PlatformOpsAttribute`). Platform-operator identity, not a business grant. Shown in its own console view.

## 9. POS exception — upheld on mechanical grounds

`BranchUserRoles` stays outside. Lane login resolves rights **before** a `BusinessContext` exists, and the table is
**branch-owned with no `CompanyID`**. Forcing it in would require resolving a company before login establishes one — a
circular dependency on the most latency-sensitive path. The Grant Writer rejects POS scope.

## 10. Coexistence model

**Never union — union broadens silently.** One `RoleSourceCutover` flag per **(company, scope)** selects exactly one
authoritative source: `Legacy` · `Shadow` · `Platform`. Detail and the ten required answers:
`Stage-002-Role-Source-Consolidation-Design.md` §5.

## 11. Shadow divergence

Shadow **decides on legacy** and evaluates platform in parallel, so it can never broaden access. Eleven divergence
types with severity and blocking status: `Stage-002-Platform-Grant-Writer-Design.md` §6.

## 12. Cutover blocking rules

**Any authorization-widening divergence blocks cutover** — `ExtraPlatformGrant`, `ScopeMismatch`, `CompanyMismatch`,
`BranchMismatch` (all Critical), plus `MissingPlatformGrant`, `RoleMappingMismatch`, `RevocationMismatch`,
`PrincipalMismatch` (High). `BootstrapOpenMaskedDifference` also blocks: **a company in bootstrap-open cannot prove its
migration is correct**, because both answers were allowed for the same reason. This is where IMP-001 and IMP-002
interlock.

## 13. Legacy writer shutdown

**7 writers** across 4 files, with per-mode behaviour: `Stage-002-Legacy-Role-Writer-Shutdown-Matrix.csv`.
At `Platform` cutover each either **redirects to the Grant Writer** or is **explicitly disabled with a user-facing
message**. **No silent success** — an admin UI must never report success after writing to an ignored source (RISK-038).
W-007 (POS) is never disabled.

## 14. Security Console contract

**Seven distinct views**, never merged: Direct Platform Grants · Effective Permissions · Business Memberships ·
Bootstrap-Open Exposure · Identity Roles · POS Branch Roles · Migration & Divergence Diagnostics.
**Effective Permissions must call the real access services** — it must not reconstruct module rules from storage, or the
console becomes a second permission engine that can disagree with production. Requires `PlatformOps` and is
permission-trimmed; it is an attack map. Detail: consolidation design §6.

## 15. Storage changes required later

The current schema **cannot** support the required audit behaviour. Eight additive nullable columns (`RevokedAt`,
`RevokedBy`, `SourceSystem`, `MigrationBatchId`, `IdempotencyKey`, `UpdatedAt`, `UpdatedBy`, `Reason`), plus a
**filtered unique index** on `(CompanyID, Scope, Role, PrincipalType, PrincipalId, ScopeBranchId) WHERE IsActive = 1`
and a unique index on `IdempotencyKey`. Both need `sqlcmd -I`. All SQL additive, idempotent, rollback-documented.
Revoked rows retained indefinitely; nothing hard-deleted.

## 16. Administration authorization

Five administrators: Identity · **Platform security (`PlatformOps`)** · Module · Company · Branch POS.
A module administrator grants **only its own scope in its own company** and can **never** grant `Platform` scope or
cross-company. **Escalation guard:** no administrator may grant a role exceeding their own effective rights (RISK-039).

## 17. Test strategy

23 mandatory Grant Writer cases plus the consolidation cases, including: idempotent command does not duplicate ·
failed grant leaves no partial row · **console decision equals access-service decision** · cutover blocked by widening
divergence · legacy writer blocked after cutover · rollback restores legacy authority · **bootstrap-open cannot make a
migration test pass falsely** · no administrator can grant beyond their own rights.
SQL Server disposable databases for the filtered index, validity constraints, transactions, migration, rollback and
concurrency — carrying `[RequiredEvidence]` so they **cannot report skipped** (RISK-026), on **isolated probe
databases** (RISK-036).

## 18. Deployment and rollback

SQL (additive) → Grant Writer + validation → console read-only → shadow read per company → reconcile → cutover →
disable legacy readers → **disable legacy writers (the last irreversible step)**. Legacy tables are **not dropped** in
the first implementation phase. **Rollback = flip the flag to `Legacy`** — migration is additive and legacy rows are
untouched, so no data restore is needed.

## 19. Risks

Merged into the canonical register this delivery: **RISK-035** (Medium — 2 concurrency tokens across 231 entities;
the stock writer **is** protected by explicit locking and must not be read as unprotected) · **RISK-036** (High —
order-dependent shared SQL fixture pollution) · **RISK-037** (**Critical** — no production writer; explicitly blocks
Wave 2 HR rollout, Security Console activation and Platform cutover) · **RISK-038** (High — legacy writer after
cutover) · **RISK-039** (High — grant privilege escalation).

**Canonical totals: 39 risks — 7 Critical · 17 High · 12 Medium · 3 Low.** Previously 34 (6/14/11/3). Totals were
**not forced**; the delta is exactly the five merged risks (+1 Critical, +3 High, +1 Medium).

## 20. Completed artifacts

| Artifact | Note |
|---|---|
| `Stage-002-Role-Source-Consolidation-Design.md` | inventory, per-module mapping, coexistence, migration, deployment/rollback |
| `Stage-002-Platform-Grant-Writer-Design.md` | contracts, 20 rules, admin authorization, storage, shadow divergence, tests |
| `Stage-002-Role-Source-Inventory.csv` | **generated** — 13 sources |
| `Stage-002-Role-Migration-Matrix.csv` | **generated** — 18 rows |
| `Stage-002-Legacy-Role-Writer-Shutdown-Matrix.csv` | **generated** — 7 writers |
| `Stage-002-EF-Relationship-Inventory.csv` | **accepted** — 53 rows, 42 Cascade, 1 rejection with action |
| `Stage-002-Risk-Register.md` + `.csv` | **both generated from one source**, deterministic |
| `generate-imp001-artifacts.py`, `generate-risk-register.py` | the generators |

Shadow divergence, Security Console contract, and the test plan live **inside** the two design documents rather than as
separate files — deliberately, because duplicating them would create synchronisation risk (permitted by the brief).

## 21. Verification state

| Check | Result |
|---|---|
| Application build, **Razor enabled** | **0 errors** |
| Full suite, **SQL evidence enabled** | **766 total · 766 passed · 0 failed · 0 skipped** |
| Risk Register generated twice | **identical hashes** (MD `e562b817…`, CSV `20a76aa1…`) |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |

No skipped evidence test is counted as coverage — there are none.

## 22. Known limitations

1. **RISK-037 is unresolved by design work alone** — the writer must be *built* before Wave 2.
2. **Migration volume is unmeasured.** Legacy role tables are small and admin-maintained, but no row counts were taken
   (that needs approved read-only access, same constraint as the variant measurement).
3. **External principals are designed but unused.** `PrincipalType` supports them; no portal exists, so
   `ExternalUser`/`ServicePrincipal` remain untested paths.
4. **The company-source defect persists in the legacy writers** — `AccountingController`, `InventoryController` and
   `CrmController` all take company from a compile-time constant (RISK-003). Migration reads that constant, so any
   pre-cutover copy inherits it. Mitigated because the Grant Writer resolves company properly, but the **copy step must
   validate company against the employee row rather than trusting the legacy value**.
5. **`BranchUserRoles` has no `CompanyID`**, so POS grants cannot be company-verified at all. Accepted as part of the
   sanctioned exception, not solved.

## 23. Explicitly not implemented

The Grant Writer · any schema change · any migration · the Security Console · cutover flags · shadow-read
instrumentation · legacy writer changes · IMP-002 bootstrap-open governance · IMP-004 Master Data consolidation ·
Stage 2A analyzers.

## 24. Final requirement status

| Gate | Status |
|---|---|
| 1–5 RISK-035…039 merged (037 as Critical) | **Completed** |
| 6 Markdown and CSV from one source | **Completed** |
| 7 Deterministic output | **Completed** — identical hashes twice |
| 8 Final report exists | **Completed** — this document |
| 9 Accepted artifacts referenced | **Completed** (§20) |
| 10 Final verification passes | **Completed** (§21) |
| 11 No role migration executed | **Completed** |
| 12 No production behaviour changed | **Completed** |
| 13 No SQL against `CrossBuyDB2` | **Completed** |
| 14 IMP-004 not started | **Confirmed** |
| 15 IMP-002 not started | **Confirmed** |
| 16 Stage 2A not started | **Confirmed** |

**IMP-001 = Completed. 20 of 20.**

## 25. Recommendation for the next step

**IMP-002 (bootstrap-open governance) should precede IMP-004**, reversing the brief's stated order — and the reason is
RISK-037 plus `BootstrapOpenMaskedDifference`:

* HR and Projects are **bootstrap-open in production today**, and IMP-002 is what makes that visible and auditable;
* a company in bootstrap-open **cannot prove its role migration is correct**, so IMP-002 is a practical prerequisite
  for IMP-001's own cutover;
* IMP-004 (Master Data) gates 2C, which is later than 2B in every version of the roadmap.

This is a recommendation, not a change — the order remains yours. If IMP-004 is preferred next, nothing in IMP-001
blocks it.

**Stopping for review. IMP-004 and IMP-002 not started. Stage 2A not started.**
