# Stage-Construction-CR02 — Subcontractor Certification Cap: Evidence

**Severity:** Critical · **Business decision:** D-07 — **HARD BLOCK** · **Status:** remediated and mutation-proved

---

## 1. The defect, exactly

`Models/Context/Accounting/Subcontract.cs` — `SubcontractBilling` has **no lines at all**. The certified amount is a
single free-typed decimal on the header:

```csharp
public decimal CumulativeWork { get; set; }     // cumulative work value entered to date
public decimal GrossWork { get; set; }          // W = cumulative − previously billed
```

`BL/SubcontractBillingService.Compute` then does the arithmetic and nothing else:

```csharp
decimal w = R(Math.Max(0m, cumulative - prevBilled));
```

There is **no comparison to `Subcontract.ContractValue`**, no allocated scope, and no per-item quantity anywhere. A
subcontractor could therefore be certified — and, on posting, invoiced through `PayableService` — for **any amount**,
and nothing in the system would notice.

## 2. The remediation

Two new entities and one rule.

**`SubcontractScopes` — the cap.**

| Field | Role |
|---|---|
| `AssignedQuantity` | what the subcontract was let for |
| `ApprovedVariationQuantity` | additional quantity authorised by an **approved** variation |
| `LastCapVariationOrderId` | which variation authorised it |
| `SubRate` | the subcontractor's rate (commercial data) |
| `CappedQuantity` (derived) | `Assigned + ApprovedVariation` — never stored, so the two components cannot disagree with it |

**`SubcontractCertificateLines` — the lines that never existed.** Each carries the scope, the BOQ item, the
commercial revision in force, a **rate snapshot**, previous/current/cumulative quantity and value, and — for a
correction — the line it adjusts.

**The rule (D-07).** `CertifyLineAsync` refuses, before writing anything:

1. cumulative quantity above `CappedQuantity` — **hard block**, naming the cap and the attempted figure;
2. cumulative value above the subcontract's own `ContractValue` — an **independent** value cap, because a
   generously-set scope can still exceed what the subcontract is worth;
3. quantity above the original assignment with no variation named;
4. a certificate that is not `Draft` — previous certificates are immutable;
5. zero quantity, or a negative quantity that does not name the line it adjusts;
6. a cumulative that a correction would drive below zero;
7. a scope, certificate or subcontract belonging to another company.

**The cap moves only through `RaiseCapAsync`**, which demands a variation that exists, belongs to the same project,
and is `Approved`. Editing `AssignedQuantity` once anything is certified is refused and says so. The schema enforces
the same rule independently: `CK_SubcontractScopes_VariationQty` requires `LastCapVariationOrderId` to be present
whenever `ApprovedVariationQuantity <> 0`.

## 3. Tests (`CrossBuy.Tests/ConstructionC1SubcontractCapTests.cs`)

| # | Test | Asserts |
|---|---|---|
| 1 | `Certification_within_the_cap_succeeds_and_accumulates` | previous/cumulative carried per line; rate snapshotted |
| 2 | **`Certification_beyond_assigned_quantity_is_hard_blocked`** | refused; message names cap `1000` and attempt `1100`; **nothing written** |
| 3 | `An_approved_variation_raises_the_cap_and_then_certification_passes` | blocked → cap raised → passes; `Assigned` untouched, increase separately visible; audited as `CapRaised` naming the VO |
| 4 | `Raising_the_cap_without_a_variation_is_refused` | draft VO refused; direct edit of the assigned quantity refused |
| 5 | `Cumulative_value_cannot_exceed_the_subcontract_value` | value cap fires even when the quantity cap would allow it |
| 6 | `Zero_or_negative_certified_quantity_is_refused` (×3) | refused; zero lines written |
| 7 | `A_certificate_that_is_not_draft_cannot_receive_or_change_lines` | posted certificate refuses new lines |
| 8 | `Correction_is_made_by_an_adjustment_line_that_reduces_cumulative` | original line untouched; adjustment carries `AdjustsLineId`; cap view shows the reduced figure |
| 9 | `A_header_amount_exceeding_its_lines_is_reported_as_unreconciled` | the reconciliation guard reports lines 5000 vs header 40000 |
| 10 | `A_scope_from_another_company_cannot_be_certified` | refused; zero lines written |

## 4. Mutation proof C-02

The cap was removed:

```csharp
if (false && cumulativeQty > scope.CappedQuantity)   // cap removed
```

| Stage | Result |
|---|---|
| baseline | passed = 1, failed = 0 |
| **mutated** | **passed = 0, failed = 1** |
| restored (SHA-256 compared) | identical ✔ |
| after restore | passed = 1, failed = 0 |

## 5. Scope boundary — stated plainly

`ValidateHeaderAgainstLinesAsync` is the guard that stops a **header amount** bypassing the line controls. It exists,
it is tested (test 9), and it is **NOT wired into `SubcontractBillingService.ApproveBillingAsync` /
`PostBillingAsync` in this increment** — because those are the Accounting-posting paths and the brief forbids
changing posting behaviour here.

The honest consequence: **for a subcontract that has no scope lines yet, the legacy header-only path is still
reachable in production.** What C1 delivers is the mechanism that makes over-certification impossible *on the
line-based path*, proved by mutation, plus the reconciliation check that C6 will enforce at approval.

**C6 must wire it**, and the recommended rule is graduated: if a subcontract has any `SubcontractScopes`, lines are
mandatory and must reconcile; if it has none, the value cap against `Subcontract.ContractValue` applies. That change
alters what `ApproveBillingAsync` accepts, so it belongs to an increment that is allowed to change that path.

## 6. Residual items

| Item | Owner | Note |
|---|---|---|
| Wire the reconciliation guard into approve/post | Construction, C6 | The only remaining route to header-only over-certification |
| Scope allocation UI | Construction, C3/C6 | No UI in this increment |
| Retention/penalty on a subcontract certificate line | Construction, C6 | `Stage-Construction-06` §3 |
| M7 measurement of existing over-certification | owner | `construction_c1_contract_mapping_measurement.sql` — reports subcontracts already certified beyond their value |
