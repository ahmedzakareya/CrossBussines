# Stage 2A — Entry Gate Review

Re-evaluation of every criterion established in `Stage-002A-Entry-Gate-Report.md` §6, plus the criteria this batch's
work introduced.

**Movement since that report: 9 MET · 4 PARTIAL · 4 NOT MET → 16 Completed · 2 Pending Approval · 1 Blocked · 1 Not
Started.** All four PARTIALs closed. Three of the four NOT METs closed.

Batch A not started · Grant Writer not implemented · no production behaviour changed · no SQL executed against
`CrossBuyDB2`.

---

## 1. The 17 original criteria

| # | Criterion | Status | Exact evidence |
|---|---|---|---|
| 1 | Analyzer reproduces the baseline or explains a delta | **Completed** | 388 / 157 / 88 / 143 / 39 controllers reproduced exactly; **0 classification disagreements across all 388 endpoints** vs the accepted scanner; `Roslyn-Authorization-Inventory.csv` (388 rows); `ReconciliationTests` 15 tests |
| 2 | New unprotected mutating actions fail CI | **Blocked** | Mechanism **proven**: `New_debt_can_be_escalated_to_a_build_ERROR_by_configuration` asserts CBA001 reports `Severity == Error` with `DefaultSeverity == Warning`. Blocked on (a) shared-file approval for `CrossBuy.csproj`, (b) **no CI exists**. Plan: `Stage-002A-Roslyn-Build-Integration-Plan.md` |
| 3 | The 143-gap baseline is shrink-only | **Completed** | `Batch00EvidenceGuardTests` 6/6; `authorization-baseline.json` `count == entries == 143 ≤ frozen 143`; CBA004 enforces staleness |
| 4 | Mandatory SQL tests cannot be skipped | **Completed** | Guard 4 (`RequireExecution`) + 3 **real-environment** failure proofs (§3). `CROSSBUY_SQL_REQUIRED=1` makes an unconfigured instance a failure. Variable-set-but-unavailable fails **in every mode** |
| 5 | Mandatory tests cannot be undiscovered | **Completed** | Guard 5 + guard 6: manifest reconciled in **both** directions, and the declared direction is now **class-scoped**. Manifest grew 31 → **104** enforced tests; 98 declared = 98 discovered, 0 missing, 0 unregistered |
| 6 | Production DB names rejected by fixtures | **Completed** | Guard 1: 5 production-catalog inputs refused; the fixture **delegates** to the guard (`Assert.Same` on the list) so the refusal cannot be deleted while a declaration survives; real proof — pointing at `CrossBuyDB2` **fails the run** instead of silently skipping |
| 7 | Scratch DB cleanup enforced | **Completed** | Guard 2 (ownership refusal, 8 rejected inputs, plus the fixture refusing a foreign name before opening any connection) + guard 3 (leftover detection on the real instance). `SQL-Evidence-Summary.md`: **0 leftovers, 1 owned database** |
| 8 | Schema-generating tests isolated | **Completed** | RISK-036 closed; 3 independent purity assertions; `PlatformSchemaDeploymentTests` **9/9**; fingerprint `5\|214\|1\|13\|1341` unchanged |
| 9 | Razor enabled for acceptance | **Completed** | `dotnet build CrossBuy.csproj -c TestRun -t:Rebuild` — full rebuild, **65 s, 0 errors** |
| 10 | Batch P baseline captured before Master Data changes | **Not Started** | 3 of 24 invariants enforced (P-01/02/03). 21 remain, plus 23 canonical datasets. **No Master Data change has occurred, so nothing is out of order** |
| 11 | The three preserved rules have executable tests | **Completed** | `BatchPPreservedRuleTests` **7/7**, 0 skipped, both mutation proofs verified (2 failures / 1 failure, then restored) |
| 12 | Measurement package ready, not executed | **Pending Approval** | `Stage-002A-Read-Only-Measurement-Approval-Package.md` exists and is marked NOT EXECUTED. Awaits written owner approval. **Correction:** the previous delivery report listed this as "Not started" — that was wrong; the package was written in the entry-gate batch |
| 13 | Batch A scope review-ready | **Completed** | `Stage-002A-Entry-Gate-Report.md` §4 |
| 14 | Batch B scope review-ready | **Completed** | `Stage-002A-Entry-Gate-Report.md` §4 |
| 15 | No production implementation started | **Completed** | `git diff -- CrossBuy.sln CrossBuy/CrossBuy.csproj` contains **no analyzer, AdditionalFiles or CBA line** — verified, not assumed. 65 tracked files show modified; all 65 were already modified at the start of this session (Stage 1 + parallel-team WIP) and none was touched here. This batch's changes: 2 new analyzer projects, 2 new test files, 1 test-infrastructure delegation in `SqlServerFixture`, manifest registration, documents and generated evidence — **all inside trees that are themselves untracked** (`CrossBuy.Tests/`, `engineering/`, `docs/platform/`, `docs/architecture/evidence/`), so no committed file changed at all |
| 16 | No SQL against `CrossBuyDB2` | **Completed** | Refusal happens **before any connection is opened** — the `CrossBuyDB2` proof in §3 never connected. `sys.databases` confirms `CrossBuyDB2` present and untouched |
| 17 | Entry-gate report delivered | **Completed** | That report, and this review |

## 2. Criteria introduced by this batch

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 18 | The seven SQL companion guards exist and each proves it can fail | **Completed** | `SqlCompanionGuardTests` — 28 methods, **46 test cases**, 0 skipped. Every guard exercised with an input it must reject |
| 19 | Analyzer build integration is implementation-ready | **Completed** (document) / **Pending Approval** (application) | `Stage-002A-Roslyn-Build-Integration-Plan.md` — exact ItemGroups, reserved project GUIDs, `.editorconfig`, per-rule escalation, 5-phase rollout, 3-level rollback, measured build impact |
| 20 | CI enforces the guardrails | **Blocked** | **No CI configuration exists in the repository.** Platform decision, not an engineering task. §10 of the integration plan states exactly what the pipeline must do |
| 21 | SQL evidence execution is legible in the output | **Completed** | `SQL-Evidence-Summary.md`, regenerated every run; says **EXECUTED** or **NOT EXECUTED** in the verdict line, and lists leftovers by name |

## 3. The three real-environment failure proofs

A guard tested only with synthetic inputs proves its logic. These were run against the actual harness.

| Condition | Result |
|---|---|
| `CROSSBUY_SQL_REQUIRED=1`, `CROSSBUY_TEST_SQL` unset | **FAILED** — *"CROSSBUY_SQL_REQUIRED is set, so the SQL evidence is mandatory, but CROSSBUY_TEST_SQL is not configured. No SQL proof executed."* |
| `CROSSBUY_TEST_SQL` set to an unreachable instance | **FAILED** — *"CROSSBUY_TEST_SQL IS set, yet the SQL fixture is unavailable, so every SQL proof reported Skipped while looking configured. That is the Stage 1 failure mode (46 tests skipped for two batches)."* |
| `CROSSBUY_TEST_SQL` pointed at `CrossBuyDB2` | **FAILED** (2 tests) — refused before any connection opened. Previously this condition produced a **silent skip of every SQL proof** |

The third is the material change. Before this batch, pointing the evidence suite at production disabled all SQL
evidence and reported it as a skip. It is now a red build.

## 4. What this batch actually fixed, beyond adding tests

**A guard that could not fail.** The pre-existing production-catalog test read `SqlServerFixture.ForbiddenCatalogs` by
reflection and asserted the array contained `"CrossBuyDB2"`. Delete the `if (ForbiddenCatalogs.Any(...))` branch, keep
the array, and the test still passed while the fixture ran happily against production. The rule is now a function the
fixture **calls**, the fixture's list is **projected from** the guard, and a test asserts they are the same object.

**72 unregistered SQL evidence tests.** Guard 6 asks the question nobody had asked: does the manifest know about every
SQL test that exists? It did not — 72 of 98 were unregistered, including all three platform-kernel concurrency
families, `PlatformSchemaDeploymentTests` (the independent witness for RISK-036), the single-worker lease tests, and the
two purity tests added when RISK-036 was closed. Unregistered evidence is evidence whose loss nobody notices. All 72
are now registered; the manifest went from 31 to 104 enforced tests.

**A one-directional guard.** The existing Batch 00 check matched a declared test name against methods on **any** type,
so moving a test to a different class satisfied it silently. Guard 5 checks against the **declared class**, and proves
the difference with a test where the same method name on a different class must not satisfy the declaration.

## 5. Verification

| Run | Result |
|---|---|
| `CrossBuy.Tests` full suite | **827 passed · 0 failed · 0 skipped** (2 m 37 s) |
| …composition | 781 pre-existing (all still pass) + 46 new guard cases |
| `CrossBuy.Analyzers.Tests` | **87 passed · 0 failed · 0 skipped** |
| Test project build | 0 warnings, 0 errors |
| Application full rebuild | **65 s, 0 errors** (303 pre-existing warnings) |
| Manifest reconciliation | 98 declared = 98 discovered · 0 missing · 0 unregistered |
| Leftover scratch databases | **0** |
| `CrossBuyDB2` | **present and untouched** |
| Shared fixture fingerprint | `5\|214\|1\|13\|1341` |

The 781 non-regression baseline is intact; 827 is 781 **plus** new guards, not a re-measurement of the same set.

## 6. Gate verdict

**Stage 2A Entry Gate is MET for engineering readiness and NOT MET for enforcement.**

Everything that engineering can close is closed. What remains needs a decision, not code:

| Outstanding | Type | Owner action |
|---|---|---|
| Criterion 2 + 20 — enforcement in the build and in CI | **Blocked / Pending Approval** | approve editing `CrossBuy.csproj` + `CrossBuy.sln`; decide the CI platform |
| Criterion 12 — read-only measurement | **Pending Approval** | written approval to run `SELECT`-only queries against `CrossBuyDB2` |
| Criterion 10 — Batch P's remaining 21 invariants | **Not Started** | required before **Batch C** (Master Data), not before Batch A |

**The honest position on criterion 2:** the analyzer reproduces the baseline, refuses new debt, and its escalation to a
build error is proven by test — but it does not run in anyone's build. Until the two shared files are edited, it is a
**measurement tool, not a gate**. No amount of further testing changes that; only approval does.

### Recommended order

1. Approve the two shared-file edits; apply the integration plan phases 1–2, including the **deliberate temporary
   regression** that proves the analyzer is actually loaded. An analyzer that is wired but silently not loading is
   indistinguishable from one that finds nothing.
2. Approve the read-only measurement package (criterion 12) — it unblocks Stage 2C sizing.
3. Decide the CI platform (criterion 20).
4. Batch P's 21 remaining invariants, **before Batch C**.

**Stopping for approval. Batch A not started. Grant Writer not implemented.**
