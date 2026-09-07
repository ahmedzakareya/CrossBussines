# Stage-Construction-06 — Contracts and Progress Certificates

Covers Phase 8 (contract management) and Phase 9 (progress certificates).

---

## 1. Contracts today

There is no contract entity. A client contract is five nullable columns on the accounting project dimension —
`CustomerId`, `Location`, `ContractValue`, `AdvancePercent`, `RetentionPercent`
(`CrossBuy/Models/Context/Accounting/Dimensions.cs:36-44`) — and a subcontract is nine fields
(`Models/Context/Accounting/Subcontract.cs:6-18`). Consequences that follow directly:

- one project can hold exactly **one** implicit client contract (decision **D-01**);
- there is no contract number, currency, scope statement, payment terms, bond, insurance, warranty, DLP, or attachment;
- retention and advance percentages are project-level, so a tiered or capped retention is unrepresentable
  (decision **D-04**);
- a subcontract has **no allocated scope**, which is the direct cause of unbounded over-certification (CR-02).

## 2. `ClientContract`

| Field | Rule |
|---|---|
| `CompanyID`, `ProjectId` | Required. Decision **D-01** decides whether many contracts may share a project. |
| `ContractNo` | Unique per company. |
| `CustomerId` | The employer — an existing `Customer`. **No duplicate party master.** |
| `ConsultantName` / `ConsultantCustomerId` | The supervising consultant (often not the employer). |
| `CurrencyId` | **Required.** Today construction journals pick `_db.Currencies.First()` with no company filter in five places (CR-10); a contract currency is where the correct value comes from. |
| `ContractValue`, `SignedDate`, `CommencementDate`, `CompletionDate` | Contractual base. `RevisedCompletionDate` moves only through an approved EOT (`Stage-Construction-09`). |
| `ScopeDescription` | Free text, localised. |
| `BoqRevisionId` | The **approved** revision that constitutes the contract quantities. |
| `AdvancePercent`, `AdvanceRecoveryMethod`, `AdvanceRecoveryStartPercent` | Decision **D-05**. |
| `RetentionPercent`, `RetentionCeilingPercent`, `RetentionMethod`, `RetentionReleaseTrigger` | Decision **D-04**. |
| `TaxTreatment`, `DefaultTaxRate` | Reuses Accounting's tax setup; no construction tax engine. |
| `PaymentTermsDays`, `CertificateCycle` | e.g. monthly; drives the cash-flow plan. |
| `LiquidatedDamagesRate`, `LdCapPercent` | The basis for penalty deductions on a certificate. |
| `WarrantyMonths`, `DlpStartEvent`, `DlpMonths` | The DLP clock — today absent, which is why retention can be released early (CR-12). |
| `VariationRule`, `ClaimNoticeDays` | Contractual change/notice rules, captured so a claim can be tested against them. |
| `Status` | `Draft` → `Active` → `SuspendedByEmployer` → `Completed` → `HandedOver` → `Closed` → `Terminated`. |
| `RowVersion` | Concurrency token. |

Children: `ClientContractTerm` (typed key/value for terms not worth a column), `Bond`, `Insurance`,
`ContractAmendment` (previous values preserved, reason required), and document links to `LibraryItem`.

### 2.1 `Bond` and `Insurance`

| Field | Note |
|---|---|
| `Kind` | `Performance` \| `AdvancePayment` \| `Retention` \| `Bid` \| `Other` (bonds); `CAR` \| `ThirdParty` \| `WorkmensComp` \| `Other` (insurance) |
| `IssuerName`, `ReferenceNo`, `Amount`, `CurrencyId` | |
| `IssueDate`, `ExpiryDate`, `Status` | **Expiry is the point of the entity.** An expiring bond must raise a notification (event `Bond.Expiring`, third-tab consumer). |
| `LinkedAdvanceId` | An advance-payment guarantee ties to the advance it secures (`ContractService.ReceiveAdvanceAsync`). |

A bond or insurance record holds **no money**. It never posts. Any cash movement (bond fee, premium) is an ordinary
payable through Accounting.

## 3. `Subcontract` — extended

Existing fields stay. Added: `SubcontractNo`, `ScopeDescription`, `CurrencyId`, `StartDate`, `EndDate`,
`AdvancePercent` + recovery rule, `RetentionCeilingPercent`, `RetentionReleaseTrigger`, `PenaltyRate`/`PenaltyCap`,
`PaymentTermsDays`, `TerminationClause`, `Status` (`Draft` → `Active` → `Suspended` → `Completed` → `Terminated`),
`ApprovedBy/At`, `RowVersion`.

Children: **`SubcontractScope`** (the cap — see `Stage-Construction-04` §3.5), `SubcontractMilestone`,
`Bond`/`Insurance` (same entities, subcontract-linked), documents.

The subcontractor stays a `Vendor` (`Subcontract.VendorId`) — no subcontractor master is created.

## 4. Certificates

Two documents, one shape:

| | Client certificate (مستخلص) | Subcontractor certificate (مستخلص باطن) |
|---|---|---|
| Today | `ProgressBilling` + lines, `BL/ProgressBillingService.cs` | `SubcontractBilling`, **no lines**, `BL/SubcontractBillingService.cs` |
| Money direction | receivable (`ReceivableService`) | payable (`PayableService`) |
| Retention account | 1104 asset | 2105 liability |
| Basis | measured progress against BOQ | allocated scope |

### 4.1 Required state flow

```
Draft ─► Submitted ─► SiteEngineerReview ─► QuantitySurveyorReview ─► ProjectManagerApproval
      ─► CommercialReview ─► FinanceReview ─► Approved ─► Posted ─► PartiallyPaid ─► Paid
                                                              └─► Reversed / Corrected (where allowed)
```

Today there are three states — `Draft` / `Approved` / `Posted` (`Models/Context/Accounting/ProgressBilling.cs:18`) —
and `ApproveAsync` checks only that the status is `Draft` and gross work is positive
(`BL/ProgressBillingService.cs:191-200`).

Rules for the flow:

1. Each step records **who, when, and a decision** (`approve` / `return with comments`). A return sends the certificate
   back to a named earlier step, never silently to `Draft`.
2. Steps are configurable per company; the *sequence* is fixed, the *participation* of a step is not (a small
   contractor may collapse Site-Engineer and QS).
3. Approval limits by value are decision **D-03**.
4. `Posted` is reachable only from `Approved` (already true today) and only by a caller holding **both**
   `certificate.post` and Accounting `post` — the pattern `ProjectsAccessService` already establishes for `billing`
   (`BL/ProjectsAccessService.cs:104-117`).
5. `PartiallyPaid`/`Paid` are **derived** from the AR/AP position of the linked invoice, never typed by a user.
6. Correction after `Posted` is a **reversal** (`JournalEntryService.ReverseAsync` through the owning service) plus a
   new certificate, or an explicit adjustment line on the next certificate. **Never** an edit of a posted certificate
   and never a row deletion.

### 4.2 Certificate values

| Value | Today | Required |
|---|---|---|
| Previous certified | derived per BOQ item from posted billings (`ProgressBillingService.cs:77-85`) | keep; store as a **line snapshot** so it is not recomputed against a changed BOQ |
| Current quantity / value | period = cumulative − previously billed (line 110) | keep — this arithmetic is correct |
| Cumulative quantity / value | from the measurement | keep, per line |
| Retention | `W × RetentionPercent` (line 118) | + ceiling, method, per-item applicability (**D-04**) |
| Advance recovery | `min(W × AdvancePercent, remaining 2104)` (line 120) | keep the cap — it is correct; add method (**D-05**) |
| Penalties (LD) | absent | new: computed from contract LD rate, capped, with an override reason |
| Other deductions | absent | new: typed deduction lines (utilities, back-charges, cross-charges) |
| Taxes | `W × TaxRate` (line 117) | keep; per-item tax where the BOQ item says so |
| Materials on site | absent | new component with its own recovery as work is executed (**D-06**) |
| Variations | only implicitly, via BOQ items tagged `VariationOrderId` | explicit: certificate shows variation-sourced value separately |
| Net payable/receivable | `W + T − R − A` (line 121) | `W + MOS + T − R − A − LD − Deductions` |

### 4.3 `CertificateLine` — required additions

`BoqItemId` (exists) · **`BoqRevisionId`** · **`WbsNodeId`** · **`CostCodeId`** · `ContractedQuantity`,
`ContractedRate` (snapshot at certification) · `CumulativeQuantity`, `PreviousQuantity`, `PeriodQuantity` ·
`CumulativeValue`, `PreviousValue`, `PeriodValue` · `RetentionApplicable` · `VariationId` (when the line comes from a
variation) · `RowVersion` · a `CertificateLineHistory` row per change.

**The snapshot is the point.** Today a certificate line stores only value figures and points at a `BoqItemId` whose
quantity and rate can be overwritten afterwards by a variation (`BL/VariationOrderService.cs:128`). With
`BoqRevisionId` plus contracted quantity/rate snapshots, a posted certificate can always be re-derived exactly as it
was certified.

### 4.4 Hard rules

| Rule | Mechanism |
|---|---|
| Cumulative quantity cannot exceed approved quantity without an approved variation | Check at submit **and** re-check at post, against the approved revision; refusal names the item. Today over-execution is silently capped instead (`BL/ProgressService.cs:85`, CR-08). |
| Previous certified values cannot be rewritten | Posted certificate lines are immutable; previous values are snapshots, not recomputations. |
| Correction uses adjustment or reversal | §4.1 rule 6. |
| Posting remains owned by Accounting | `ReceivableService` / `PayableService` / `JournalEntryService` only — as today. |
| Payment remains owned by Accounting | Certificate reads payment status; it never records a payment. |
| Certificate retains the source BOQ revision | `CertificateLine.BoqRevisionId`. |
| Line-level audit | `CertificateLineHistory`. |
| Subcontractor certified ≤ allocated scope | `SubcontractScope` cap (**D-07**). Closes CR-02. |

### 4.5 Posting atomicity (CR-04 / CR-05 — Accounting-owned)

`BL/ProgressBillingService.PostAsync` (lines 202-258) performs three separate postings — invoice, retention receipt,
advance-recovery receipt — with **no ambient transaction**, and recovers the created receipt ids with
`MAX(ID)` before/after (lines 233-236, 242-245). `SubcontractBillingService` does the same for payments (164-167).

Two things are required, and both belong to the Accounting owner:

1. One ambient transaction across the certificate's postings, or an explicit `PartiallyPosted` state that a resume
   operation can complete idempotently.
2. Created-document ids **returned** by the receivable/payable services rather than inferred from `MAX(ID)`.

Construction must not "solve" this by posting the ledger itself. Until it is resolved, the certificate design assumes
posting can fail midway and therefore keeps the certificate's own status transition as the last write.

## 5. Retention and advance — reuse, do not rebuild

`BL/ContractService.cs` is correct and stays:

- advance received = Dr cash / **Cr 2104 liability**, ProjectId-tagged (`ContractService.cs:73-88`);
- retention withheld inside the certificate as a settlement receipt into **1104** (`ProgressBillingService.cs:230-237`);
- subcontractor retention held as **Cr 2105** and released by `ReleaseSubRetentionAsync`;
- balances read from **posted GL lines**, not a shadow table (`ContractService.cs:52-58`);
- release refuses an amount above the GL balance (`ContractService.cs:104-105`).

What construction adds around it: a **retention ledger view** per contract (withheld per certificate, released, due at
which trigger), a **DLP gate** on release (CR-12), and multiple advances with their own recovery rules.