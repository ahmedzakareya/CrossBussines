# CrossBuy — Accounting Cycle Discovery

Evidence package for an illustrated, bilingual accounting-cycle user guide.

**Discovery date:** 21 September 2026
**Repository:** `C:\CrossBuy\CrossBuy`
**Branch / commit:** `reporting/studio-gap-closure` @ `32985d91822da8cf4f0ffb05caade44629748ab7`

---

## The one thing to read first

Two execution passes have run. **Pass 2 executed the connected trade cycles, treasury, fixed
assets, foreign currency and the closing scenarios** — the areas pass 1 left open.

| | |
|---|---|
| **Executed and verified in the ledger** | Manual journal (draft → post → reverse); purchase invoice → partial payment → final settlement → purchase return; sales invoice → partial receipt → final collection → sales return; a stock purchase; cash/bank transfer; asset capitalisation; depreciation run; FX revaluation and its reversal; period close, refusal-on-closed-period, reopen, and year-end close. **15 of 54 ledger source types executed.** |
| **Reconciled** | The AR difference now closes **exactly**; the AP difference closes to **74.20**, localised. Trial balance = ledger to the cent. |
| **Exports** | **11 real files** produced and opened — 8 in-module .xlsx (21,963 data rows) plus a platform **PDF (17 pages)**, XLSX and CSV. |
| **Roles** | 4 profiles provisioned and signed in; the write gate, the company boundary and both halves of the segregation-of-duties control proven. |
| **Attempted and REFUSED** | Payroll posting — no September 2026 payroll exists. Recorded with the exact guard. |
| **Still not executed** | Opening balances, accrual/prepayment, bank reconciliation, inventory count, realised FX on settlement, and the POS / project / manufacturing postings. |

Everything is labelled. Nothing source-inferred is presented as an executed result.

---

## Isolation — established and verified

The brief made safe isolation a precondition. It was met:

```
Isolated database   CrossBuyAcctTest      restored from a COPY_ONLY backup of CrossBuyDev
Isolated instance   published to C:\temp\cb_acct_iso\app, run on http://localhost:5299
Developer's session UNTOUCHED — IIS Express PID 32692 on :44368 still serving CrossBuyDev
Production          never contacted
```

**Proof, taken from SQL Server rather than asserted:**

| Process | Database | Sessions |
|---|---|---|
| **39228 — this discovery** | **CrossBuyAcctTest** | 4 |
| 32692 — developer's IIS Express | CrossBuyDev | 3 |
| 24512 — developer's other process | CrossBuyDev | 1 |

My process holds **zero** sessions on `CrossBuyDev`.

**Outbound integrations disabled before the first write:** SMTP off (`Smtp__Enabled=false`), AI
proxy pointed at a dead port. E-invoicing needed no switch — `EtaInvoiceService.SubmitSalesInvoiceAsync`
returns a hardcoded `"NotConfigured"` and makes no network call at all (repository evidence).

Full detail, including the exact commands: [01-baseline-and-environment.md](01-baseline-and-environment.md).

---

## Documents

| # | Document | Contents |
|---|---|---|
| 01 | [Baseline and environment](01-baseline-and-environment.md) | Commit, working tree, isolation proof, reproduction and cleanup |
| 02 | [Accounting scope matrix](02-accounting-scope-matrix.md) | All 62 capabilities across A–K, each classified against evidence |
| 03 | [Worked examples](03-worked-examples.md) | ACC-GL-01 step by step with real journal numbers; the 15 unexecuted scenarios specified |
| 04 | [Posting rules](04-posting-rules.md) | 16 posting services, 54 source types, hard-coded vs configurable accounts |
| 05 | [Reports and reconciliation](05-reports-and-reconciliation.md) | 9 reports executed; 3 reconciled, 2 **not** reconciled, 5 not tested |
| 06 | [Screenshot storyboard](06-screenshot-storyboard.md) | 30 captures with teaching purpose, captions and highlight regions |
| 07 | [Guide outlines](07-guide-outlines.md) | Proposed Arabic and English chapter sequences |
| 08 | [Gaps and blockers](08-gaps-and-blockers.md) | What remains, why, and exactly how to close it *(pass 1; superseded in part by 10–11)* |
| 09 | [Reconciliation bridges](09-reconciliation-bridges.md) | The AR and AP differences, traced and closed numerically |
| 10 | [Execution pass 2](10-execution-pass-2.md) | The connected worked examples as executed, with journals and refusals |
| 11 | [Roles and exports](11-roles-and-exports.md) | The access matrix, the SoD proof, and 11 verified export files |

## Machine-readable inventories

| File | Rows | Contents |
|---|---:|---|
| `scope-matrix.csv` | 62 | Capability → declared source types → data present → executed → status |
| `screenshot-manifest.csv` | 30 | Every field the brief requires, plus readiness checks and duplicate groups |
| `reconciliation-workbook.csv` | 10 | Each check with both sides, the difference and the verdict |
| `posting-account-resolution.csv` | 35 | Every hard-coded account code, with file and line |
| `evidence-register.csv` | — | Every claim, its command, its result, its evidence level |
| `reference/*.json` | — | Raw capture logs, posting-rule extraction, probe output |

## Assets and scripts

`assets/` — brand marks, Cairo fonts with `OFL.txt`, owner portrait (carried forward unchanged
from the user-manual discovery; the name spellings and their unresolved discrepancy are restated
in 08, not re-litigated here).
`scripts/` — all seven scripts exactly as run, including the readiness library.

---

## Evidence labels

Every claim in this package carries one:

| Label | Means |
|---|---|
| **Repository evidence** | Read from source at the pinned commit |
| **Runtime observed** | A screen was rendered and measured, or a query was run against the isolated database |
| **Reconciled result** | Two independent sources were compared and agreed |
| **Unverified** | Neither executed nor observed |

---

## Three findings worth knowing before you read further

**1. Save and Post are genuinely separate, and a draft has no number.** Executed: the draft
journal was written with `EntryNo = NULL`; posting allocated `JV-2026-008513` and set
`PostedBy`. Reversal did not delete it — it created a *new* posted entry of type `Reversing`
(`JV-2026-008514`) and linked the original via `ReversedByEntryId`.

**2. Segregation of duties exists in code and is switched off in this data.** `PostJournal`
refuses a material entry unless the poster is a `ChiefAccountant` and is *not* the creator. The
gate is `threshold > 0`, and `AccountingSettings.ApprovalThreshold` is **0.00** — so no entry is
material and the control never fires.

**3. Both reconciliation differences are now explained, and neither was a data error.** The AR
difference of **15,939.00** is exactly the posted sales returns — **the aging report never
deducts credit notes**. The AP difference of **16,276.17** is 15,700.00 of supplier payments to
vendors with no posted invoice (the aging skips those parties entirely) plus 650.37 of purchase
returns, leaving **74.20** localised to nine reversed purchase-invoice journals and still open.
Both are **report-definition differences**. See [09](09-reconciliation-bridges.md).

---

## Where the environment stands

The isolated instance and database are **left running for review**:

```
database   CrossBuyAcctTest      restored to checkpoint CP2 (transactions in, closing rolled back)
instance   http://localhost:5299  published app at C:	emp\cb_acct_isopp
checkpoints C:	emp\cb_acct_iso\CP1_pre_execution.bak, CP2_post_execution_pre_closing.bak
developer's session  :44368 on CrossBuyDev — untouched throughout
```

Cleanup is in [01 §4](01-baseline-and-environment.md); nothing is removed without an explicit
instruction.

## A methodological note

The trial balance first appeared to disagree with itself by 7.04 — tiles against table footer,
both rendered from the same model property. The cause was mine: the tiles use an animated
counter (`data-kt-countup`) and the screenshot caught it mid-count. The readiness gate now waits
for counters to stop changing, and the figures agree exactly.

It is recorded because it is the subtler cousin of the previous package's loading-screen problem:
**a number that was never true is worse than a blank screen, because it looks like evidence.**
