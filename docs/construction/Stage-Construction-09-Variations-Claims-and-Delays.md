# Stage-Construction-09 — Variations, Claims and Delays

Covers Phase 10. Variations exist today and are the highest-severity design defect in the module (CR-03); claims,
delay events and EOT do not exist at all.

---

## 1. What variations do today

`BL/VariationOrderService.cs` + `Models/Context/Accounting/VariationOrder.cs`:

- two states — `Draft` and `Approved`;
- a line is `Kind = New` or `Kind = Adjust`;
- on approve, `New` lines are inserted as `BoqItem` rows tagged `VariationOrderId` (`VariationOrderService.cs:136`), and
  **`Adjust` lines overwrite the live `BoqItem.Quantity` and `UnitPrice` in place** (line 128), keeping the previous
  values only on the variation line (`OldQuantity`/`OldUnitPrice`, `VariationOrder.cs:44-45`);
- revised contract value = `Project.ContractValue` + Σ approved variation values (`RevisedContractValueAsync`, line 55),
  with the original preserved — that part is right;
- no journal entry is created, which is also right: a scope change is not a financial event.

**Why the in-place overwrite is the defect.** A certificate line stores `BoqItemId` and value figures but no revision
and no rate snapshot (`Models/Context/Accounting/ProgressBilling.cs:38-49`). So after an `Adjust` variation, a
previously posted certificate can no longer be re-derived from its own data — its basis changed underneath it. Combined
with CR-01 (BOQ delete-and-reinsert) the certified history is not reproducible.

## 2. Variation model

### 2.1 Five documents, one thread

| Document | Purpose |
|---|---|
| **VariationRequest** | The originating instruction or proposal — who asked, why, under which contract clause. |
| **VariationEstimate** | The priced assessment: cost impact, time impact, method, assumptions, validity. More than one estimate may exist (options A/B/C). |
| **VariationApproval** | The authority record: internal approval, then client approval, each with reference and date. |
| **ContractVariation** | The approved change to the contract: value, revised completion date, the BOQ revision it produced. |
| **BoqRevision / BudgetRevision** | The *effects*, produced by approval — never edits to existing rows. |

### 2.2 State flow

```
Draft ─► Submitted ─► Estimated ─► UnderReview ─► ClientApprovalPending
      ─► Approved ─► Implemented ─► IncludedInCertificate ─► Closed
                 └─► Rejected
```

Rules:

1. `Approved` requires an authority record (internal + client where the contract demands it) and a stated cost **and**
   time impact — "time impact: none" must be recorded explicitly, not left null.
2. `Approved` **emits** a new `BoqRevision` (`Source = Variation`, `VariationId` set) and, where the value changes the
   plan, a `ProjectBudget` revision. It never writes over a BOQ line. **This is the change that closes CR-03.**
3. `Implemented` means the revision is approved and live; `IncludedInCertificate` is derived from certificate lines
   carrying `VariationId`.
4. A rejected or withdrawn variation leaves its estimate and its documents intact — history stays whole.
5. Approval authority by value is decision **D-03**'s sibling: a variation approval limit table, same mechanism.

### 2.3 Tracked fields

`Originator` (client / consultant / contractor / subcontractor) · `Reason` (design change, site condition, client
instruction, statutory, error, omission) · `InstructionReference` + date · `CostImpact` (+ currency, + breakdown by cost
code) · `TimeImpactDays` + affected WBS/activities · `BoqImpact` (lines added/adjusted/omitted) · `BudgetImpact` ·
`SubcontractImpact` (which subcontract scopes change, and whether the sub has agreed) · documents · approvals ·
`RowVersion`.

**Subcontract impact is not optional.** A variation that adds work usually changes a subcontractor's allocated scope
(`SubcontractScope`, `Stage-Construction-04` §3.5); if the scope is not updated, the sub's certification cap is wrong.

### 2.4 Migration of existing variations

Existing `VariationOrder` rows stay. At C7:

- `Adjust`-line history already captured in `OldQuantity`/`OldUnitPrice` is used to synthesise a **retrospective**
  `BoqRevision` chain, so the current BOQ becomes revision *n* with a documented (if incomplete) lineage;
- the in-place write path is removed;
- posted certificates are stamped with the revision that was current at their posting date — an approximation, honestly
  labelled as reconstructed rather than recorded. It cannot be better than the data allows, and pretending otherwise
  would be worse.

## 3. Claims

`Claim` — entitlement, not an invoice. A claim becomes money only through a variation or a certificate line.

| Field | Note |
|---|---|
| `CompanyID`, `ProjectId`, `ClientContractId` \| `SubcontractId` | A claim may be **against** the employer or **from** a subcontractor — `Direction` (`Outgoing` \| `Incoming`) distinguishes them. |
| `ClaimNo`, `ClaimType` | `Prolongation` \| `Disruption` \| `Acceleration` \| `Variation` \| `PaymentDelay` \| `Termination` \| `Other` |
| `Cause`, `ResponsibleParty` | employer / consultant / contractor / subcontractor / third party / force majeure |
| `NoticeDate`, `NoticeReference`, `ContractualNoticeDays`, `NoticeWithinTime` (derived) | **The derived flag matters:** a claim served late is often barred, and the contract's notice period is on `ClientContract.ClaimNoticeDays`. |
| `Entitlement` (`Cost` \| `Time` \| `Both`) | |
| `ClaimedAmount`, `AssessedAmount`, `SettledAmount`, `CurrencyId` | Each stored separately; a settlement never overwrites the claim. |
| `ClaimedDays`, `AssessedDays`, `AwardedDays` | |
| `Evidence` children | `LibraryItemId` links: DSRs, photos, correspondence, delay analysis. |
| `Correspondence` children | date, direction, reference, summary, `LibraryItemId`. |
| `Status` | `Draft` → `NoticeServed` → `Submitted` → `UnderAssessment` → `Negotiation` → `Agreed` \| `Rejected` → `Settled` → `Closed` (+ `Escalated` for arbitration/litigation) |
| `SettlementVariationId` / `SettlementCertificateId` | How the money actually arrived. |
| `RowVersion` | |

**Rules:** a claim never posts; `SettledAmount` must be reconcilable to the variation or certificate line that carried
it; assessed and settled values are separate fields so the negotiation history survives; a rejected claim is retained.

## 4. Delay events

`DelayEvent` — the factual record that claims and EOT are built from.

| Field | Note |
|---|---|
| `EventDate`, `StartDateTime`, `EndDateTime`, `DurationHours` | |
| `Cause`, `CauseCategory` | weather / access / design / instruction / material / labour / equipment / utility / authority / force majeure |
| `AffectedWbsNodeIds`, `AffectedActivityIds` | Multiple nodes per event. |
| `ResponsibleParty` | employer / consultant / contractor / subcontractor / third party / none |
| `IsExcusable`, `IsCompensable` | **Recorded as two explicit decisions**, with the classifier and a reason. Excusable-but-not-compensable is the common and most-argued case, so it must be first-class rather than inferred. |
| `IsCritical`, `CriticalPathImpactDays` | Whether the delay hit the critical path — requires a schedule (C7). Until a baseline schedule exists this is an assessed judgement, flagged as such. |
| `EotDays`, `CostImpact` | Assessed impact. |
| `DsrDelayLineId` | The site record it came from — a delay first noticed in a DSR should not be re-typed. |
| `ClaimId`, `VariationId` | Where it was escalated. |
| `Status` | `Recorded` → `Assessed` → `NotifiedToClient` → `Accepted` \| `Disputed` → `Closed` |

### 4.1 Extension of time

`ExtensionOfTime` — `ClientContractId` · claimed/assessed/awarded days · linked `DelayEvent` ids · `NewCompletionDate` ·
approval chain · `Status` (`Requested` → `UnderAssessment` → `Granted` \| `PartiallyGranted` \| `Rejected`).

**Hard rule:** `ClientContract.RevisedCompletionDate` may be written **only** by an approved EOT. Today
`Project.EndDate` (`Models/Context/Accounting/Dimensions.cs:32`) is an ordinary editable column — a contractual
completion date can move with no reason, no approval and no history, and liquidated damages are computed from it.

### 4.2 Schedule variance

Schedule variance requires a **baseline** that does not exist today. Design placement: C7, with
`WbsNode.BaselineStart/BaselineFinish` captured at budget-baseline approval and `Activity` (C7) carrying planned dates,
so variance = actual/forecast finish − baseline finish, adjusted by awarded EOT days. Until then, schedule variance is
reported as *not measurable* rather than approximated from cost — **CR: never derive schedule progress from financial
progress** (`Stage-Construction-10`).

## 5. Interaction map

```
DSR delay line ──► DelayEvent ──► ExtensionOfTime ──► ClientContract.RevisedCompletionDate
                        │                                   │
                        └──────► Claim ◄────────────────────┘
                                   │
   VariationRequest ──► Estimate ──┴─► Approval ──► ContractVariation
                                                        ├─► BoqRevision  (never an in-place edit)
                                                        ├─► Budget revision
                                                        ├─► SubcontractScope update
                                                        └─► certificate lines tagged VariationId
```

## 6. Events proposed by this phase

`Variation.Raised` · `Variation.Approved` · `Claim.Raised` · `Delay.Recorded` — defined in
`Stage-Construction-12-Business-Events.md`, raised by nobody in this increment.

## 7. Open decisions touching this phase

| Decision | Effect |
|---|---|
| **D-03** (approval limits) | The same limit mechanism governs variation approval authority. |
| **D-07** (over-certification) | Determines whether a scope increase must precede over-execution or may follow it. |
| Variation numbering per contract vs per project | Follows **D-01**; recorded on screen 13's unresolved column. |
| Whether a claim may be raised without a served notice | Contract-dependent; the model records `NoticeWithinTime` either way rather than blocking. |