# 04 — Posting rules

How a business event becomes a journal entry, derived from the posting logic itself.

> **Superseded in part by execution pass 2.** **Fifteen of the 54 source types now have OBSERVED debit/credit lines** (pass 1 had two); the other 39 remain source-derived and are not presented as results. The execution record is [10-execution-pass-2.md](10-execution-pass-2.md); the current counts are in [08-gaps-and-blockers.md](08-gaps-and-blockers.md) §1.


**Machine-readable:** `posting-account-resolution.csv` (35 hard-coded account sites with file and
line), `reference/posting-rules-raw.json` (creators, duplicate guards, transaction sites).

---

## 1. The shape of the posting layer

| | Count |
|---|---:|
| Services that create journal entries | **16** |
| Distinct `SourceType` values declared in code | **54** |
| Source types with entries in the isolated database | **28** |
| Hard-coded account-code sites | **35** |
| Configurable account-lookup sites | **277** |
| Duplicate-protection sites | **75** |
| Explicit transaction / execution-strategy sites | **31** |

Everything posts through one service — `IJournalEntryService` — via `CreateAsync`,
`CreateAndPostAsync` or `PostAsync`. There is no second path into the ledger.

## 2. Saving, approving and posting are three different things

Established by **execution**, not by reading:

| Act | What it does | Evidence |
|---|---|---|
| **Save draft** | Writes the entry with `Status = Draft` and **`EntryNo = NULL`** | Executed: JE 24209 |
| **Post** | Allocates `EntryNo` (`JV-2026-008509`), sets `Status = Posted` and `PostedBy` | Executed |
| **Approve** | For journals, *the same act as posting* — the success message is "Posted and approved" | Repository + executed |
| **Reverse** | Creates a **new** posted entry of type `Reversing`; the original becomes `Reversed` and gains `ReversedByEntryId` | Executed: 24209 → 24210 |

> **A draft is not in the ledger.** The trial balance excludes it: two pre-existing drafts
> totalling 7,690.00 are in the database and absent from the report. **Never treat "Saved" as
> posted.**

### 2.1 The approval threshold and segregation of duties

```csharp
// AccountingController.PostJournal — repository evidence
var threshold = await ApprovalThresholdAsync();          // AccountingSettings.ApprovalThreshold
if (threshold > 0 && entry.Total >= threshold) {
    if (!await IsChiefAsync())            → refuse: chief accountant's approval required
    if (entry.CreatedBy == currentEmpId)  → refuse: "the entry's creator cannot approve it"
}
```

And on create (`AccountingController:1163`) the same threshold **silently downgrades a Post to a
Draft**: `if (post && threshold > 0 && total >= threshold) { post = false; forcedDraft = true; }`.

**Configured value in this database: `0.00` → the gate never fires.** Implemented, disabled.

## 3. The 54 business events that post

Grouped by cycle. **Bold** = entries present in the isolated database.

| Cycle | Source types |
|---|---|
| **Opening** | `OpeningGL`, `OpeningARBalance`, `OpeningAPBalance`, `OpeningAsset`, **`OpeningStock`** (148) |
| **Purchasing** | **`PurchaseInvoice`** (121), `PurchaseInvoiceEdit`, **`PurchaseReturn`** (30), `PurchaseReturnEdit`, **`LandedCost`** (1), **`Payment`** (4) |
| **Sales** | **`SalesInvoice`** (5,519), `SalesInvoiceEdit`, `SalesInvoiceReversal`, **`SalesReturn`** (26), `SalesReturnEdit`, **`Receipt`** (4,403) |
| **Cash** | `Transfer`, **`BankReconTest`** (4) |
| **Inventory** | **`Inventory`** (967), **`StockTransfer`** (5), `TransferIn`, `TransferOut`, **`StockWriteOff`** (15), **`Assembly`** (8), `Disassembly`, `Issue`, `Adjustment`, **`StockReconcile`** (6) |
| **Fixed assets** | **`AssetCapitalization`** (1), `FixedAsset`, `FixedAssetDisposal`, `AssetMaintenance`, `Depreciation`, **`EquipmentDepAllocation`** (1) |
| **Payroll** | **`Payroll`** (2), `PayrollPay`, `LeaveProvision`, `LeaveEncash`, **`FinalSettlement`** (2) |
| **POS** | **`PosShiftClose`** (24), **`PosRefund`** (21), **`PosTip`** (24), `PosPrep`, `PosWaste` |
| **Projects** | `ProjectAdvance`, `ProjectIssue`, **`ProjectLabor`** (1), `RetentionRelease`, `SubRetentionRelease` |
| **Manufacturing** | **`WorkOrder`** (115) |
| **FX** | **`FxRevaluation`** (3), **`FxRevaluationReversal`** (3) |
| **Closing** | `VatReturn`, `YearClose` |
| **Manual** | **`Manual`** (3 + our 2), **`Reversal`** (321 + our 2) |

**`Depreciation`, `YearClose`, `VatReturn`, `PayrollPay`, `ProjectAdvance`, `RetentionRelease`,
`Transfer` and `Adjustment` are declared in code and have never produced an entry in this
data.** They are *Implemented but unverified*, not *working*.

## 4. Configurable versus hard-coded accounts

**277 configurable lookups against 35 hard-coded codes.** The mapping layer dominates, but the
exceptions are concentrated and worth knowing:

| Service | Hard-coded codes |
|---|---:|
| `StockService` | **19** |
| `PosOrderService` | 5 |
| `PosSetupService` | 5 |
| `PosPreparationService` | 2 |
| `AiInsightsService`, `ClosingService`, `FinancialStatementService`, `MaintenanceService` | 1 each |

A hard-coded code means **a customer cannot re-map that account without a code change**. Every
site is listed with file and line in `posting-account-resolution.csv`, so the guide can tell an
implementer exactly which accounts are fixed.

*Method note: a "hard-coded site" is a literal account code compared or matched in source
(`Code == "510101"`, `Code.StartsWith("1101")`). A configurable site is a lookup through
settings or a mapping property. The counts are a reliable signal of where the risk is, not an
exact inventory of every posting line.*

## 5. Duplicate-posting protection

**75 guard sites** were found — typically a check that an entry already exists for the same
`SourceType` + `SourceId`, or that a document's status is already `Posted`.

**Not verified by execution.** Attempting a double post would need the same document posted
twice, which was not performed. Treat duplicate protection as **implemented, unverified**.

## 6. Transactions and rollback

**31 sites** use `BeginTransactionAsync` or an EF Core execution strategy, so a posting that
fails part-way is intended to roll back atomically.

**Not verified by execution.** Forcing a mid-posting failure was out of scope for a read-mostly
pass.

## 7. Subsidiary ledger versus general ledger timing

The product posts the subledger document and its journal entry **in the same operation** — there
is no separate "transfer to GL" step, and no source type exists for one. The trial balance
therefore reflects a sales invoice the moment it is posted.

The one structural timing difference is **goods received not invoiced**: account
`210203 Goods Received Not Invoiced (GRNI)` exists, and `Inventory` (967 entries) and
`PurchaseInvoice` (121) are separate source types. That is the receipt/invoice timing difference
the brief asks about — **present in the chart and in the source types, not executed here.**

## 8. Automatic versus manual journals

`JournalType` takes `Manual`, `Auto` and `Reversing`. Observed in the data: 5,519 `SalesInvoice`
entries are `Auto`; our two are `Manual`; the two reversals are `Reversing`. A user cannot edit
an `Auto` entry directly — the correction path is through the source document
(`SalesInvoiceEdit`, `PurchaseInvoiceEdit`) or a reversal.

## 9. What a full posting matrix still needs

This document gives the **trigger, the source type, the service and the account-resolution
mode** for all 54 events. What it does **not** give, for 53 of them, is the **observed debit and
credit lines**, because the transactions were not executed.

Those lines can be read from source, but the brief is explicit: *never present source-inferred
outcomes as executed results.* So the debit/credit columns are deliberately left for the
execution pass described in [08-gaps-and-blockers.md](08-gaps-and-blockers.md) — with the single
exception of `Manual` and `Reversal`, whose lines are recorded in
[03-worked-examples.md](03-worked-examples.md) exactly as they were written to the ledger.
