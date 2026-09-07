# Stage-Construction-C1 — Delivery Report

**Product:** CrossBusiness Platform · **Layer:** CrossBusiness Construction & Contracting
**Tab:** FOURTH · **Increment:** C1 — commercial foundation (CR-01, CR-02, CR-03)
**Date:** 2026-08-06 · **Branch:** `master`

---

## 1. What was asked, and what was delivered

| Asked | Delivered |
|---|---|
| CR-01 remediation | BOQ save is a difference, not a replacement. Identity survives; omitted lines retire; referenced lines cannot be removed. Mutation-proved. |
| CR-02 remediation design **and safe implementation foundation** | `SubcontractScopes` (the cap) + `SubcontractCertificateLines` + hard block per D-07. Mutation-proved. Wiring into the posting path is C6 — §5. |
| CR-03 revision foundation | `CommercialRevisions` (immutable) + `CertificateLineSnapshots` + exact historical reproduction. Mutation-proved. |
| Concurrency + line-level audit foundation | Rotated concurrency token on all 5 new commercial entities; append-only field-level audit with correlation. Declared gap on the 5 legacy base tables — §5. |
| Contract model decision (D-01) | `ClientContracts` + one-primary-per-project index; measurement script shipped; **no backfill run**. |
| Evidence and preservation | 8 documents, 1 disk-state manifest, 1 raw mutation-results JSON, hashes, archive, restore test. |
| Roadmap additions | C11 (schedule baselines, Primavera XER / Microsoft Project import) and C12 (resource leveling) added — **not implemented**. |

## 2. Test-first, and the proof that the tests are guards

38 tests were written **before** the services. A green suite proves nothing on its own, so each remediation was
broken on purpose and the guarding test had to turn red.

```
C-01  restore delete-and-reinsert BOQ behaviour
        baseline passed=1 failed=0  ->  mutated passed=0 failed=1  ->  restored sha identical  ->  passed=1 failed=0
C-02  remove the subcontract certification cap
        baseline passed=1 failed=0  ->  mutated passed=0 failed=1  ->  restored sha identical  ->  passed=1 failed=0
C-03  reproduce a certificate from today's BOQ instead of its snapshot
        baseline passed=1 failed=0  ->  mutated passed=0 failed=1  ->  restored sha identical  ->  passed=1 failed=0

marker sweep: no mutation marker survives anywhere in the tree
RESULT: ALL MUTATION PROOFS PASSED
```

Raw output: `mutation-proof-results.json`. Re-runnable: `python docs/construction/_generator/run_mutation_proofs.py`.

**Two harness bugs found and fixed while doing this, worth recording** because both would have produced a *false*
proof:

1. A lingering `testhost.exe` held the test assembly open; MSBuild could not copy the new one and `dotnet test`
   silently ran the **stale** assembly. The harness now kills it and retries on `MSB3021`/`MSB3027`.
2. Restoring with `shutil.copy2` preserved the file's **original mtime**, which is older than the artifacts compiled
   from the mutated source — so MSBuild skipped the rebuild and the mutated assembly was still under test after the
   restore. The harness now stamps mtime to now; the SHA-256 comparison proves the content is byte-identical anyway.

## 3. Verification

| Check | Result |
|---|---|
| Fresh build `dotnet build CrossBuy.sln` | **Build succeeded — 0 errors** |
| Focused construction tests (`~ConstructionC1`) | **38 passed, 0 failed, 0 skipped** |
| Full application suite | **1319 passed, 0 failed, 183 skipped** (total 1502) |
| Mutation proofs C-01/C-02/C-03 | **3 / 3 PROVEN**, all restored byte-identical |
| Mutation markers left in tree | **none** |
| Scratch cleanup | mutation backups written to a temp dir and deleted in a `finally`; restore test extracts to a temp dir and removes it |
| SQL executed against CrossBuyDB2 or any database | **none — zero statements** |
| EF migration added | **none** (idempotent `deploy/sql` only, per standing rule) |
| Production UI built | **none** |
| Files owned by tabs 1–3 modified | **none** — see §4 |

### 3.1 A note on the full-suite number

At the start of this increment the shared suite had 8 pre-existing failures in first-tab authorization tests, and
part-way through it went red from two other tabs' in-flight edits (`ICommParticipationService.ResolveNotifiableAsync`
gained a required parameter without its test being updated). Both were **theirs**, both were confirmed by mtime and
by the fact that `CrossBuy/CrossBuy.csproj` built clean throughout, and both were fixed by those tabs during the
increment. The suite is now fully green, and no construction file was changed to make that happen.

## 4. Files touched — complete list

Full detail with SHA-256, byte size and line count: `Stage-Construction-C1-Disk-State-Manifest.csv` (26 files).

**Created (production):** `Models/Context/Construction/ConstructionCommercial.cs`,
`BL/Construction/ConstructionAuditService.cs`, `BL/Construction/CommercialRevisionService.cs`,
`BL/Construction/SubcontractScopeService.cs`.

**Modified (production):** `BL/BoqService.cs` — the CR-01 remediation, this tab's own file.

**Modified (shared infrastructure), additive only:**

| File | Change | Why it was unavoidable |
|---|---|---|
| `Models/Context/CrossDbContext.cs` | +8 `DbSet` declarations, +1 `ConstructionCommercialModel.Configure(builder)` call | EF cannot map an entity that is in neither a `DbSet` nor `OnModelCreating`. The mapping itself lives in this tab's own file, so the footprint here is 11 lines. |
| `Program.cs` | +3 `AddScoped` registrations | The new services must be resolvable. All three take only `CrossDbContext` + the audit writer, so the DI graph gains no new coupling. |

Neither file had an existing line altered. **No file under `BL/Platform/`, `BL/Reporting/`, `BL/Communication/`,
`ProjectsAccessService.cs`, or any view was touched.**

**Created (SQL, not executed):** `deploy/sql/construction_c1_commercial_foundation.sql`,
`deploy/sql/construction_c1_contract_mapping_measurement.sql`.

**Created (tests):** the fixture + 4 test files, 38 tests.

## 5. What is NOT finished — stated plainly

Three items are deliberately incomplete, and each would be a misrepresentation if left implied:

| Item | State | Owner / phase |
|---|---|---|
| **Header-only subcontract certification** | The line path hard-blocks. The reconciliation guard `ValidateHeaderAgainstLinesAsync` exists and is tested, but is **not wired** into `ApproveBillingAsync`/`PostBillingAsync` — those are Accounting-posting paths this increment may not change. For a subcontract with **no scope lines**, the legacy header-only route is still reachable in production. | Construction, **C6** |
| **`VariationOrderService.ApproveAsync` still writes BOQ values in place** | The revision mechanism exists and is proved, but variation approval is not yet routed through it. A variation approved via the old path still overwrites in place. | Construction, **C7** |
| **Concurrency tokens on the 5 legacy base tables** | `BoqItems`, `ProgressBillings`, `ProgressBillingLines`, `VariationOrders`, `SubcontractBillings` carry none. Adding one needs an `ALTER` + mapping change, which would break every existing Projects read between shipping and applying. Commercial decisions are protected through the satellites; a concurrent *descriptive* edit is not. | owner schedules the ALTER; see `…Concurrency-and-Audit-Design.md` §6 |

Also pending, by design: the `ClientContracts` / `BoqLineStates` **backfill**, which is gated on the mapping
measurement returning M3 = 0.

## 6. Completion gate

| Gate item | Status |
|---|---|
| CR-01 mechanically prevented | ✔ difference-based save; no delete path remains; mutation-proved |
| BOQ IDs remain stable | ✔ tests 1, 2, 5, 6 |
| Previously billed work cannot rebill | ✔ the money test, asserted through the real previously-billed query |
| Subcontract certificates have line-level caps | ✔ `SubcontractScopes` + `SubcontractCertificateLines` |
| Over-certification hard-blocks without approved variation | ✔ D-07 enforced in service **and** by `CK_SubcontractScopes_VariationQty`; mutation-proved. **Caveat: line path only — §5** |
| Rate changes use immutable revisions | ✔ mechanism complete and proved; **variation reroute is C7 — §5** |
| Historical certificates retain rate/revision snapshots | ✔ `CertificateLineSnapshots`, one per line, never overwritten |
| ContractId mapping measured before migration | ✔ script shipped, **not executed**; decision rule agreed in advance; no backfill run |
| Concurrency enforced | ✔ on all 5 new commercial entities, incl. a genuine two-context race test. **Declared gap on legacy base tables — §5** |
| Line-level audit exists or is fully specified where blocked | ✔ exists, append-only, structurally asserted |
| Mutations C-01/C-02/C-03 fail and are restored | ✔ 3/3, byte-identical restores, no markers |
| No other tab's files modified | ✔ §4 |
| No production UI built | ✔ |
| No CrossBuyDB2 SQL executed | ✔ zero statements |
| Archive restoration has 0 mismatches | ✔ `Stage-Construction-C1-Preservation-Report.md` |

## 7. Recommended next step

Not more code. Three owner actions unblock C2/C3:

1. **Apply** `construction_c1_commercial_foundation.sql` to the target database (SQL before code).
2. **Run** `construction_c1_contract_mapping_measurement.sql` and record M3 and M9. If M3 returns any row, stop and
   bring it to review — the single-contract assumption is already violated in the data.
3. **Confirm** the D-08 default mode (Warning / Approval Required / Hard Block) per company, which C2's budget
   control needs before it can be built.

Then C6 wires the reconciliation guard and C7 reroutes variation approval — the two remaining routes to the
defects C1 has otherwise closed.
