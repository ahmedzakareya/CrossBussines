# Stage 2A — Batch P — Non-Regression Baseline

**Status: Partial — 3 of the 24 Master Data invariants are mechanically enforced.** The three enforced are the
**preserved behavioural rules**, chosen first because they were the only ones protecting money and quantity with
nothing but prose.

Detail of the isolation work: `Stage-002A-Batch-P-Isolation-Fix-Report.md`.

---

## 1. What Batch P is for

Capture the **pre-change** baseline so a later Master Data migration can be proved not to alter stock, COGS, POS,
pricing, Hypermarket or manufacturing behaviour. The baseline must exist **before** Batch C touches Master Data —
running it afterwards would measure the wrong starting point.

## 2. Enforced now — the three preserved rules

| Rule | Test class | Risk |
|---|---|---|
| **P-01** a missing UoM conversion rejects; **no factor-1 fallback**; an inverse-only row does not satisfy the guard | `BatchPPreservedRuleTests` | RISK-048 |
| **P-02** an ambiguous barcode is detectable and must be refused; **first-match is order-dependent and prohibited** | same | RISK-050 |
| **P-03** `ItemBarcode.UoMId` controls the sold unit — EACH sells 1, CASE sells 12, and remapping changes 12 → 1 | same | RISK-046 |

Each mirrors the predicate the production path evaluates (`PosOrderService.cs:545-549`;
`HyperPosController.Scan:245-259`; `IntegrityCheckService:286`). **7 tests, 0 skipped**, both mutation proofs verified.

## 3. The defect this batch created and fixed

The first version called `EnsureEfSchemaAsync` on the **shared** platform fixture, breaking three
`PlatformSchemaDeploymentTests` — **RISK-036, reproduced by the batch that documented it**. The tests were temporarily
**skipped** so the suite stayed honest rather than red, which left the entry gate unmet because a skipped test is not
evidence.

**Fixed** by a dedicated probe database with an ownership marker, confirmed teardown, and a purity assertion. RISK-036 is
now mechanically mitigated for this class, and the shared helper (`SqlServerFixture.CreateProbeDatabaseAsync`) exists so
the other three SQL families can adopt it.

## 4. Canonical dataset (deterministic)

EACH as base unit · CASE = 12 EACH · one item with an EACH barcode and a CASE barcode · a second item sharing one
barcode value (the ambiguity case) · a unit with **no** conversion · a unit with an **inverse-only** conversion.
Fixed codes (`BP-PEN`, `BP-NBK`, `BP-EA-0001`, `BP-CS-0001`, `BP-AMBIG-01`), rebuilt per test so no test inherits
another's rows.

## 5. Not yet enforced — the remaining 21 invariants

Moving-average valuation · stock quantities · movement history · journal output · opening balances · serial balances ·
batch balances · bin balances · UoM rounding · composite explosion quantities · historical composite cost · POS
calculations · Hypermarket calculations · pricing · tax · manufacturing calculations · FEFO allocation · COGS · receipt
quantities · invoice quantities · return and cancellation consistency.

*Missing work:* the 23 canonical dataset shapes and machine-readable expected-output artifacts with deterministic hashes,
zero-tolerance fields, and currency precision (KWD 3-decimal explicit where applicable).
*Impact:* **Batch C must not modify Master Data until these are captured.** The three enforced rules cover the
behavioural traps; the 21 cover the numeric baseline.
*Next step:* datasets first (they are shared by most invariants), then the invariants in dependency order — valuation
and quantities before COGS and pricing, since the latter derive from the former.

## 6. Test properties

Mandatory · non-skippable · SQL Server based · **isolated probe database** · deterministic · order-independent ·
repeatable · cleanup-verified · listed individually in `engineering/required-evidence-manifest.json`
(**31 enforced tests across 10 groups**).

## 7. Verification

| Check | Result |
|---|---|
| Batch P | **7 passed · 0 failed · 0 skipped** |
| `PlatformSchemaDeploymentTests` | **9 passed** |
| Full suite ×2 | **779 passed · 0 failed · 0 skipped** (identical) |
| Mutation A / B | **2 failures / 1 failure**, then restored |
| Shared fixture fingerprint | **unchanged**, asserted |
| Probes remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |
