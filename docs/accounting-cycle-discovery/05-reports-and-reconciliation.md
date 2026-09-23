# 05 — Reports and reconciliation

Nine reports executed in two languages, and what reconciling them against the ledger found.

**Machine-readable:** `reconciliation-workbook.csv`, `screenshot-manifest.csv`,
`reference/reports-en.json` / `reports-ar.json` (the figures read out of each rendered page).

---

## 1. Reports executed

All nine rendered, passed the readiness gate and were captured in **English and Arabic**.

| ID | Report | Route | Business question | Rows | Observed totals |
|---|---|---|---|---:|---|
| `ACC-RPT-01` | Trial Balance | `/Accounting/TrialBalance` | Do the books balance? | 37 | **113,067,021.50 / 113,067,021.50**, difference **0.00** |
| `ACC-RPT-02` | Balance Sheet | `/Accounting/BalanceSheet` | What do we own and owe? | 13 | Assets 65,900,245.20 · Liabilities 7,848,325.83 · Equity 58,051,906.56 |
| `ACC-RPT-03` | Income Statement | `/Accounting/IncomeStatement` | Did we make a profit? | 17 | Revenue 37,067,611.42 · Expense −38,748.33 · Result 37,106,372.54 |
| `ACC-RPT-04` | Cash Flow | `/Accounting/CashFlow` | Where did the cash go? | 6 | Opening 18,651,426.98 · Movement 27,756,527.99 · Closing 46,407,963.93 |
| `ACC-RPT-05` | AR Aging | `/Accounting/ArAging` | Who owes us, and for how long? | 41 | Current 3,792,796.69 · 31-60 7,474,962.17 · 61-90 4,013,976.87 · 90+ 3,254,466.53 · **Total 18,536,202.26** |
| `ACC-RPT-06` | AP Aging | `/Accounting/ApAging` | Whom do we owe? | 3 | 31-60 460,758.60 · 61-90 190,749.86 · **Total 651,508.46** |
| `ACC-RPT-07` | Journal Entries | `/Accounting/Journals` | What was posted, and when? | 25/page | columns: Entry No, Date, Type, Description, Total, Status |
| `ACC-RPT-08` | Customer Statement | `/Accounting/CustomerStatement` | One customer's account | picker | opens a party picker first |
| `ACC-RPT-09` | Vendor Statement | `/Accounting/VendorStatement` | One supplier's account | picker | opens a party picker first |

### 1.1 Date basis and record inclusion — measured

- The **trial balance includes `Posted` and `Reversed`** entries. Both an original and its
  reversal are in it, netting to zero. Verified: SQL over `Posted + Reversed` gives exactly the
  reported 113,067,021.50.
- **Drafts are excluded.** Two drafts totalling 7,690.00 exist in the database and are absent
  from the report.
- `Cancelled` documents (6 purchase invoices) produce no ledger effect.
- Trial balance accepts optional `from` / `to` dates and a **cost-centre slice**
  (`TrialBalance(DateTime? from, DateTime? to, int? costCenterId)`). The captures were taken
  with no parameters — i.e. **all dates**.

### 1.2 Currency basis

All figures are in the **functional currency (EGP)**. The ledger stores base amounts
(`GrandTotalBase`, `SubTotalBase`, `TaxTotalBase` on the trade documents), and a
`Currency Translation Reserve` account (3202) exists. Multi-currency presentation was **not
tested**.

### 1.3 Export and print — NOT tested

Export actions exist in the controller: `JournalsExport`, `SalesInvoicesExport`,
`PurchaseInvoicesExport`, `ReceiptsExport`, `PaymentsExport`, `CustomersExport`,
`VendorsExport`, `CustomerAnalyticsExport`. The platform reporting layer additionally declares
five output formats (Html, PrintHtml, Pdf, Xlsx, Csv).

**SUPERSEDED in pass 2.** Eleven real export files were produced and verified — eight in-module
.xlsx totalling 21,963 data rows, plus a platform **PDF (17 pages)**, XLSX and CSV. See
[11 §6](11-roles-and-exports.md). `exports/` now holds the actual files.

---

## 2. Reconciliation

Ten checks. **3 reconciled, 2 did not reconcile, 5 not tested.** Figures are recorded as found.

### ✅ Journal debits vs credits — RECONCILED

```
SELECT SUM(Debit), SUM(Credit) FROM JournalEntryLines l
JOIN JournalEntries j ON j.ID = l.JournalEntryId
WHERE j.CompanyID = 1 AND j.Status IN ('Posted','Reversed');

  113,067,021.50 | 113,067,021.50 | difference 0.00 | 11,789 entries
```

Matches the Trial Balance screen **exactly**, both in the table footer and in the summary tiles.

### ✅ Trial balance screen vs ledger — RECONCILED

Same figure from two independent paths: the application's `GeneralLedgerService.TrialBalanceAsync`
and direct SQL.

### ✅ Sample transaction traced into the report — RECONCILED

| | |
|---|---:|
| Trial balance, 2026-09-20 (previous discovery's capture) | 113,027,021.50 |
| Trial balance, after ACC-GL-01 ×4 | **113,067,021.50** |
| **Difference** | **40,000.00** |
| ACC-GL-01 postings: 4 originals + 4 reversals × 5,000.00 | **40,000.00** |

The transaction this discovery created is visible in the report total, to the cent.

### ✅ Receivables vs control account — RECONCILED in pass 2 (see [09](09-reconciliation-bridges.md))

| | |
|---|---:|
| AR Aging report total | **18,536,202.26** |
| Account `1102 Accounts Receivable`, net, Posted+Reversed | **18,520,263.26** |
| **Difference** | **15,939.00** |

**Tested and rejected as the cause:** the 6 draft sales invoices. Their `GrandTotalBase` is
`NULL`, so they cannot account for a specific 15,939.00.

**SUPERSEDED — cause found in pass 2.** The aging report never deducts credit notes. Posted
sales returns total **15,939.00**, equal to the difference to the cent. Full bridge in
[09](09-reconciliation-bridges.md).

### ✅ Payables vs control account — RECONCILED to 74.20 in pass 2 (see [09](09-reconciliation-bridges.md))

| | |
|---|---:|
| AP Aging report total | **651,508.46** |
| Account `2101 Accounts Payable`, net, Posted+Reversed | **635,232.29** |
| **Difference** | **16,276.17** |

**Tested and rejected:** the 6 cancelled purchase invoices total only **480.00**.

**SUPERSEDED — cause found in pass 2.** 15,700.00 of payments to three vendors with no posted
invoice (the aging skips those parties) plus 650.37 of purchase returns, leaving **74.20**
localised to nine reversed purchase-invoice journals and still open.
Full bridge in [09](09-reconciliation-bridges.md).

> Both differences are recorded exactly as found. No figure was adjusted to make them agree,
> and neither should be presented to a reader as a reconciled control until the cause is known.

### ⬜ Not tested

| Check | Ledger side available | Why not tested |
|---|---|---|
| Inventory valuation vs account 1103 | 874,681.60 | The valuation report was not run |
| Cash/bank vs accounts 110101 / 110102 | 45,739,411.92 / 653,065.41 | Bank reconciliation was not run |
| Fixed assets vs 1201 / 1202 | 11,000.00 cost | Asset register not run |
| Payroll liabilities vs 2103 / 210204 / 210205 | not extracted | Only 2 payroll entries; none executed |
| Financial statements vs adjusted trial balance | both captured | The cross-foot was not performed |

---

## 3. An observation about account 1102

`Accounts Receivable` (1102) and `Accounts Payable` (2101) are both flagged **`IsPostable = 0`**
and yet both carry net balances in the ledger. Either the flag does not prevent posting, or the
balances arrive through child accounts that the grouping rolls up.

**Not established.** It matters for the guide, because "you cannot post to a control account" is
exactly the kind of rule a manual states — and this data does not support stating it.

---

## 4. Method note — the number that was never true

The first trial-balance capture showed **113,047,014.46** in the summary tiles against
**113,047,021.50** in the table footer: a 7.04 difference on one screen, from one model
property.

The tiles use Metronic's `data-kt-countup` animation, and the screenshot caught it mid-count.
The readiness gate now blocks until every counter's text is stable across three polls; after
that the two figures are identical.

Every row of `screenshot-manifest.csv` carries `countersSettled` so this can be audited rather
than taken on trust. **Thirty of thirty captures pass it.**
