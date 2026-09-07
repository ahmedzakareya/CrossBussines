# Stage 2A — Batch A — Platform Grant Writer — Delivery Report

**Status: Completed. RISK-037 is mitigated with executable evidence.**

`PlatformRoleAssignments` has been the designated grant store since Stage 1 Batch C with **no production writer** —
grants could only be inserted by hand, so every module reading it was bootstrap-open in practice. A store nobody can
write is a security design that exists only on paper. Batch A ends that.

No Batch B · no Grant Writer beyond this scope · no Bootstrap hardening · no Wave 2 · no Security Console · no role
migration · no reader cutover · no Master Data change · no SQL executed against `CrossBuyDB2`.

---

## 1. Completion gate — 27 criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Production Grant Writer exists | **MET** — `IPlatformGrantWriter` / `PlatformGrantWriter`, DI-registered |
| 2 | Create works | **MET** — acceptance test 1 |
| 3 | Revoke works | **MET** — same test, and audit fields asserted |
| 4 | Validity update works | **MET** — with reactivation refused |
| 5 | Direct grant query works | **MET** — `ListDirectGrantsAsync` + `GetGrantAsync` |
| 6 | Additive schema verified twice | **MET** — both slices applied twice on a disposable probe, idempotent |
| 7 | Duplicate active grants blocked by SQL | **MET** — pre-existing filtered unique index verified, not duplicated |
| 8 | Revoked history preserved | **MET** — no hard delete; `RevokedAt`/`RevokedBy`/`Reason` + CHECK |
| 9 | Idempotency works | **MET** — replay returns the original; different payload → Conflict |
| 10 | Administration authorization enforced | **MET** — 3 tiers, 9 rules |
| 11 | Privilege escalation blocked | **MET** — RISK-039 tests |
| 12 | Company isolation enforced | **MET** — cross-company denied; foreign grant = NotFound |
| 13 | Branch scope preserved | **MET** — exact branch, foreign branch refused |
| 14 | BusinessEvents transactional | **MET** — recorded inside `ScopedTx` before commit, no swallow |
| 15 | Production writer used in acceptance tests | **MET** — **no test seeds a grant row** |
| 16 | HR reads a grant created by the writer | **MET** — real `HrAccessService`, fresh context |
| 17 | Revocation removes effective authorization | **MET** — proven on a never-bootstrap-open action |
| 18 | All mandatory SQL tests execute | **MET** — 844/844, **0 skipped** |
| 19 | Analyzer reports no new debt | **MET** — CBA001 = 0, CBA004 = 0, CBA006 = 0; debt still **143** |
| 20 | No role migration executed | **CONFIRMED** |
| 21 | No legacy reader cutover | **CONFIRMED** |
| 22 | No Bootstrap hardening | **CONFIRMED** |
| 23 | No Security Console | **CONFIRMED** — API only; UI deferred to Batch M |
| 24 | No Master Data changes | **CONFIRMED** |
| 25 | No SQL against `CrossBuyDB2` | **CONFIRMED** |
| 26 | Batch B not started | **CONFIRMED** |
| 27 | Delivery report complete | **MET** — this document |

## 2. Verification

| Run | Result |
|---|---|
| Analyzer project build | 0 errors |
| Analyzer tests | **93 / 93**, 0 skipped |
| Solution build — Debug | **0 errors** (281 pre-existing warnings) |
| Solution build — Release | **0 errors** (276 warnings) |
| Solution build — TestRun | **0 errors, 0 warnings** |
| Application suite, SQL evidence enabled | **844 passed · 0 failed · 0 skipped** (3 m 38 s) |
| …composition | 827 pre-existing + **17 new grant-writer acceptance tests** |
| Grant-writer acceptance | **17 / 17** |
| SQL slice applied twice on a disposable probe | idempotent both passes; probe dropped |
| Manifest declared / discovered | reconciled, **121 enforced tests**, 0 unregistered |
| Risk register generated twice | byte-identical (md5 match) |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |
| CBA001 / CBA004 / CBA006 | **0 / 0 / 0** |

## 3. The guardrail blocked this batch's own code — and that is the headline

The analyzer, wired into the build in the previous increment, **failed the build with three CBA001 errors** the moment
the new controller appeared:

```
error CBA001: Mutating endpoint 'PlatformGrantsApiController.Create' has no authorization the analyzer can see
error CBA001: Mutating endpoint 'PlatformGrantsApiController.Revoke' ...
error CBA001: Mutating endpoint 'PlatformGrantsApiController.UpdateValidity' ...
```

A17 forbids both available shortcuts — no baseline additions, no suppression. The only honest option was the third:
**declare `IPlatformGrantWriter` an authorization authority**, which it genuinely is (every write method resolves the
actor's tier and ceiling through the real access services and refuses before touching a row). Added visibly to
`AuthorizationSurface.AuthorityTypes` with the reasoning in the code.

Two further pre-existing guards also caught this batch, both correctly:

* **`Stage1RawSqlSafetyTests`** rejected the writer's `FromSqlRaw` until a company-scope verdict was recorded. Added,
  with the reason the raw SQL exists (the `UPDLOCK` read *is* the concurrency control) and why it is scoped (both
  `ID` and `CompanyID` are explicit parameters).
* **SQL companion guard 6** flagged the 17 new tests as unregistered evidence until they were added to the manifest.

Three independent guardrails from earlier increments each stopped real work until it was made correct. That is what
they were built for.

### And two defects this batch introduced, found by reading the guards rather than trusting a green run

1. **The acceptance class leaked 36 probe databases.** Undisposed `CrossDbContext` instances kept pooled connections
   open, so `DROP DATABASE` did not take effect. Fixed with `SqlConnection.ClearAllPools()` before the drop; a full
   run now leaves **0**.
2. **Worse: guard 3's real-instance leftover check could never fail.** It passed `ListOwnedProbeDatabasesAsync()` as
   "accounted for", and that method returns *every* probe on the instance — so every leftover was automatically
   excused. It reported clean while 36 leaked probes sat on the server. This is precisely the "guard satisfied by a
   declaration rather than by a fact" failure the companion guards exist to prevent, committed by a guard itself.
   Fixed to account only for the shared fixture's own database.

Both are reported rather than quietly corrected. Detail in `Stage-002A-Platform-Grant-Writer-Test-Evidence.md` §5.

## 4. Measured effect on the authorization surface

| Figure | Before | After | Why |
|---|---|---|---|
| Mutating endpoints | 388 | **391** | 3 new administration endpoints |
| Attribute-protected | 157 | 157 | unchanged |
| Verified in-body | 88 | **91** | the 3 new endpoints, via the writer |
| **Authorization debt** | **143** | **143** | **unchanged — zero new debt** |
| Controllers | 39 | **40** | one new API controller |

`391 = 157 + 91 + 143`. The 143 baseline entries are **byte-identical** to Stage 1's and the gap set still matches the
file id for id. `frozenBaselineHistory` in `authorization-baseline.json` records the delta and its reason; the
descriptive totals moved because production code was added, and the enforced invariant did not.

## 5. A1 findings that changed the plan

Recorded in full in `Stage-002A-Batch-A-Implementation-Plan.md`. The four that mattered:

1. **The filtered unique index already existed** with exactly the required key. A2 said "add"; the correct action was
   to **verify**. The slice asserts its presence, uniqueness, filter and key order, and **throws** if any has drifted —
   a missing duplicate-prevention index is a security property missing, not a performance detail.
2. **Business event names are constrained by the kernel.** `RecordAsync` validates `EntityCode` against
   `EntityRegistry` and enforces `<EntityCode>.<Action>`. So `PlatformGrantCreated` became
   `PlatformRoleAssignment.Created`, and `EntityRegistry` (a parallel-team shared file) gained one additive
   `EntityDefinition`. Without it, every event would throw at runtime.
3. **`payroll-manage` is the only unambiguous proof action.** It is never bootstrap-open, so revocation genuinely
   removes authorization. On a bootstrap-eligible action, revoking the last grant returns the company to
   bootstrap-open and authorization comes *back* — the revocation proof would have been meaningless.
4. **No concurrency token was added.** A9 asked for justification first. The project's established idiom — a locked
   read inside the transaction — needs no schema change and no EF mapping change. `rowversion` is recorded as a
   future option with its trigger (an edit-form UI), not added on speculation.

## 6. Judgement calls, stated

**Module-level authority is inadmissible under bootstrap-open.** A module's access service answers *true* for its
manage action when nothing is configured — that is compatibility, not authority. Treating it as authority would let
any employee of an unconfigured company grant themselves a role. So the **first** grant in a company can only be made
by a real Identity administrator. This is A5 rule 6, it is tested, and it is the reason RISK-043 does not widen here.

**Two of the five events are logged, not evented.** `BusinessEventRecord` requires a positive `EntityId` naming a real
entity. A denied attempt and a lost uniqueness race have **no row**. Emitting an event would mean inventing an id —
and the timeline and notification projections resolve `EntityId`, so a fabricated id would attach a security event to
whichever real grant holds that number. A false audit line pointing at an innocent record is worse than no event, so
`AdministrationDenied` and `ConflictDetected` are structured logs with every field an investigator needs. A10 lists
both as optional, "only where safe and useful" — they are neither, for a structural reason. **Recorded as a gap.**

**A company administrator is not required to hold the role they confer.** Requiring it would mean an administrator
could never configure a module they do not personally work in. A *module* administrator **is** required to hold it,
because their authority derives from the module itself. Both are tested.

**No UI.** The API is sufficient for production writability, so A12's option to defer applies: **UI deferred to Batch
M**, stated explicitly as A12 requires.

## 7. Shared files modified

| File | Change |
|---|---|
`CrossBuy/BL/Platform/EntityRegistry.cs` | one entity code + one `EntityDefinition` (additive) |
`CrossBuy/Models/Context/CrossDbContext.cs` | slice-2 property lengths + the idempotency index (additive) |
`CrossBuy/Program.cs` | two DI lines |
`CrossBuy.Analyzers/AuthorizationSurface.cs` | declared the writer an authority |
`engineering/authorization-baseline.json` | descriptive totals + history; **143 entries untouched** |
`engineering/required-evidence-manifest.json` | +17 registered tests |
`CrossBuy.Tests/Stage1RawSqlSafetyTests.cs` | raw-SQL verdict |

All additive. Each needs selective-commit plumbing at commit time. **No existing production behaviour was changed:**
no access service, no reader, no legacy role writer, no Master Data path was touched.

## 8. Not done, and why

| Item | Status | Reason |
|---|---|---|
| `AdministrationDenied` / `ConflictDetected` as events | **Gap** | needs a company-level event entity that can carry an id (§6) |
| Concurrent create/revoke race tests under real parallelism | **Gap** | A15 asks for them; the uniqueness race is handled in code and reported as `Conflict`, but a two-thread SQL Server test is not yet written |
| Grant administration UI | **Deferred to Batch M** | A12 permits it once the API is sufficient |
| Legacy coexistence beyond metadata | **Foundations only** | `SourceSystem` + `MigrationBatchId` exist and are dormant, as A16 requires |
| CI platform | **Blocked Pending Platform Decision** | A18: local build enforcement works; **remote CI enforcement is NOT active** |

### CI status, stated plainly per A18

There is **no CI pipeline**. Enforcement today is **local build enforcement only** — the analyzer fails a developer's
build and would fail a build server if one existed. Ready-to-apply Azure DevOps and GitHub Actions plans are described
in `Stage-002A-Roslyn-Build-Integration-Plan.md` §10; **neither is committed**, and one must be chosen. Do not read
this batch as evidence of remote CI enforcement.

## 9. Risk register

Generated twice, byte-identical, from `docs/architecture/evidence/generate-risk-register.py`.

| Risk | Before | After | Basis |
|---|---|---|---|
| **RISK-037** | Open / Critical | **Mitigated** (severity unchanged) | production writer + 17 passing acceptance tests, none seeding a grant |
| **RISK-038** | Open / High | **Open** | legacy writers are still live; no cutover occurred |
| **RISK-039** | Open / High | **Mitigated** (severity unchanged) | ceiling implemented and tested through the real access services |
| **RISK-040** | Open / Critical | **Open** | Mechanism A is unchanged |
| **RISK-043** | Open / High | **Open** | no bootstrap hardening performed |

**No severity was reduced.** Mitigated is not Closed: RISK-037 stays open in severity because no company has been cut
over and the legacy writers still run, and RISK-039 stays open because no administration UI exists yet.

## 10. Verdict

The production write capability that Wave 2, bootstrap hardening, platform-source cutover and the Security Console all
depend on now exists, is authorized, is audited, and is proven by tests that use it rather than around it.

**Stopping for review. Batch B not started. Bootstrap hardening not started. Wave 2 not started. Security Console not
built. No role migration executed.**
