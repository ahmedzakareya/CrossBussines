# 08 — Gaps and blockers

What this package does not establish, why, and precisely what closes each item.

**Revised after execution pass 2.** The pass-1 text of this document is superseded; where a gap
closed, it is recorded as closed with its evidence, not deleted. Five of the pass-1 gaps are
closed or substantially closed. What remains is listed honestly below.

---

## 1. Coverage, reported separately as the brief requires

| Dimension | Covered | Basis |
|---|---|---|
| **Features (A–K)** | **27 of 62** runtime verified · 13 evidenced by existing ledger data only · 22 unverified | `scope-matrix.csv` |
| **Executed workflows** | **12 of 16** scenario groups executed; 1 attempted and refused with a recorded blocker; 3 not executed | [10](10-execution-pass-2.md) §3–§7 |
| **Posting rules** | **15 of 54** source types have observed debit/credit lines. All 54 have trigger, service and account-resolution mode | `04-posting-rules.md`, [10](10-execution-pass-2.md) |
| **Screenshots** | **80 written** (49 EN / 31 AR); **1 readiness failure, explained and not written**; 2 duplicate groups, both structural | `screenshot-manifest.csv`, [10](10-execution-pass-2.md) §9 |
| **Role checks** | **4 of 8 profiles** exercised with real sign-ins; the SoD control proven in both halves | [11](11-roles-and-exports.md) |
| **Reports** | 9 executed and captured in both languages; **5 of 10 reconciliation checks pass, 0 fail, 5 not tested**; **11 export files produced and verified** | [11](11-roles-and-exports.md) §6–§7 |

> The accounting cycle is **not** verified end to end. A large majority of it now is: the trade
> cycles, treasury, fixed assets, depreciation, foreign currency and the closing sequence were
> executed and reconciled. Opening balances, payroll, accruals, bank reconciliation, inventory
> counting and realised FX were not.

---

## 2. Gaps closed in pass 2

### ✅ Closed — Gap 1, "fifteen of sixteen scenarios were not executed"

Twelve scenario groups were executed against the isolated database, producing **16 posted journal
entries** (`JV-2026-008517` … `008532`) plus intentional refusals. Purchasing, sales, both return
types, treasury transfer, asset capitalisation, depreciation, FX revaluation and its reversal,
period close, closed-period refusal, period reopen and year-end close all ran. See
[10](10-execution-pass-2.md).

### ✅ Closed — Gap 2, "two subledgers do not reconcile and the cause is unknown"

Both causes are found and both bridges are in [09](09-reconciliation-bridges.md).

| | Pass-1 difference | Cause | Status |
|---|---:|---|---|
| Receivables | 15,939.00 | the aging **never deducts posted credit notes** | **closes exactly to 0.00** |
| Payables | 16,276.17 | payments to vendors with no posted invoice (15,700.00) + purchase returns (650.37) | **closes to a 74.20 residual** |

The 74.20 residual is **still open** and is carried below as Gap R1. Both differences are
**pre-existing** — this pass's postings touched only 510105 and 110102.

### ✅ Closed — Gap 3, "no role was tested" *(partially — 4 of 8)*

Four profiles were provisioned through the project's own dev seeder and signed in genuinely. The
**company boundary holds** (a company-65 user sees 0.00 and zero account rows) and the **write
gate holds** (a user with no roles cannot post; 0 entries created). Read-screen reachability is
genuinely open, and that is recorded as a finding rather than inferred from an administrator
screenshot.

The remaining four profiles are carried below as Gap R2.

### ✅ Closed — Gap 4, "no report was exported"

**Eleven files** were produced and verified: 8 in-module `.xlsx` (21,963 data rows, each
containing this pass's own documents) and 3 platform formats against `Accounting.JournalActivity`
— CSV 32,681 B, XLSX 21,929 B and **PDF 64,980 B, 17 pages**. The files are in `exports/`.

### ✅ Closed — the segregation-of-duties control

`ApprovalThreshold` was raised to 1,000.00 through the supported settings screen, both halves of
the control were proven, and the value was **restored to 0.0000**. No source code was changed.

---

## 3. Gaps that remain open

### Gap R1 — the 74.20 AP residual

Nine reversed purchase-invoice journals leave 74.20 unaccounted for in the AP bridge. It is a
rounding-or-partial-reversal artefact in pre-existing data, and it was **not** resolved by
creating a balancing journal — which the brief forbids and which would in any case hide it.

**How to close it.** Walk the nine journals line by line against their reversals and compare the
per-line amounts; the difference is almost certainly in one or two lines, not spread across all.

### Gap R2 — four of eight role profiles untested

Accountant, cashier, purchasing, sales, warehouse and payroll are **module/POS roles** granted per
employee on each module's own roles screen. No test user holds them. Chief accountant is covered
only indirectly, through the `admin` account that holds the `ChiefAccountant` module role.

**How to close it.** One module-role grant and one sign-in per profile. Roughly two hours.

### Gap R3 — seven areas not executed

| Area | Status | Blocker |
|---|---|---|
| Opening balances (GL / AR / AP / assets) | not executed | no screen for `OpeningGL` / `OpeningARBalance` / `OpeningAPBalance` / `OpeningAsset` was reached; only `OpeningStock` has data |
| Payroll posting and disbursement | **attempted, REFUSED** | *"There is no postable payroll in this period"* — no September 2026 payroll data. Closing it means salary policies and attendance for 27 employees |
| Accrual / prepayment adjustment | not executed | `Adjustment` declared, no entries, no dedicated screen located |
| Bank reconciliation | not executed | `/Accounting/Reconcile` renders; no statement was reconciled |
| Inventory count / adjustment | not executed | stock moved only as a by-product of the purchase and the return |
| POS, projects, manufacturing postings | not executed | these post from their own modules; their existing ledger rows show the integrations work, not that this pass exercised them |
| Realised FX on settlement | not executed | needs a foreign-currency invoice settled at a different rate; only the unrealised revaluation was run |

### Gap R4 — the Arabic pass does not cover roles, SoD or exports

Sixteen Arabic result screens were captured, covering the transaction and report evidence. The
role probes, the SoD test and the export runs were executed in English only.

### Gap R5 — posting rules still source-only for 39 of 54 events

Fifteen source types now have observed debit/credit lines. The other 39 have trigger, service
and account-resolution mode established from source, and their lines are deliberately **not**
written down as though they were results.

---

## 4. Smaller findings, recorded

| Finding | Detail |
|---|---|
| **The platform PDF embeds Dubai, not Cairo** | `AAAAAA+Dubai-Bold`, `BAAAAA+Dubai-Regular`. `ReportFontLibrary` embeds static Cairo faces specifically so PDFs render in Cairo; the produced file does not use them |
| **`Accounting.TrialBalance` is not a registered platform report** | the PDF attempt returned 404. The trial balance exists only as an in-module screen and cannot be exported through the platform |
| **A purchase return credits GRNI (210203), not Inventory** | observed; this package does not attempt to justify it |
| **A purchase return is valued at moving-average cost** | 5 units bought at 50.00 came back at 152.02. The screen says so; a guide that omits it will mislead |
| **Setting an item changes the debit account** | same form, same chosen account: `itemId` null debits 510105, `itemId` set debits 1103 |
| **A fully-settled party disappears from the aging report** | `if (row.Total > 0) rows.Add(row)` — correct behaviour, and it caused this pass's one readiness failure |
| **Hardcoded company** | `AccountingController` still carries `private const int DefaultCompanyId = 1` (line 185). Observed behaviour is nonetheless that a company-65 user sees an empty ledger |
| **Control accounts are flagged non-postable yet carry balances** | 1102 and 2101 have `IsPostable = 0` and non-zero net balances |
| **Duplicate DOM id** | `CreateJournal.cshtml` renders two buttons both with `id="postBtn"` (lines 241, 246) |
| **Reversing entries are described in Arabic only** | «قيد عكسي JV-…», including in the English interface |
| **No recurring journals; no manufacturing variance; no supplier-advance source type** | none found |
| **`VatReturn` and `PayrollPay` have still never produced an entry** | `Depreciation` and `YearClose` no longer belong on this list — both were executed in pass 2 |
| **Duplicate-posting protection and rollback unverified** | 75 guard sites and 31 transaction sites exist; neither was exercised |

---

## 5. Environment left in place

Left available for review, as instructed — **not** cleaned up.

| | |
|---|---|
| `CrossBuyAcctTest` database | **left in place**, restored to checkpoint CP2 |
| Isolated instance on **`:5299`** | **still running** — a different process on a different port from the developer's instance |
| `C:\temp\cb_acct_iso\` | published app, `run-isolated.cmd`, `app.log`, and both checkpoints |
| `CP1_pre_execution.bak` | the state after pass 1 |
| `CP2_post_execution_pre_closing.bak` | **the state the database is in now** — transactions present, no year-end entry |
| Developer's instance and `CrossBuyDev` | **untouched throughout**, re-proved after every restart |
| `ApprovalThreshold` | **restored to 0.0000** |
| Seed password | `C:\temp\cb_acct_iso\.seedpw`, outside this package and outside the repository |
| Records created | 16 journal entries `JV-2026-008517`–`008532`, plus master data 3015 / 9047 / 2026, all marked `ZZ-DISCOVERY` |

Cleanup procedure is in [01 §4](01-baseline-and-environment.md). **Do not run it without an
explicit instruction** — the evidence in this package can be re-checked only while the
environment stands.

---

## 6. What I still need from you

Three of the four pass-1 decisions are unchanged; the execution question is answered.

1. **The English spelling of the owner's name.** `Ahmed Zakarya` (git identity, 235 commits) or
   `ahmed zakareya` (employee record)? The git form is proposed. The Arabic **احمد زكريا** is
   taken from the owner's own employee record and is not in doubt.
2. **Is `assets/owner/owner-portrait-original.png` the right portrait?** The chain ties it to
   employee 5, which the sign-in greeting identifies as the owner — but no measurement can
   confirm the person in a photograph.
3. **What version and publisher go on the cover?** The product declares neither, and the wordmark
   carries a **®** with no registrant named anywhere in the repository.

None of these blocked pass 2, and none of them blocks writing the guide's body.
