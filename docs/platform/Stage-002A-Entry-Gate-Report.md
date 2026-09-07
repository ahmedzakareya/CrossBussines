# Stage 2A — Entry Gate Report

**The entry gate is NOT met. 9 of 17 criteria satisfied.** Stage 2A production implementation must not begin.

No production feature implemented · no Grant Writer · no bootstrap behaviour change · no Master Data migration ·
no SQL executed against `CrossBuyDB2` · no frozen Phase 0 document modified.

**Suite: 772 / 772 passed · 0 failed · 0 skipped** (was 766; +6 evidence-guard tests). Application build with Razor
enabled: **0 errors**.

---

## 1. Stage 2A baseline delta — none

Re-derived from the live tree before any work:

| Metric | Frozen | Measured | Delta |
|---|---|---|---|
| Mutating actions | 388 | **388** | none |
| Attribute-protected | 157 | **157** | none |
| Verified in-body | 88 | **88** | none |
| Authorization backlog | 143 | **143** | none |
| Controllers | 39 | **39** | none |

**No delta record is required.** The frozen Phase 0 baseline still reconciles: `388 = 157 + 88 + 143`.

## 2. What was delivered

### 2.1 Shrink-only authorization debt baseline — **Completed**

`engineering/authorization-baseline.json` — **143 entries** (141 `AuthorizationGap` + 2 `AnonymousByDesign`), generated
from the reconciled evidence, every entry carrying controller, action, verb, module, file, classification, target wave,
`addedBy` and reason.

Rules encoded in the file itself, including the one that is easy to miss: **a controller or action rename does not create
a silent allowance** — the id is `Controller.Action`, so a rename makes the old id stale and the new id unlisted, which
is an error rather than a free pass.

### 2.2 Required-evidence manifest — **Completed**

`engineering/required-evidence-manifest.json` — 9 groups, **24 existing tests named individually**, 6 planned for
Batch 00/P. It exists because *"total tests passed"* was the metric that let 46 skipped tests count as coverage for two
batches.

### 2.3 The evidence guard, and a negative proof — **Completed**

`CrossBuy.Tests/Batch00EvidenceGuardTests.cs`, **6 tests, deliberately NOT gated on `CROSSBUY_TEST_SQL`** — their whole
purpose is to catch the case where the SQL-gated tests are silently absent.

The core guard resolves every manifest-named test **by reflection** and fails if it does not exist, so a rename or
deletion cannot quietly remove evidence.

**Proven by negative test, not asserted:** injecting a phantom test name into the manifest made the guard **fail**
(1 failed); removing it restored **6 passed**. Stage 1's defect was precisely a guard nobody had proven could fail.

Also enforced: the baseline may only shrink · every entry is traceable · **only the two login actions** may claim
`AnonymousByDesign` · the frozen figures still reconcile · the manifest declares `CROSSBUY_TEST_SQL` and the
not-discovered rule.

## 3. What was NOT delivered — with exact missing work

### 3.1 Roslyn Authorization Analyzer — **Not Started**

*Missing:* the analyzer project, 9 diagnostics (CBA001–009), the symbol-level authority resolver, the bounded
call-graph walk, and 20 analyzer test cases reproducing the Stage 1 scanner failures.
*Reason:* capacity in this delivery. **Not a tooling obstacle** — `Microsoft.CodeAnalysis.CSharp.Workspaces` 4.8.0 and
5.0.0 are present in the local NuGet cache, so the project can be created offline.
*Impact:* **new unprotected mutating actions do not yet fail the build.** The baseline artifact exists and is enforced
for *shape and shrinkage*, but nothing yet detects a newly-added unprotected action. That is entry-gate criterion 2 and
it is unmet.
*Next step:* create `CrossBuy.Analyzers` + `CrossBuy.Analyzers.Tests`, implement CBA001 first, and gate it behind the
**dogfood check — it must reproduce 157/88/143 before it may fail any build.**

### 3.2 Batch P executable evidence suite — **Not Started**

*Missing:* `BatchPMasterDataBaselineTests`, the canonical disposable datasets (23 shapes), machine-readable baseline
artifacts with deterministic hashes, and the three preserved-rule tests (missing-conversion rejection, ambiguous-barcode
rejection, barcode-UoM guard).
*Reason:* capacity.
*Impact:* **this is the most consequential gap.** The 24 invariants need a baseline captured **before** any Master Data
change, and the three preserved retail rules are currently protected by prose alone — nothing mechanically prevents a
factor-1 fallback or first-match barcode resolution being introduced.
*Next step:* the three rule tests **first** (they are small, need no dataset, and close the highest-value hole), then
the datasets, then the 24 invariants.

### 3.3 Order-independence guard (RISK-036) — **Partial**

*Delivered:* the manifest names the three intended tests, and IMP-003's comparison test was already fixed so it no
longer pollutes the shared fixture.
*Missing:* `Batch00EvidenceGuardTests` SQL companions — shared-fixture purity, leftover-scratch detection, and the
fixture's catalog refusal — plus reverse-order execution.
*Impact:* the latent ordering fragility from the F4 fixture is documented and one trigger was removed, but not yet
mechanically prevented.

### 3.4 CI implementation and output summary — **Not Started**

*Missing:* the pipeline definition, four-state result parsing, and the evidence summary block.
*Reason:* no CI configuration exists in the repository to extend; creating one is a decision about infrastructure rather
than code.
*Impact:* every guarantee is currently enforceable **locally** but not enforced **automatically**.
*Next step:* confirm the CI system, then wire the manifest check, Razor-enabled acceptance, 3× stability and the
leftover-scratch query.

### 3.5 Documents not written — **Not Started**

`Stage-002A-Batch-00-Engineering-Guardrails-Design.md` · `Stage-002A-Batch-00-Delivery-Report.md` ·
`Stage-002A-Batch-P-Non-Regression-Baseline.md` · `Stage-002A-Entry-Plan.md`.
The Batch 00 design content exists in the accepted `Stage-002-Engineering-Platform-Proposal.md` (frozen); the entry
order exists in the accepted `Stage-002A-Execution-Roadmap.md` (frozen). **Neither frozen document was modified.**

### 3.6 Risk Register — **unchanged, deliberately**

No new evidence-backed risk emerged from this batch, and the brief forbids speculative additions. **50 risks
(8/25/14/3), hashes unchanged.** RISK-036, 037, 040, 043, 046–050 all preserved.

## 4. Batch A and Batch B scope previews

Both are **review-ready in the frozen designs** and were not restated: Batch A in
`Stage-002-Platform-Grant-Writer-Design.md` (contracts, 20 domain rules each mapped to a failure code, five
administrator types, storage with 8 additive columns + filtered unique index, 23 tests) and Batch B in
`Stage-002-Bootstrap-Open-Governance-Design.md` (six-state model, `BootstrapAccessPolicies` + history,
behaviour-preserving seed, the 14 Never-Bootstrap-Open actions, decision-source contract).

**RISK-037 remains blocking until Batch A completes. RISK-040 and RISK-043 remain blocking until Batch B completes.**

## 5. Early-2A screen forecast — 1 screen, not six

**Engineering Compliance Dashboard** only: baseline trend, new violations, evidence status. The trend over time is the
one thing CI text cannot show, and it is what stops the baseline becoming permanent.
**Grant Administration** waits on Batch A approval · **Bootstrap Exposure** waits on Batch B plumbing ·
**Security Console** stays Batch M and must not activate before authoritative role sources exist.
**CI remains the primary developer interface.**

## 6. Entry gate assessment

| # | Criterion | Status |
|---|---|---|
| 1 | Analyzer reproduces the baseline or explains a delta | **NOT MET** — analyzer not built; **baseline verified unchanged** (§1) |
| 2 | New unprotected mutating actions fail CI | **NOT MET** |
| 3 | The 143-gap baseline is shrink-only | **MET** — artifact + enforcing test |
| 4 | Mandatory SQL tests cannot be skipped | **PARTIAL** — manifest + reflection guard exist; CI enforcement does not |
| 5 | Mandatory tests cannot be undiscovered | **MET** — proven by negative test |
| 6 | Production DB names rejected by fixtures | **MET** — pre-existing `ForbiddenCatalogs`; test pending (§3.3) |
| 7 | Scratch DB cleanup enforced | **PARTIAL** — verified manually every run (0 remaining); not yet a test |
| 8 | Schema-generating tests isolated | **PARTIAL** — one trigger removed; guard test pending |
| 9 | Razor enabled for acceptance | **MET** — every run this batch |
| 10 | Batch P baseline captured before Master Data changes | **NOT MET** — and no Master Data change has occurred, so nothing is out of order |
| 11 | The three preserved rules have executable tests | **NOT MET** — highest-value gap |
| 12 | Measurement package ready, not executed | **MET** |
| 13 | Batch A scope review-ready | **MET** |
| 14 | Batch B scope review-ready | **MET** |
| 15 | No production implementation started | **MET** |
| 16 | No SQL against `CrossBuyDB2` | **MET** |
| 17 | Entry-gate report delivered | **MET** |

**9 MET · 4 PARTIAL · 4 NOT MET.**

## 7. Recommendation

Complete the gate in this order — smallest-first, highest-value-first:

1. **The three preserved-rule tests** (criterion 11). Small, no dataset needed, and they close the gap where money-and-quantity rules are protected only by prose.
2. **The SQL guard companions** (criteria 6, 7, 8) — three tests, turning manual verification into enforcement.
3. **The Roslyn analyzer, CBA001 only**, behind the dogfood check (criteria 1, 2).
4. **Batch P datasets and the 24 invariants** (criterion 10) — before Batch C, not before Batch A.
5. **CI wiring** once the CI system is confirmed (criterion 4).

**Batch A may reasonably begin in parallel with 1–3**, because it touches no Master Data and the analyzer protects
against *new* debt rather than existing debt. That is a scheduling judgement for you — the strict reading of the gate is
that nothing begins until all 17 are met, and I am not treating my own recommendation as approval.

**Two decisions still outstanding:** approve the read-only measurement (§`Stage-002A-Read-Only-Measurement-Approval-Package.md`
§8 — a **restored copy is recommended over `CrossBuyDB2`**), and confirm the CI system for §3.4.

**Stopping for review. Batch A not started.**
