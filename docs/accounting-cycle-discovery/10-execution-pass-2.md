# 10 — Execution pass 2

The connected worked examples, executed. This document is the record of what was actually run,
what it produced, and what refused.

**Isolation revalidated before any write**: process 39228 (later 5088 after the checkpoint
restore) held sessions on `CrossBuyAcctTest` only, zero on `CrossBuyDev`; no external
connections; the developer's instance on `:44368` answered 200 throughout.

---

## 1. Checkpoints

| Checkpoint | Taken | Contents |
|---|---|---|
| `CP1_pre_execution.bak` | before pass 2 | the state after pass 1 (journal loop only) |
| `CP2_post_execution_pre_closing.bak` | after the transactions, before closing | **the state the database is in now** |

The closing scenarios are destructive — a closed year refuses further posting — so they were run
**after CP2** and **CP2 was then restored**. FY2026 is `Open` again and the year-end entry is
gone from the live database; its evidence is in §7 and in the screenshots.

## 2. Synthetic master data

| Record | Id | Name |
|---|---|---|
| Supplier | **3015** | `ZZ-DISCOVERY Supplier Co` / ZZ-DISCOVERY مورّد الاكتشاف |
| Customer | **9047** | `ZZ-DISCOVERY Customer Co` / ZZ-DISCOVERY عميل الاكتشاف |
| Fixed asset | **2026** | `ZZ-DISCOVERY Test Machine`, cost 24,000.00, life 48 months |

Test assumptions, not statutory advice: **VAT 14%**, functional currency **EGP**, all documents
dated **2026-09-21** inside open period 9.

## 3. Purchasing — executed

| Step | Document | Amount | Journal | Posting |
|---|---|---|---|---|
| Purchase invoice (expense line) | **PI-2026-05073** | 1,140.00 | `JV-2026-008517` | Dr 510105 Expense 1,000 + Dr 110401 VAT Input 140 / Cr 2101 AP 1,140 |
| Partial payment | **PY-2026-00008** | 500.00 | `JV-2026-008518` | Dr AP 500 / Cr 110102 Bank 500 |
| Final settlement | **PY-2026-00009** | 640.00 | `JV-2026-008519` | Dr AP 640 / Cr Bank 640 |
| Purchase invoice (**stock** line) | **PI-2026-05074** | 1,140.00 | `JV-2026-008524` | Dr **1103 Inventory** 1,000 + VAT In 140 / Cr AP 1,140 |
| Purchase return | **DN-2026-03016** | **173.30** | `JV-2026-008526` | Dr AP 173.30 / Cr **210203 GRNI** 152.02 + Cr VAT Input 21.28 |

**Arithmetic**: 1,000 + 14% = 1,140 ✓; settled 500 + 640 = 1,140 ✓.

### Three findings from purchasing

**Setting an item changes the debit account.** The same form, the same expense account id: with
`itemId` null it debits `510105 Rent Expense`; with `itemId` set it debits `1103 Inventory`. The
item, not the chosen account, decides.

**A purchase return is valued at moving-average cost, not the invoice price.** Five units bought
at 50.00 were credited back at **152.02**, because the average cost after the purchase was
30.4040 — `(970 × 30.00 + 20 × 50.00) ÷ 990`. Stock moved 970 → 990 → **985**, value
**29,947.98 = 985 × 30.4040** ✓. The screen says so in its own note — *"Value is computed at the
item's cost"* — and the guide must repeat it, because a user expecting 250.00 will be surprised
by 152.02.

**A purchase return credits GRNI (210203), not Inventory (1103).** Observed, recorded as
observed; this package does not attempt to justify it.

## 4. Sales — executed

| Step | Document | Amount | Journal | Posting |
|---|---|---|---|---|
| Sales invoice | **SV-2026-17613** | 1,710.00 | `JV-2026-008520` | Dr 1102 AR 1,710 / Cr 4101 Revenue 1,500 + Cr 210201 VAT Output 210 |
| Partial receipt | **RC-2026-15485** | 1,000.00 | `JV-2026-008521` | Dr Bank 1,000 / Cr AR 1,000 |
| Final collection | **RC-2026-15486** | 710.00 | `JV-2026-008522` | Dr Bank 710 / Cr AR 710 |
| Sales return | **CN-2026-04050** | 342.00 | `JV-2026-008523` | credit note |

**Arithmetic**: 1,500 + 14% = 1,710 ✓; collected 1,000 + 710 = 1,710 ✓.

### The asymmetry between the two returns

The sales return accepted a **service line** (revenue account, no item). The purchase return
**refused** one:

```csharp
// PayableService.CreatePurchaseReturnAsync
var itemLines = lines.Where(l => l.ItemId != null && l.WarehouseId != null && l.Qty > 0);
if (itemLines.Count == 0)
    return (false, "The return must contain at least one item line (with a warehouse)", null);
```

The first purchase-return attempt failed on exactly this and is recorded as a failure rather than
hidden: the request returned to `/Accounting/NewPurchaseReturn`, which is how this controller
signals a refusal. A stocked purchase invoice was then created so the return had something to
issue out.

> **This is why "landed on a success URL" is not proof.** The first attempt's HTTP round-trip
> succeeded; the business operation did not.

## 5. Closing party balances

| Party | Invoices | Settled | Returns | Net |
|---|---:|---:|---:|---:|
| Customer 9047 | 1,710.00 | 1,710.00 | 342.00 | **0.00 — fully settled** |
| Supplier 3015 | 2,280.00 | 1,140.00 | 173.30 | **1,140.00 outstanding** (the stock invoice is deliberately left open) |

**A fully-settled party disappears from the aging report.** `ReceivableService.AgingAsync` ends
with `if (row.Total > 0) rows.Add(row)`. Once the customer reached zero it left the AR aging
entirely — which is correct, and is also why one capture failed its readiness gate (§9).

## 6. Treasury, assets and foreign currency — executed

| Scenario | Journal | Posting |
|---|---|---|
| **Cash/bank transfer** 2,500.00 | `JV-2026-008527` | Dr 110101 Main Cash 2,500 / Cr 110102 Bank 2,500 |
| **Asset capitalisation** 24,000.00 | `JV-2026-008528` | Dr 1201 Assets at Cost 24,000 / Cr Bank 24,000 |
| **Depreciation run** | `JV-2026-008529` | **one journal, 7 asset line-pairs**, total 12,433.34 — Dr 520103 Depreciation Expense / Cr 1202 Accumulated Depreciation |
| **FX revaluation** | `JV-2026-008530` | Dr 1102 AR 63,840 + 10,000 / Cr 4903 Unrealised FX Gain; Cr 2101 AP 67,863.74 / Dr 5903 |
| **FX revaluation reversal** | `JV-2026-008531` | the exact mirror |

**Depreciation is one entry for all assets**, exactly as the screen states. My asset contributes
**500.00** — `24,000 ÷ 48` — and that line is present in the 7.

**The FX pair cancels exactly**, which is the behaviour an unrealised revaluation should have:
recognised at period end, reversed at the start of the next.

### Payroll — attempted and REFUSED

`PostPayroll(2026, 9)` produced no entry. The guard that fires is identifiable from source:

```csharp
if (pre.Employees.Count == 0 || pre.TotalGross <= 0)
    return (false, "There is no postable payroll in this period", null);
```

**Blocker: the development data has no September 2026 payroll to post.** 27 employees exist, but
no computed run for that period. Producing one means creating salary policies and attendance for
27 employees — a large data change outside the accounting scope, and it is left undone rather
than faked.

## 7. Closing — executed on CP2, then rolled back

| Scenario | Result |
|---|---|
| **Close period 9** | ✅ `Status = Closed`, `ClosedBy = 5`, `ClosedAt` recorded |
| **Post into the closed period** | ✅ **REFUSED** — the journal stayed on `/Accounting/CreateJournal` instead of redirecting to the register. Captured as an intentional error state |
| **Reopen with a reason** | ✅ `ReopenedAt`, `ReopenedBy` and `ReopenReason` all written |
| **Year-end close FY2026** | ✅ FY2026 → `Closed`; `JV-2026-008532` dated **2026-12-31** |

### The year-end entry

**15 lines, Dr = Cr = 58,028,927.40**, closing revenue and expense accounts to
**`3201 Retained Earnings` 57,972,617.03**.

It is dated **31 December**, not the run date. And my synthetic **Rent Expense of 1,000.00**
appears in it — a synthetic purchase made on 21 September is traceable into the year-end close.

The correct endpoint is `SetPeriodStatus(id, status, reason, overrideWarnings)`; there is no
`ClosePeriod` action. Both period permissions are `NeverBootstrapOpen` in source.

**CP2 was restored afterwards**, so the live isolated database is back to FY2026 `Open` with the
transactions intact and no year-end entry.

## 8. What was NOT executed, and why

| Area | Status | Blocker |
|---|---|---|
| Opening balances (GL / AR / AP / assets) | **not executed** | `OpeningGL`, `OpeningARBalance`, `OpeningAPBalance`, `OpeningAsset` have no screen reached in this pass; only `OpeningStock` has existing data |
| Payroll posting and disbursement | **attempted, refused** | no September 2026 payroll data (§6) |
| Accrual / prepayment adjustment | **not executed** | `Adjustment` is declared with no entries and no dedicated screen was located |
| Bank reconciliation | **not executed** | `/Accounting/Reconcile` renders; no statement was reconciled |
| Inventory count / adjustment | **not executed** | the stock side moved only as a by-product of the purchase and return |
| POS, projects, manufacturing | **not executed** | these post from their own modules; their existing ledger data is evidence that the integrations work, not that this pass exercised them |
| Realised FX on settlement | **not executed** | requires a foreign-currency invoice settled at a different rate; only unrealised revaluation was run |

## 9. Capture quality in this pass

| | |
|---|---|
| Captures written | 80 (49 English, 31 Arabic) |
| Readiness-gate failures | **1**, and it is explained below |
| Duplicate groups | **2**, both structural and explained |

**The one failure** — `ACC-O2C-02-02_remaining-balance` (English) expected the text
`ZZ-DISCOVERY` on the AR aging page and did not find it. The customer had settled to zero and the
aging drops zero-balance parties (§5). **No file was written**, which is the gate working: a
screenshot of an aging report that does not contain the record it claims to show would be
misleading evidence.

**The two duplicates** are the same structural cause: a step's result screen *is* a report
screen.

| Group | Files | Why |
|---|---|---|
| DUP-01 | `ACC-GL-01-06_after-reverse.ar` = `ACC-RPT-07_journals.ar` | reversal redirects to `/Accounting/Journals`, which is the journal register report |
| DUP-02 | `ACC-O2C-02-02_remaining-balance.ar` = `ACC-RPT-05_ar-aging.ar` | the "remaining balance" view *is* the AR aging report |

Both pairs must be **cropped differently** in the guide rather than printed twice.

### Arabic evidence method

The transactions were executed once, in English. Re-running them in Arabic would have created
duplicate documents and corrupted the worked-example ledger, so the Arabic pass captured the
**result screens** showing the **same records** in the Arabic interface — 16 screens, all
`dir="rtl"`, all through the same readiness gate.
