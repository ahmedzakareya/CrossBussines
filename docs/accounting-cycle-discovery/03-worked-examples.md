# 03 — Worked examples

One scenario executed end to end with real record identifiers, and fifteen specified.

> **Superseded in part by execution pass 2.** **Twelve of the sixteen scenario groups have since been executed** — the "not run" wording below applies only to pass 1. The execution record is [10-execution-pass-2.md](10-execution-pass-2.md); the current counts are in [08-gaps-and-blockers.md](08-gaps-and-blockers.md) §1.


---

## The synthetic business case

| | |
|---|---|
| Company | `CompanyID 1` in the isolated database — its existing chart of accounts, currencies and periods are reused so the examples sit in a realistic ledger |
| Marker | Every record created carries **`ZZ-DISCOVERY`** in its description |
| Date | **2026-09-21**, inside open period 9 (2026-09-01 → 2026-09-30) |
| Currency | EGP, the functional currency |
| Test assumptions | Amounts are chosen to be easy to trace. No tax rate or exchange rate is asserted as statutory — the configured values are reported as **test assumptions** |

---

## ACC-GL-01 — Manual journal: draft → post → reverse ✅ EXECUTED

**Business purpose.** Record an expense that has no source document in another module, then
demonstrate the only supported way to undo it.
**Responsible role.** Accountant to create; chief accountant to post when the entry is material
(see §Posting control below).
**Prerequisites.** An open fiscal period covering the entry date; two postable accounts; neither
account may require a cost centre unless one is supplied.
**Starting balances.** Trial balance before this discovery: **113,027,021.50** Dr = Cr.

### Steps as performed

| # | Navigation / control | Value entered | Observed result |
|---|---|---|---|
| 1 | **Accounting → General → Journals → New** (`/Accounting/CreateJournal`) | — | Heading *"New Journal Entry"*, one empty line, **Save draft** and **Post** both present |
| 2 | `Date` (`#entryDate`) | `2026-09-21` | accepted |
| 3 | `Description` | `ZZ-DISCOVERY GL-01 September office rent` | accepted |
| 4 | **Add line** (`#addRow`) ×1 | — | second line added |
| 5 | Line 1 `Account` | `510105 — Rent Expense` | selected |
| 6 | Line 1 `Debit` | `5000` | total updates live |
| 7 | Line 2 `Account` | `110102 — Bank - Current` | selected |
| 8 | Line 2 `Credit` | `5000` | footer shows `Total 5000.00 / 5000.00` and a green **Balanced ✓** badge |
| 9 | **Save draft** | — | redirect to `/Accounting/Journals` |
| 10 | **Post** (`/Accounting/PostJournal`) | id = 24213 | redirect to `/Accounting/Journals` |
| 11 | **Reverse** (`/Accounting/ReverseJournal`) | id = 24213 | redirect to `/Accounting/Journals` |

Screenshots: `ACC-GL-01-01` … `ACC-GL-01-06`, in **both** languages. The English set shows
JE **24213**; the Arabic set shows JE **24214** (reversed by 24216).

### State before and after — read from the database, not from the screen

**After step 9 (draft):**

| ID | EntryNo | Status | FiscalPeriodId | JournalType | SourceType | Dr | Cr |
|---|---|---|---|---|---|---|---|
| 24213 | **NULL** | **Draft** | 9 | Manual | NULL | 5000.00 | 5000.00 |

> **A draft has no entry number.** The number is allocated at posting. A guide that tells a user
> to "note the journal number after saving" would be wrong.

**After steps 10 and 11:**

| ID | EntryNo | Status | JournalType | ReversedByEntryId | PostedBy |
|---|---|---|---|---|---|
| 24213 | `JV-2026-008513` | **Reversed** | Manual | **24215** | 5 |
| 24215 | `JV-2026-008514` | **Posted** | **Reversing** | — | — |

### The actual journal lines

**24213 — `JV-2026-008513`** *(the record shown in the English screenshots)*

| Account | Name | Debit | Credit |
|---|---|---:|---:|
| 510105 | Rent Expense | 5,000.00 | |
| 110102 | Bank - Current | | 5,000.00 |

**24215 — `JV-2026-008514`** (type `Reversing`, description auto-generated: «قيد عكسي JV-2026-008513»)

| Account | Name | Debit | Credit |
|---|---|---:|---:|
| 510105 | Rent Expense | | 5,000.00 |
| 110102 | Bank - Current | 5,000.00 | |

Analytical dimensions: none — no cost centre or project was supplied, and neither account
requires one. Currency: EGP (functional); no rate applied.

### Effect on balances

| | Before | After post | After reverse |
|---|---:|---:|---:|
| 510105 Rent Expense | *n* | *n* + 5,000 | *n* |
| 110102 Bank - Current | *n* | *n* − 5,000 | *n* |
| Trial balance total | 113,027,021.50 | +5,000 per run | +10,000 per run |

### Report that proves it

**Trial balance** (`ACC-RPT-01`).

| | |
|---|---:|
| Before this discovery (captured 2026-09-20) | 113,027,021.50 |
| After it (captured 2026-09-21) | **113,067,021.50** |
| **Movement** | **40,000.00** |

The scenario was executed **four times** — English and Arabic, twice each — producing eight
postings (four originals and four reversals) of 5,000.00. **8 x 5,000 = 40,000.** Verified
independently by SQL over `JournalEntryLines`: `SUM(Debit) = SUM(Credit) = 113,067,021.50`,
difference `0.00`, across 11,789 entries.

**This is a complete trace from a sample transaction to a report total** — and the behaviour
reproduced identically on all four runs, which is stronger than a single observation.

### Reversibility and its limits

- Reversal **does not delete**. It writes a new, immediately-posted entry of type `Reversing`
  and sets `ReversedByEntryId` on the original, whose status becomes `Reversed`.
- Both entries remain in the trial balance and net to zero. The audit trail is preserved.
- The reversing entry has **no draft stage** — it is posted the moment it is created.
- The reversing entry's description is generated **in Arabic only** («قيد عكسي …»), including in
  the English interface. Untranslated UI, flagged for the guide.
- `ReverseJournal` requires the accounting `post` permission.

### Posting control — what the code enforces, and what this data enables

`AccountingController.PostJournal` (repository evidence):

```csharp
var threshold = await ApprovalThresholdAsync();
if (threshold > 0 && entry.Total >= threshold) {
    if (!await IsChiefAsync())  → "the chief accountant's approval is required"
    if (entry.CreatedBy == empId) → "Segregation of duties: the entry's creator cannot approve it"
}
```

On create, the same threshold **silently downgrades a Post to a Draft**
(`AccountingController:1163`: `if (post && threshold > 0 && total >= threshold) { post = false; forcedDraft = true; }`).

**In this database `AccountingSettings.ApprovalThreshold = 0.00`**, so `threshold > 0` is false
and neither control fires. The segregation-of-duties control is **implemented and disabled by
configuration** — the guide must say so rather than describing a protection that is not active.

On success the message is **"Posted and approved"** — in this product, for journals, approving
and posting are the same act.

---

## The fifteen scenarios that were **not** executed

Each is specified so it can be run, but **no outcome below has been observed** — do not present
any of it as a result.

| ID | Scenario | Why not run | What exists as evidence instead |
|---|---|---|---|
| ACC-OB-01 | Opening balances (GL, customer, supplier, stock) | not run | Source types `OpeningGL`, `OpeningARBalance`, `OpeningAPBalance`, `OpeningAsset` declared in code, **no entries**; `OpeningStock` has **148 entries** in the data |
| ACC-P2P-01 | Credit purchase → goods receipt → supplier invoice | not run | 121 `PurchaseInvoice` and 967 `Inventory` entries exist; account `210203 Goods Received Not Invoiced` exists |
| ACC-P2P-02 | Partial supplier payment → final settlement | not run | 4 `Payment` entries exist |
| ACC-P2P-03 | Purchase return and its settlement | not run | 30 `PurchaseReturn` entries exist |
| ACC-O2C-01 | Credit sale → delivery → customer invoice | not run | 5,519 `SalesInvoice` entries exist |
| ACC-O2C-02 | Partial receipt → final settlement | not run | 4,403 `Receipt` entries exist |
| ACC-O2C-03 | Sales return and its settlement | not run | 26 `SalesReturn` entries exist |
| ACC-BNK-01 | Cash/bank transaction and reconciliation | not run | 4 `BankReconTest` entries exist; `Transfer` declared, no data |
| ACC-INV-01 | Inventory count difference / adjustment | not run | 6 `StockReconcile`, 15 `StockWriteOff` entries exist |
| ACC-FA-01 | Asset purchase and depreciation | not run | 1 `AssetCapitalization`; `Depreciation` declared, **no entries** |
| ACC-PAY-01 | Payroll posting and payment | not run | 2 `Payroll`; `PayrollPay` declared, **no entries** |
| ACC-FX-01 | Foreign-currency invoice, settlement, revaluation | not run | 3 `FxRevaluation` + 3 `FxRevaluationReversal` exist; realised FX on settlement **not evidenced** |
| ACC-ADJ-01 | Accrual / prepayment adjustment | not run | `Adjustment` declared, **no entries** |
| ACC-CLS-01 | Period close and financial statements | not run | Statements captured; closing not performed |
| ACC-CLS-02 | Year-end close | not run | `YearClose` declared, **no entries** — never run in this data |
| ACC-COR-01 | Correction of a posted mistake | **partly covered** by ACC-GL-01's reversal | Edit-then-repost (`SalesInvoiceEdit`, `PurchaseInvoiceEdit`) not exercised |

### Connected-module scenarios

| Module | Status |
|---|---|
| **POS / restaurant** | `PosShiftClose` 24, `PosTip` 24, `PosRefund` 21 entries exist — integration is real. `PosPrep` and `PosWaste` declared, no data. **Not executed.** |
| **Projects** | `ProjectLabor` 1 entry. `ProjectAdvance`, `RetentionRelease`, `SubRetentionRelease` declared, **no data**. **Not executed.** |
| **Manufacturing** | `WorkOrder` 115 entries — the integration posts. WIP account `1105 Work in progress` exists. Variance accounting **not found** as a distinct source type. **Not executed.** |

---

## How to run the rest

The harness is in place and each piece is reusable:

```bash
# start the isolated instance
C:\temp\cb_acct_iso\run-isolated.cmd

# a scenario, per language
set CB_USER=…  CB_PASS=…
CB_BASE=http://localhost:5299 CB_LANG=ar \
  node <browser-automation>/browser.mjs http://localhost:5299/Account/Login \
       --script scripts/sc-gl01-journal.mjs

# rebuild every inventory from whatever evidence exists
python scripts/build_inventories.py
```

`scripts/lib-ready.mjs` supplies the readiness gate, the capture recorder and the evidence log;
a new scenario only has to describe its own steps. Estimated effort for the remaining fifteen:
**one to two days**, most of it in the P2P and O2C chains, which need synthetic master data
(one supplier, one customer, one item) created first.
