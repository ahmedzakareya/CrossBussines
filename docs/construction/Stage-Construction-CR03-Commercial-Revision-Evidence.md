# Stage-Construction-CR03 — Immutable Commercial Revisions: Evidence

**Severity:** Critical · **Status:** remediated and mutation-proved in C1

---

## 1. The defect, exactly

`BL/VariationOrderService.ApproveAsync` applied an `Adjust` line by writing straight onto the live BOQ row:

```csharp
var it = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == l.BoqItemId!.Value && …);
l.OldQuantity = it.Quantity; l.OldUnitPrice = it.UnitPrice;   // snapshot kept ONLY on the variation line
it.Quantity = l.Quantity; it.UnitPrice = l.UnitPrice;          // <-- the contractual values, overwritten in place
```

And a certificate line (`Models/Context/Accounting/ProgressBilling.cs`) carries **no revision and no rate**:

```csharp
public int? BoqItemId { get; set; }
public decimal CumulativeExecutedValue { get; set; }
public decimal PreviouslyBilledValue { get; set; }
public decimal PeriodValue { get; set; }
```

**The consequence.** After a variation, a certificate that was posted at the old rate could no longer be reproduced
from its own data. Its `BoqItemId` now resolved to a line carrying *different* commercial values, so any report,
statement or dispute that recomputed it produced a number the employer never certified. Combined with CR-01, the
certified history was not reproducible at all.

## 2. The remediation

**`CommercialRevisions` + `CommercialRevisionLines`** — the only way a contractual quantity or rate may change.

| Element | Rule |
|---|---|
| `Kind` | `Original` (the as-awarded baseline) \| `Revised` |
| `Source` | `Contract` \| `Variation` \| `Correction`; a `Variation` source **must** name an approved variation (service check + `CK_CommercialRevisions_Variation_Source`) |
| `EffectiveDate` | separate from `ApprovedAt` — a variation approved today may take effect from an earlier instruction date |
| `Status` | `Draft` → `Approved` → `Superseded`. **An approved revision is immutable: no code path writes to one.** |
| `Reason` | required to approve (service check + `CK_CommercialRevisions_Approval`) |
| per line | `PreviousQuantity`, `NewQuantity`, `PreviousRate`, `NewRate`, `QuantityImpact`, `ValueImpact`, `ChangeKind` |
| supersession | approving a revision sets the previous one to `Superseded` and stamps `SupersededByRevisionId` |
| one draft at a time | `UX_CommercialRevisions_OneDraftPerProject` — two concurrent drafts would make "the revision in force" ambiguous the moment both were approved |

At approval the previous values are **re-snapshotted from what the line holds now**, not from what it held when the
change was staged. Staging may be hours old; the truth of "previous" is the current value.

**`CertificateLineSnapshots`** — what a client certificate line was certified against: the revision id, the
contracted quantity and rate, and the certified quantity and value. `UX_CLS_Line` makes it one snapshot per line,
forever; `CaptureCertificateLineSnapshotAsync` refuses to overwrite an existing one. A snapshot that can be rewritten
is a cache, not evidence.

`ReproduceCertificateLineAsync` reads **only** the snapshot. It never touches `BoqItems`.

**`OpenBaselineAsync`** exists so a certificate raised on day one has a revision to point at — without an as-awarded
baseline, "the revision in force" would be null exactly when it matters most.

## 3. Tests (`CrossBuy.Tests/ConstructionC1CommercialRevisionTests.cs`)

| # | Test | Asserts |
|---|---|---|
| 1 | `Approving_a_revision_applies_new_values_and_preserves_previous_ones` | BOQ carries new values; revision holds previous; impacts and totals exact (500 → 720, impact 220); state points at the revision |
| 2 | `An_approved_revision_is_immutable` | staging refused, re-approval refused, values untouched, applied exactly once |
| 3 | `A_revision_cannot_be_approved_without_a_reason` (null/""/"  ") | refused; nothing applied |
| 4 | **`A_posted_certificate_reproduces_the_rate_it_was_certified_at`** | live BOQ moved 5 → 10; reproduction still returns rate 5, quantity 100, value 250, and the **baseline** revision id |
| 5 | `A_new_approved_revision_supersedes_the_previous_one` | 1→2→3 chain, `SupersededByRevisionId` links, exactly one in force |
| 6 | `A_revision_cannot_stage_a_line_from_another_project` | refused; zero lines staged |
| 7 | `Approving_a_revision_audits_the_old_and_new_commercial_values` | old/new numerics, revision id, actor, reason, one correlation id |

## 4. Mutation proof C-03

Reproduction was made to re-read the live BOQ — the pre-C1 behaviour:

```csharp
var mutantItem = await _db.BoqItems.AsNoTracking().FirstOrDefaultAsync(b => b.ID == s.BoqItemId && …);
if (mutantItem != null)
    return new ReproducedCommercialValue { … ContractedRate = mutantItem.UnitPrice … };
```

| Stage | Result |
|---|---|
| baseline | passed = 1, failed = 0 |
| **mutated** | **passed = 0, failed = 1** |
| restored (SHA-256 compared) | identical ✔ |
| after restore | passed = 1, failed = 0 |

The mutation is faithful to the original defect: the certificate's stated rate silently becomes whatever the BOQ says
today.

## 5. Relationship to `VariationOrderService`

`VariationOrderService.ApproveAsync` is **not modified in this increment.** It still writes BOQ values in place, and
that is deliberate: rerouting variation approval through the revision service changes the behaviour of an existing,
user-facing approval path, which belongs to C7 where variations are the subject.

What C1 delivers is the **mechanism** that makes the reroute a small change and makes the history reproducible from
the moment it lands:

- the revision entities exist, with immutability enforced in service **and** schema;
- `OpenAsync(source: Variation, sourceVariationOrderId: …)` already refuses a variation that is not approved;
- `ApproveAsync` already performs the apply-and-supersede in one transaction with full audit.

**C7 must replace the in-place write in `VariationOrderService.ApproveAsync` with a call to
`CommercialRevisionService`.** Until then, a variation approved through the old path still overwrites in place —
stated plainly rather than implied.

## 6. Residual items

| Item | Owner | Note |
|---|---|---|
| Reroute `VariationOrderService.ApproveAsync` through the revision service | Construction, C7 | The remaining in-place write |
| Capture the snapshot inside the client-certificate save/post path | Construction, C6 | The capture service exists and is tested; wiring touches the posting path |
| Baseline revisions for existing projects | owner | `OpenBaselineAsync` per project, after the D-01 contract backfill |
| M8 measurement | owner | Reports approved variations that adjusted a line in place, and how many posted certificates preceded each |
