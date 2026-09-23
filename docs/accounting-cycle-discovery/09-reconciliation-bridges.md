# 09 — Reconciliation bridges

The AR and AP differences, traced to their causes and closed numerically.

**Status: both bridges close. AR closes exactly. AP closes to 74.20, which is itself identified
and quantified.**

---

## 0. Alignment before comparison

Both sides were put on the same basis before anything was compared:

| Dimension | Setting |
|---|---|
| Company | `CompanyID = 1` on both sides (the aging report is hardcoded to `DefaultCompanyId = 1`) |
| Branch | not a dimension of either the aging report or the control account |
| As-of date | aging uses `DateTime.UtcNow`; the control account is all-time. **No posting in this data is dated in the future**, so the two bases coincide |
| Posting status | aging: documents with `Status = 'Posted'` only. Control: journal entries with `Status IN ('Posted','Reversed')` |
| Currency | both in the functional currency — aging uses `GrandTotalBase ?? GrandTotal` and `AmountBase ?? Amount` |
| Account mapping | AR control = account `1102`, AP control = `2101`, confirmed by the source-type breakdown below |

**Method.** The aging algorithm was read from `ReceivableService.AgingAsync` and then
**re-implemented in SQL**. The re-implementation reproduces the reported totals exactly
(18,536,202.26 and 651,508.46), which is what licenses the rest of the analysis.

### The algorithm, as coded

```csharp
// ReceivableService.AgingAsync — the shape that matters
invoices = SalesInvoices where CompanyID = c and Status == "Posted"
receipts = Receipts      where CompanyID = c and Status == "Posted"
foreach customer:
    if (custInv.Count == 0) continue;            // ← a party with NO invoice is SKIPPED ENTIRELY
    paid = sum(receipts.AmountBase ?? Amount)     //   even if it has payments
    FIFO-apply paid to invoices oldest-first
    if (row.Total > 0) rows.Add(row);             // ← a party in net credit is DROPPED
```

Two structural consequences, and both turn out to matter:

1. **Sales/purchase returns never enter the calculation.** Only invoices less receipts.
2. **A party with payments but no posted invoice is skipped**, so its payments are invisible to
   the report while the control account is still relieved by them.

---

## 1. Accounts receivable — closes exactly

| | Amount |
|---|---:|
| **AR aging report total** | **18,536,202.26** |
| less: posted **sales returns** (credit notes), base currency — never deducted by the aging | **(15,939.00)** |
| **= AR control account 1102, net** | **18,520,263.26** ✓ |

**Difference explained in full: 15,939.00, to the cent.**

Checks performed and their results:

| Candidate | Result |
|---|---|
| Customers dropped for a net credit balance | **0 customers, 0.00** — not a factor |
| Draft sales invoices | 6 exist; `GrandTotalBase` is `NULL`; excluded by the `Status='Posted'` filter on both sides |
| SQL re-implementation of the aging | **18,536,202.26 — matches the report exactly** |
| Posted sales returns, base | **15,939.00 — equals the difference exactly** |

**Root cause: the AR aging report does not subtract credit notes.** The control account does.
This is a report-definition difference, not a data error, and the guide must say so: *the aging
report shows gross exposure before credit notes.*

## 2. Accounts payable — closes to 74.20

| | Amount |
|---|---:|
| **AP aging report total** | **651,508.46** |
| less: payments to vendors with **no posted purchase invoice** — the aging skips those vendors entirely | **(15,700.00)** |
| less: posted **purchase returns** (debit notes) on account 2101 | **(650.37)** |
| add: residual between purchase-invoice **journals** and **documents**, net of reversals | **+74.20** |
| **= AP control account 2101, net credit** | **635,232.29** ✓ |

`651,508.46 − 15,700.00 − 650.37 + 74.20 = 635,232.29`

### The 15,700.00, itemised

| Vendor | Posted payments | Has a posted invoice? | Counted by the aging? |
|---|---:|---|---|
| 1 | 5,000.00 | yes | **yes** — deducted |
| 2 | 3,200.00 | **no** | no |
| 3 | 8,000.00 | **no** | no |
| 4 | 4,500.00 | **no** | no |
| | **15,700.00 skipped** | | |

These are payments on account — advances to suppliers with nothing yet invoiced. The ledger
debits AP; the report cannot see them because `custInv.Count == 0` skips the vendor before its
payments are read.

### The 74.20 residual

The AP control account's movement, decomposed by source type:

| Source | Debit | Credit | Net credit |
|---|---:|---:|---:|
| `PurchaseInvoice` | 0.00 | 918,239.66 | +918,239.66 |
| `FxRevaluation` | 0.00 | 73,791.74 | +73,791.74 |
| `FxRevaluationReversal` | 73,791.74 | 0.00 | −73,791.74 |
| `PurchaseReturn` | 650.37 | 0.00 | −650.37 |
| `Payment` | 20,700.00 | 0.00 | −20,700.00 |
| `Reversal` | 261,926.20 | 269.20 | −261,657.00 |
| **Total** | | | **635,232.29** ✓ |

Purchase-invoice journals credit AP **918,239.66**; reversals debit back **261,657.00**; net
**656,582.66**. The purchase-invoice *documents* total **656,508.46**. The gap is **74.20**.

Nine purchase-invoice journals carry `Status = 'Reversed'`, and the reversal pairs leave a net
**249.20** credit on 2101 rather than zero — so the reversals are not perfectly self-cancelling
on this account.

> **This 74.20 is NOT resolved.** It is isolated to the reversed purchase-invoice journals and is
> quantified, but the individual document that causes it has not been identified. Evidence still
> missing: a line-by-line comparison of each of the nine reversed purchase-invoice journals
> against its document header. **No balancing journal was created and no data was altered.**

Note the FX pair: `FxRevaluation +73,791.74` and `FxRevaluationReversal −73,791.74` cancel
exactly, which is the expected behaviour of an unrealised revaluation reversed in the following
period — evidence that the FX mechanism works on this account.

---

## 3. Pre-existing versus newly caused

**Every difference above is pre-existing.** The transactions executed by this discovery
(journals 24209–24216) touch only accounts `510105 Rent Expense` and `110102 Bank - Current`.
Neither AR (1102) nor AP (2101) is affected, and both differences were measured before and after
the execution pass with identical values.

| | Before the execution pass | After |
|---|---:|---:|
| AR difference | 15,939.00 | 15,939.00 |
| AP difference | 16,276.17 | 16,276.17 |

---

## 4. What this corrects in the earlier package

Document 05 recorded both differences as **"root cause NOT ESTABLISHED"**. That is now
superseded:

- **AR: fully explained** — sales returns are not deducted by the aging report.
- **AP: 16,201.97 of 16,276.17 explained** (15,700.00 supplier payments on account + 650.37
  purchase returns − the 74.20 reversal residual); **74.20 remains open and is localised.**

Neither was a data error. Both are **report-definition differences** — which is a materially
different finding, and one the guide must teach rather than hide: *the aging reports answer
"what is outstanding on invoices", not "what is the control-account balance".*
