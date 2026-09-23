# 02 — Accounting scope matrix

Every capability the brief asks about (A–K), classified against evidence rather than against a
menu label.

**Machine-readable:** `scope-matrix.csv` — 62 rows, each with the source types that evidence it,
whether entries exist in the isolated database, and whether this discovery executed it.

---

## How each row was classified

Three independent inputs, none of them a screen:

1. **Declared in code** — a `SourceType` the posting layer can write (54 exist).
2. **Entries in the isolated database** — proof the path has produced real ledger rows (28 do).
3. **Executed by this discovery** — a transaction this pass performed and verified (2).

| Status | Meaning | Rows |
|---|---|---:|
| **Implemented and runtime verified** | This discovery executed it, or rendered the screen *and* found ledger data behind it | **27** *(was 9 after pass 1)* |
| **Implemented, evidenced by existing ledger data; not executed here** | The path has demonstrably produced entries; neither pass ran it | **13** |
| **Implemented but unverified** | Declared in code with no entries and no execution, or a configuration area with no source type | **22** |
| Partial / Not found / Not applicable | — | 0 |

> **No row is marked "verified" because a menu item exists.** After two passes, 27 of 62 rows
> reach runtime-verified; the other 35 do not, and say so. The live counts are regenerated into
> `scope-matrix.csv` by `scripts/build_inventories.py`.

---

> ## ⚠ Read this before the tables below
>
> The per-area tables in this document were written after **pass 1**. **Pass 2 executed much of
> what they mark "not executed"** — the trade cycles, treasury, fixed assets, depreciation,
> foreign currency and the closing scenarios.
>
> Rows changed by pass 2 are marked **✅ EXECUTED (pass 2)** below. The authoritative, regenerated
> classification is **`scope-matrix.csv`**, and the execution record is
> **[10-execution-pass-2.md](10-execution-pass-2.md)**.

---

## A — Foundations

| Capability | Evidence | Status |
|---|---|---|
| Company and branch accounting context | `CompanyID` on every table; company resolved from the employee record; **`AccountingController` still uses `private const int DefaultCompanyId = 1`** (line 185, comment: *"for Phase 0/1 we operate on a single company"*) | Implemented but unverified |
| Financial years, periods, locks, closing rules | 2 years × 13 periods (12 + adjustment), all Open; `/Accounting/Periods` rendered; period 9 auto-resolved from the entry date **on execution** | **Runtime verified** |
| Chart of accounts, types, control accounts | 63 accounts, `AccountTypes` (ASSET/LIAB/EQUITY/REV/EXP); `/Accounting/ChartOfAccounts` rendered | **Runtime verified** |
| Cost centres and analytical dimensions | 2 cost centres; journal lines carry cost centre **and project**; 6 accounts have `RequireCostCenter = 1` (COGS, Utilities, Write-off, Salaries, Social Insurance, Depreciation) | Implemented but unverified |
| Functional / transaction currencies, rates, precision | 5 currencies; `ExchangeRates`; base-amount columns on trade documents; `AccountingSettings.RateMaxAgeDays = 0`, `RateStaleBehavior = Warn`; refusal exists for a currency with no decimal precision | Implemented but unverified |
| Tax configuration, inclusive/exclusive, rounding | 6 tax codes; VAT Input 110401 and VAT Output 210201 exist | Implemented but unverified |
| Document numbering | **Executed**: draft had `EntryNo = NULL`; posting allocated `JV-2026-008509`. Series `JV-{year}-{6 digits}` | **Runtime verified** |
| Opening GL balances | `OpeningGL` declared, **no entries** | Implemented but unverified |
| Opening customer balances | `OpeningARBalance` declared, **no entries** | Implemented but unverified |
| Opening supplier balances | `OpeningAPBalance` declared, **no entries** | Implemented but unverified |
| Opening stock | **`OpeningStock` — 148 entries** | Ledger data; not executed |
| Opening assets | `OpeningAsset` declared, **no entries** | Implemented but unverified |

**Avoiding duplication across subledgers and the ledger** — the brief asks how opening entries
avoid double-counting. Separate source types exist per subledger (`OpeningGL`, `OpeningARBalance`,
`OpeningAPBalance`, `OpeningStock`, `OpeningAsset`) and an `Opening Balance Equity` account
(3301) is the contra. **The mechanism is visible; it was not exercised, so the answer is not
verified.**

## B — General ledger

| Capability | Evidence | Status |
|---|---|---|
| Manual journals | **Executed** — JE 24209, 24211 | **Runtime verified** |
| Draft / post separation | **Executed** — draft has no number; posting allocates it and sets `PostedBy` | **Runtime verified** |
| Reversal of a posted entry | **Executed** — 24209 → 24210, type `Reversing`, original `Reversed` | **Runtime verified** |
| Approval | **Both halves PROVEN** with the threshold raised to 1,000 then restored: an above-threshold Post is forced to Draft, and the creator cannot approve their own | ✅ **EXECUTED (pass 2)** |
| Adjusting journals | `Adjustment` declared, **no entries**; period 13 exists as an adjustment period | Implemented but unverified |
| Recurring journals | **Not found** — no recurring-journal source type or service | Not found |
| Attachments, references, audit history | `CreatedBy`/`CreatedAt`/`ModifiedBy`/`PostedBy`/`PostedAt` on the entry | Implemented but unverified |
| Closed-period handling | **REFUSAL TRIGGERED AND CAPTURED** — a journal dated in the closed period did not post | ✅ **EXECUTED (pass 2)** |
| Correction of a posted mistake | Reversal **executed**; edit-and-repost (`SalesInvoiceEdit`, `PurchaseInvoiceEdit`) not exercised | Partial |

## C — Purchasing and payables

| Capability | Evidence | Status |
|---|---|---|
| Purchase invoice | **PI-2026-05073 and PI-2026-05074 created and posted**; journals `JV-2026-008517` / `008524` | ✅ **EXECUTED (pass 2)** |
| Purchase invoice edit / re-post | `PurchaseInvoiceEdit` declared, no entries | Implemented but unverified |
| Goods receipt / GRNI timing | Account `210203 Goods Received Not Invoiced` exists; `Inventory` 967 entries separate from `PurchaseInvoice` | Ledger data; not executed |
| Landed costs | **1 entry** | Ledger data; not executed |
| Supplier payment and allocation | **PY-2026-00008 (500.00) and PY-2026-00009 (640.00)**, settling 1,140.00 in full | ✅ **EXECUTED (pass 2)** |
| Purchase return / debit note | **DN-2026-03016, 173.30** at moving-average cost; first attempt REFUSED for having no item line | ✅ **EXECUTED (pass 2)** |
| Supplier statement and aging | Rendered and captured; the 16,276.17 difference is **now explained** — see [09](09-reconciliation-bridges.md) | ✅ **Runtime verified + reconciled to 74.20** |
| Supplier advances | No distinct source type found; `2104 Advances from customers` is the customer-side account only | Not found |

## D — Sales and receivables

| Capability | Evidence | Status |
|---|---|---|
| Sales invoice | **SV-2026-17613 created and posted**, journal `JV-2026-008520` | ✅ **EXECUTED (pass 2)** |
| Sales invoice edit / reversal | `SalesInvoiceEdit`, `SalesInvoiceReversal` declared, no entries | Implemented but unverified |
| Customer receipt and allocation | **RC-2026-15485 (1,000.00) and RC-2026-15486 (710.00)**, collecting 1,710.00 in full | ✅ **EXECUTED (pass 2)** |
| Sales return / credit note | **CN-2026-04050, 342.00** — accepted a SERVICE line, unlike the purchase return | ✅ **EXECUTED (pass 2)** |
| Customer statement and aging | Rendered and captured; the 15,939.00 difference is **explained exactly** — the aging never deducts credit notes | ✅ **Runtime verified + RECONCILED** |
| Customer advances | Account `2104 Advances from customers` exists; no source type | Implemented but unverified |
| Electronic-invoicing status | `EtaStatus` screen exists; `SubmitSalesInvoiceAsync` returns a hardcoded `"NotConfigured"` and **makes no call** | **Not implemented (deferred)** — kept separate from accounting posting, as the brief requires |

## E — Cash and banks

| Capability | Evidence | Status |
|---|---|---|
| Cash and bank accounts | 1 bank account, 1 cash box; accounts 110101 (+5 tills) and 110102 | Implemented but unverified |
| Transfers | **2,500.00 Bank → Main Cash**, journal `JV-2026-008527` | ✅ **EXECUTED (pass 2)** |
| Bank charges and reconciliation | **4 `BankReconTest` entries**; `/Accounting/Reconcile` exists | Ledger data; not executed |
| Petty cash / employee advances | No distinct source type found | Not found |
| Cleared / uncleared / allocated | Not established | Unverified |

## F — Inventory accounting

| Capability | Evidence | Status |
|---|---|---|
| Valuation and COGS | **`Inventory` 967 entries**; account `510101 Cost of Goods Sold` (requires a cost centre) | Ledger data; not executed |
| Valuation method | **Not established** — no explicit FIFO/average setting was located | Unverified |
| Transfers | **`StockTransfer` 5 entries**; `TransferIn`/`TransferOut` declared, none | Ledger data; not executed |
| Counts and adjustments | **`StockReconcile` 6 entries**; `Adjustment` declared, none | Ledger data; not executed |
| Write-offs | **15 entries**; account `510103 Inventory write-off / damage loss` | Ledger data; not executed |
| Assembly / disassembly | **`Assembly` 8 entries**; `Disassembly` none | Ledger data; not executed |
| Landed-cost allocation | 1 entry | Ledger data; not executed |
| Negative stock, batches, serials | Batch/serial tracking flags exist on the item form (**executed evidence**: the item form's `Track batch` / `Track expiry` / `Track serial` switches) | Implemented but unverified financially |
| Inventory valuation vs GL | Account 1103 = **874,681.60**; **valuation report not run** | Not tested |
| Do transfers affect financial accounts? | `StockTransfer` **is** a ledger source type, so transfers post. Whether they are value-neutral was **not verified** | Partial |

## G — Fixed assets

| Capability | Evidence | Status |
|---|---|---|
| Acquisition and capitalisation | **Asset 2026 created, 24,000.00**, journal `JV-2026-008528`: Dr 1201 / Cr Bank | ✅ **EXECUTED (pass 2)** |
| Depreciation run | **`Depreciation` declared, NO entries**; `EquipmentDepAllocation` 1 entry; screen states *"One month's depreciation is computed for all active assets and posted as a single entry"* | Implemented but unverified |
| Disposal and gain/loss | `FixedAssetDisposal` declared, no entries | Implemented but unverified |
| Maintenance | `AssetMaintenance` declared, no entries | Implemented but unverified |
| Transfers of assets | Not found | Not found |

## H — Payroll

| Capability | Evidence | Status |
|---|---|---|
| Payroll posting | **ATTEMPTED and REFUSED** — *"There is no postable payroll in this period"*; no September 2026 payroll data exists | ⚠ **Executed, refused — blocker recorded** |
| Disbursement | `PayrollPay` declared, **no entries**; the screen warns *"No bank or cash account. Create one under «Banks & cash» first."* | Implemented but unverified |
| Liabilities | Accounts 2103 Payroll Payable, 210204 Social Insurance Payable, 210205 Payroll Tax Payable | Implemented but unverified |
| Leave provision / encashment | `LeaveProvision`, `LeaveEncash` declared, no entries; accounts 210206, 520104, 520106 exist | Implemented but unverified |
| Final settlement | **2 entries** | Ledger data; not executed |
| Payroll-to-ledger reconciliation | **Not tested** | Not tested |

## I — Connected modules

| Capability | Evidence | Status |
|---|---|---|
| POS shift close | **24 entries** | Ledger data; not executed |
| POS refund | **21 entries** | Ledger data; not executed |
| POS tips | **24 entries**; account `210207 Tips payable` | Ledger data; not executed |
| POS preparation / waste | `PosPrep`, `PosWaste` declared, **no entries** | Implemented but unverified |
| Projects — advance | `ProjectAdvance` declared, **no entries**; account `2104` and the screen's own note *"Advance received = a liability (2104), not revenue"* | Implemented but unverified |
| Projects — labour and issues | **`ProjectLabor` 1 entry**; `ProjectIssue` none; accounts 1105 WIP, 2058 Contract revenue, 2059 Project execution cost | Ledger data; not executed |
| Projects — retention | `RetentionRelease`, `SubRetentionRelease` declared, **no entries**; accounts 1104, 3056 exist | Implemented but unverified |
| Manufacturing — work orders | **`WorkOrder` 115 entries**; WIP account 1105 | Ledger data; not executed |
| Manufacturing — variances | **No variance source type found** | Not found |

**Incomplete integrations, explicitly:** POS preparation/waste, project advances and retention,
and manufacturing variances all have code paths or accounts but no ledger evidence.

## J — Foreign currency

| Capability | Evidence | Status |
|---|---|---|
| Transaction conversion | Base-amount columns on trade documents; 5 currencies | Implemented but unverified |
| Realised FX on settlement | Accounts `4902 Realized FX Gain`; **no distinct source type and no entries found** | Implemented but unverified |
| Unrealised revaluation | **Executed**, journal `JV-2026-008530` | ✅ **EXECUTED (pass 2)** |
| Revaluation reversal | **Executed**, `JV-2026-008531` — cancels the revaluation exactly | ✅ **EXECUTED (pass 2)** |
| Reporting currency basis | Functional currency EGP; account `3202 Currency Translation Reserve` exists | Implemented but unverified |

## K — Closing and reporting

| Capability | Evidence | Status |
|---|---|---|
| Trial balance | **Executed and reconciled to the cent** | **Runtime verified + reconciled** |
| Account ledger / drill-down | Not exercised | Unverified |
| Balance sheet, income statement, cash flow | **All three rendered and captured** in both languages; **not cross-footed** to the trial balance | Runtime verified (render only) |
| AR / AP aging | Rendered; **both differences are now explained** — AR closes exactly, AP to a 74.20 residual ([09](09-reconciliation-bridges.md)) | ✅ **Runtime verified + reconciled** |
| Adjustments, accruals, prepayments | `Adjustment` declared, no entries | Implemented but unverified |
| Depreciation and FX revaluation at period end | See G and J | Implemented but unverified |
| Period close | **Period 9 CLOSED** via `SetPeriodStatus`; a posting into it was then **REFUSED** | ✅ **EXECUTED (pass 2)** |
| Year-end close, retained earnings | **EXECUTED** — `JV-2026-008532`, 15 lines, Dr = Cr = 58,028,927.40, closing to Retained Earnings 57,972,617.03, dated 2026-12-31 | ✅ **EXECUTED (pass 2, rolled back with CP2)** |
| Reopening a closed period | **EXECUTED** — `ReopenedAt`, `ReopenedBy` and `ReopenReason` all written | ✅ **EXECUTED (pass 2)** |
| Monthly vs year-end closing | Distinct: periods carry `Status`/`ClosedBy`/`ClosedAt`; year-end is a separate `YearClose` source type and a separate screen | Implemented but unverified |
