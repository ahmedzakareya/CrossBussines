# H11 Insurance & Claims — integration readiness

Status (2026-11-03): **applied and running, at the owner's instruction ("تشغيل كامل").**
- The slice ran on CrossBuyDB2 and was re-run with an identical object snapshot.
- The SHF-02 line is in place.
- :7183 serves the screens. The menu is still hidden, and the registry rows go through TAB-1 and the generator.

The rest of this file is the readiness review as written before that.

## 1. Review: tenant isolation, permissions, validation, transactions

### Tenant isolation
Every H11 query starts from a company-scoped root: `Payers`, `Policies`, `Preauths`, `Batches`, `Claims` or `Lines(ctx)`, each `Where(x => x.CompanyId == ctx.CompanyId)`.

Joined H1–H10 tables carry their own predicate:
- patients, services and price lists: `p.CompanyId == ctx.CompanyId`;
- bills and bill lines: `b.CompanyId == ctx.CompanyId`.

**Writes check that every referenced id belongs to the company before using it:**

| Write | Ids checked |
|---|---|
| `SavePayerAsync` | price list |
| `SavePolicyAsync` | patient, payer |
| `RequestPreauthAsync` | patient, service |
| answer, submit and resubmit | the row is loaded through the company-scoped root, so a foreign id is `not_found` |

The batch build re-checks the company inside the pure rule `HospitalInsuranceRules.ClaimRefusal`. It covers the bill, the payer, and the policy (its company, its patient and its payer), so a row from another tenant is refused even if a future query forgets the scope.

### Permissions (reusing the existing hospital actions; no new action)

| Step | Action |
|---|---|
| screens, eligibility lookup | `read` |
| contracts | `manage` |
| patient policies | `register` |
| approval requests and answers, batches, the payer's answers, resubmission | `bill` |
| insurance patient picker | `read` **and** (`register` or `bill`) — see §5 |

### Validation
- Codes, names and member numbers are required, with lengths bounded and matching the SQL.
- Percentages are 0–100; the cap is ≥ 0; an end date cannot precede its start.
- The payer kind must be known, and unknown service categories are dropped.
- An approval answer needs a reference (approved) or a reason (rejected).
- A rejection needs both a code and a reason.
- An accepted amount must lie between 0 and the amount claimed.
- A resubmission needs a justification, and only a rejected line can be resubmitted.

The SQL repeats these as CHECK constraints.

### Defects found by this review, fixed in H11-owned files

| # | Defect | Fix |
|---|---|---|
| R1 | The claim added VAT to every line (`× (1 + VatRatePercent)`). The bill charges VAT only on `Taxable` lines, and only on the patient's part. | VAT is no longer claimed (`HospitalInsuranceRules.LineAmount`); VAT treatment is part of the accounting design (§7). |
| R2 | The contract discount was keyed on the payer's list, not on the list that priced the bill. | Keyed on `bill.PriceListId` (main list only). |
| R3 | Bills in a foreign currency were mixed into one batch, and rounding was fixed at 3 decimals. | A bill is claimed only in the contract's currency (`other-currency` refusal), rounded to that currency's decimals via `HospitalPricingService.CurrencyOfAsync`. |
| R4 | Two concurrent builds could create two drafts for the same payer and month, and so claim one bill twice. | Filtered unique index `UX_HospitalClaimBatches_Draft`. The loser hits it, or `UX_HospitalClaims_BatchBill`, rolls back whole and gets "Someone else changed this at the same moment". |
| R5 | Two people answering the same line or approval request at once: both see Pending and the last write wins. | Guarded `ExecuteUpdateAsync … WHERE Status = Pending`: only the first lands, and the second is told the answer is already recorded. |
| R6 | A line answer and the batch state were saved separately, with no transaction. | Both happen in one `ScopedTx`. |
| R7 | A line could be resubmitted twice concurrently. | The line is taken first with a guarded update (Rejected → Resubmitted), backed by `UX_HospitalClaimLines_Resubmits`. |
| R8 | Twin approval requests sent at once. | `UX_HospitalPreauths_Pending`, plus the `DbUpdateException` refusal. |
| R10 | **(found by TAB-0)** The 6 entities have no DbSet and no `ToTable`, so EF would query `dbo.HospitalPayer` etc. while the slice creates `dbo.HospitalPayers`: the screens would fail even with the SQL applied. | `e.ToTable(...)` for all 6. Guard `Every_mapped_entity_names_a_table_its_slice_creates` checks each table against a `CREATE TABLE` in the slice. |
| R11 | **(found by TAB-0)** The slice's prerequisite check missed `dbo.HospitalPriceLists` (and `dbo.HospitalBillLines`), both FK targets. | Added. Guard `Every_table_a_slice_references_but_does_not_create_is_a_checked_prerequisite` checks that every FK target the slice does not create is in its THROW check. |
| R9 | Clashing localization keys: `Approved` already means a verification step ("الاعتماد"); `Service` means a room kind ("Service area"). | The views use `pa:Approved` / `pa:Rejected` and the new `Service (catalogue)`. |

### Known limits (not defects in H11; they are integration items)
- **L1 — no payer on the bill.** A bill does not record which policy pays it. A patient insured with two payers has their bill claimed by whichever payer's batch is built first. The first claim excludes the second, so the bill is never claimed twice, but the choice is not made at billing. The fix is part of the billing integration (§7, step B1).
- **L2 — the patient is still charged in full.** Until the billing integration, an insured patient's bill still asks the patient for the full amount, because `HospitalBillingService.View()` sets `insurer = 0`.

## 2. SHF-02 — `CrossBuy/Models/Context/CrossDbContext.cs`
Registered owner: **Integration Owner**. The change rule is: add only in the tab's marked region; never reorder; never reformat.

**Exact change: one added line**, directly after the H10 registration inside `OnModelCreating`, with LF endings and tab indentation like its neighbours:

```diff
 			Hospital.HospitalRadiologyModel.Configure(builder);
 			Hospital.HospitalPharmacyModel.Configure(builder);
+			Hospital.HospitalInsuranceModel.Configure(builder);
 
 			CrossBuy.BL.Platform.CompanyQueryFilters.Apply(builder, this);
```

- No DbSet is added: the H11 service reaches its tables through `_db.Set<T>()`.
- No `CompanyQueryFilters` change is needed: H11 scopes every query explicitly.
- The line must land **together with** applying the SQL slice (§4). With the line and no tables, the first query on an H11 screen fails (invalid object name). The rest of the system is unaffected: EF does not touch a table until it is queried.

## 3. Menu — `CrossBuy/Models/Menu/MainMenu.cs`
The ownership tool reports this file as UNCLAIMED. Owner review is requested before editing.

Two items go in the existing "المالية والتأمين / Finance & insurance" group, after "Price lists". The labels are inline and bilingual, like every hospital menu item, so no resx key is needed:

```diff
 				new() { LabelAr = "قوائم الأسعار", LabelEn = "Price lists", Action = "PriceLists", Controller = "Hospital" },
+				new() { LabelAr = "الموافقات والمطالبات", LabelEn = "Approvals & claims", Action = "Insurance", Controller = "Hospital" },
+				new() { LabelAr = "شركات التأمين والعقود", LabelEn = "Payers & contracts", Action = "Payers", Controller = "Hospital" },
 				new() { LabelAr = "إعدادات الفوترة", LabelEn = "Billing settings", Action = "BillingSettings", Controller = "Hospital" },
```

This link is safe to add only after §2 and §4. Until then both screens fail on their first query.

## 4. SQL slice
- File: `CrossBuy/deploy/sql/hospital_h11_insurance.sql` (the docs copy was removed: it was byte-identical). It creates six new tables and four filtered or plain unique guards, is idempotent, runs in one transaction, and alters nothing that exists.
- **It has never been executed.**
- Integration steps, each needing approval:
  1. Move the file to `CrossBuy/deploy/sql/hospital_h11_insurance.sql`. `deploy/sql` belongs to TAB-1.
  2. Register the slice in `manifest.json`, `sql-slices.json`, `roadmap_v2_data2.py`, Roadmap-V2-09 and `required-evidence-manifest.json`. That is the same five places as SQL-38.
     - Memory: registry edits are manifest-coupled.
     - Memory: Roadmap-V2-09 projects two registries.
  3. Apply the slice to CrossBuyDB2.
- Prerequisites the file asserts: H2 patients and H6 billing. H7 price lists are referenced by a foreign key.

## 5. Patient picker permission
- **The mismatch:** the insurance screens used `/Hospital/Patients/Search`, which is gated on `admit`. A registration clerk (`register`) or a cashier (`bill`) without `admit` could not pick a patient.
- **Resolved inside H11, without widening `admit`:** a new endpoint `GET /Hospital/Insurance/Patients/Search`.
  - It requires `read` plus either `register` or `bill`, and refuses with 403 otherwise.
  - It reuses `HospitalPatientService.ListAsync`, so it returns no more than the `read`-gated Patients screen already shows.
  - `Insurance.cshtml` and `Payers.cshtml` now call it. The admission picker is unchanged.

## 6. Localization — for TAB-1 review (`CrossBuy/Resources/HospitalResources{,.ar,.en,.fr}.resx`)
- **171 keys used by H11.**
  - **41 already exist** in all four files. Their meanings were checked against H11's use. Two clashed and are no longer used by H11 (R9).
  - **130 are new.** All are in `docs/hospital/h11/strings.json`, each with `en`, `ar` and `fr`.
  - No key is present in some files and missing in others. No prepared key is unused or already in the resx.
- Four prefixes are new families: `pa:`, `cb:`, `cl:`, `inelig:`. Until they are merged, these labels render as raw keys (e.g. `cb:Partial`); the other labels fall back to their English text.
- **Merge rule (governance README):** resx keys are added via git plumbing, never a whole-file `git add`, and never with a non-Unicode write (memory: `resx-arabic-destroyed-by-codepage`).

## 7. Accounting integration — design only (no production code changed)

### Today
- At close, a bill posts Dr patient receivable (patient share + VAT) and Cr revenue (gross by revenue account), plus Cr output VAT.
- `View()` sets the insurer share to 0, so the entry balances only because nothing is insured.
- **Any change that makes `Insurer > 0` without adding the payer-receivable debit produces an unbalanced entry.** It must ship as one unit.

### Target entries (bill currency, converted at the bill's frozen rate like today)

| Event | Debit | Credit |
|---|---|---|
| **B1. Bill closes, insured** | Patient receivable = co-pay + uncovered + VAT · **Payer receivable (per payer)** = insurer share | Revenue (gross after the contract discount) · Output VAT |
| **B2. Claim line accepted in full** | — (the receivable stands) | — |
| **B3. Accepted for less / rejected** | **Claim rejection expense** (or rebilled to the patient, per contract) = the shortfall | Payer receivable = the shortfall |
| **B4. Resubmitted line later accepted** | Payer receivable = the amount re-accepted | Claim rejection expense (reversal of B3) |
| **B5. Payer remittance received** | Bank | Payer receivable = amount paid |
| **B6. Remittance differs from accepted (short-pay, bank charges)** | Claims adjustment expense | Payer receivable |
| **B7. Rejection recharged to the patient (contract allows)** | Patient receivable | Payer receivable — instead of B3's expense |

### Rules
1. **One split, frozen at close.** At bill close, billing calls `HospitalInsuranceRules.Split` with the policy that covers the closing day. It stores, per line, the payer amount, and per bill, the `PolicyId`. These are new nullable bill columns in a billing slice.
   - The claim then **reads** those frozen numbers instead of recomputing them. This closes L1 (the bill names its payer) and removes the second computation (no duplicated code).
2. **The discount is priced, not claimed.** A main-list contract's discount is applied when the bill is priced (H7 `QuoteItemAsync` / services). Revenue is posted net, and the claim never carries a discount of its own.
3. **VAT stays on the patient part.** Kuwait levies no VAT today, and GCC states that do generally exempt or zero-rate healthcare. Where a country taxes a service, the VAT on the payer's part is a payer-receivable line, decided by the country setting (`c.vatRule` in the prototype). The owner or the accountant must confirm this per country before step 2.
4. **Mapped accounts, never a hard-coded id.** New billing settings:
   - `PayerReceivableAccountId`, overridable per payer;
   - `ClaimRejectionAccountId`;
   - `ClaimAdjustmentAccountId`.

   The close and the answer must refuse with "Map the … account in billing settings" rather than post to a default. This is the H10 lesson: account 30 was a depreciation account.
5. **Journals through the existing service.** Every entry goes through `JournalEntryService` inside the same `ScopedTx` as the claim change. Every entry references the batch number and the bill number, and a reversal is a reversing entry, never an edit.
6. **The period.** The answer (B3/B4) and the remittance (B5/B6) post in the period of the event. A closed accounting period refuses them, as `AccountingPeriodControlService` already does.
7. **A cashier's patient due** = co-pay + uncovered + VAT − paid. `HospitalBillTotals.Due` already computes `Patient + Vat - Paid`, so only the `Insurer` term needs filling.

### Integration order (each step separately authorised)
1. SQL: billing columns `HospitalBills.PolicyId` and `HospitalBillLines.InsurerAmount`, plus the settings accounts. This is a new billing slice, not H11's.
2. `HospitalBillingService`: `View()` reads the frozen insurer share; close posts B1; settings refusals.
3. `HospitalInsuranceService.BuildBatchAsync` reads the frozen split and policy instead of recomputing them.
4. Answer and remittance postings B3–B7. A remittance screen is not built yet.
5. Tests: entry balance, per-event postings, and refusals on unmapped accounts.

## 8. Dependency matrix

| # | Shared or foreign file | Owner / registry | Proposed change | Approval needed | Evidence | Blocks |
|---|---|---|---|---|---|---|
| D1 | `CrossBuy/Models/Context/CrossDbContext.cs` | SHF-02, Integration Owner | +1 line (§2) | TAB-0 confirmed it is the Integration Owner. It put the line and the slice (D2 + D3) to the owner as **one decision**; no window until the owner decides. Do not edit until then. | `HospitalInsuranceGuardTests` builds the H11 model with the SQL Server conventions and no database | every H11 screen at runtime |
| D2 | `CrossBuy/deploy/sql/hospital_h11_insurance.sql` + the 5 registries | TAB-1 (deploy/sql); registry owners | move + register (§4) | TAB-1 + owner | slice guards asserted by `The_slice_declares_the_same_guard` | D1 being useful; runtime |
| D3 | CrossBuyDB2 | owner | apply the slice | owner (no DB write is authorised now) | — | runtime verification |
| D4 | `CrossBuy/Resources/HospitalResources{,.ar,.en,.fr}.resx` | TAB-1 | +130 keys from `strings.json` (§6) | TAB-1 | the key comparison: 171 used / 41 existing / 130 new / 0 partial | translated UI (raw keys for 4 prefixes until then) |
| D5 | `CrossBuy/Models/Menu/MainMenu.cs` | UNCLAIMED | +2 items (§3) | owner | — | discoverability only (URLs work) |
| D6 | `CrossBuy/BL/Hospital/HospitalBillingService.cs` + a billing slice | unclaimed hospital file; no production change authorised | §7 steps 1–3 | owner + accounting decision | — | patient charged only the co-pay; payer receivable; L1 |
| D7 | `Program.cs` | SHF-01 | **none**: the service is built with `ActivatorUtilities` | — | build | — |
| D8 | `Views/Shared/_CbConfirm.cshtml` | TAB-1 | **none**: `cbPrompt` lives in `Views/Hospital/_HospitalPrompt.cshtml` | — | — | — |
