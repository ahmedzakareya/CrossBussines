# Stage-Construction-08 — Quality, Technical Control and Document Control

Covers Phase 14 (quality and technical control) and Phase 15 (drawing and document control). Also carries handover,
defects liability and closeout, which are the documents that consume this phase's output.

None of this exists today. The closest artefacts are `BL/InventoryApprovalService.cs` (a stock approval) and the
platform file manager (`Models/Context/Library/LibraryItem.cs` + `BL/FileManagerService.cs`), which is a folder tree
with soft delete and **no revision, no supersede link and no current-revision flag**.

---

## 1. The common document header

Seven quality documents share one field set. Sharing the *shape* (not the table) is what keeps their screens, permission
model, audit and reporting consistent.

| Field | Rule |
|---|---|
| `CompanyID`, `ProjectId` | Required. `SiteId` where site-specific. |
| `DocumentNo` | Sequential per project per type, via the existing `NumberSequence` mechanism (`Models/Context/Accounting/GeneralLedger.cs:57`) — no new numbering engine. |
| `DisciplineId` | Civil / Structural / Architectural / Mechanical / Electrical / Plumbing / HSE / Other — user-extensible lookup. |
| `WbsNodeId` | The work it concerns. |
| `LocationText`, `Level`, `Zone` | Where on site. |
| `DrawingRegisterId`, `DrawingRevisionId` | The drawing referenced (and **which revision**). |
| `SpecificationReference` | Clause reference. |
| `RaisedByEmployeeId`, `RaisedAt` | |
| `AssignedToEmployeeId` / `AssignedToParty` | Internal or external (consultant/client) addressee. |
| `ResponseDueDate` | The clock. A document with no due date cannot be chased. |
| `Status` | Per-type state machine (below). Never a free string. |
| `ClosedAt`, `ClosureEvidence` | What proved it closed. |
| `RevisionNo` | Re-submissions are revisions, not overwrites. |
| `RowVersion` | Concurrency token. |
| children | `Comment` (or the third tab's comment infrastructure when onboarded), `Attachment` → `LibraryItemId`, `ApprovalStep` |

## 2. The seven documents

| Document | State machine | Notes |
|---|---|---|
| **RFI** (request for information) | `Draft` → `Submitted` → `UnderReview` → `Answered` → `Closed` (+ `Reopened`) | Response due date and overdue tracking are the whole value. Escalation to a `DelayEvent` or `Claim` when an answer is late. |
| **InspectionRequest** (IR / WIR) | `Draft` → `Requested` → `Scheduled` → `Inspected` → `Approved` \| `ApprovedWithComments` \| `Rejected` → `Reinspection` → `Closed` | Optional **hold-point** rule: a WBS node may be configured so that covering work without an approved IR is blocked or warned. |
| **MaterialInspection** (MIR) | as IR, plus `GoodsReceiptId` / delivery reference | Links the physical delivery to the approved submittal. |
| **Submittal** (material approval) | `Draft` → `Submitted` → `UnderReview` → `ApprovedA` \| `ApprovedB` (with comments) \| `Rejected C` → `Resubmitted` | The A/B/C status codes are consultant convention (decision **D-13** applies the same codes to drawings). Whether an approved submittal *binds* procurement is an open decision (screen 23). |
| **MethodStatement** | `Draft` → `Submitted` → `UnderReview` → `Approved` \| `Rejected` | Linked to the WBS node it governs; optionally a hold point for that node. |
| **Ncr / CorrectiveAction** | NCR: `Open` → `RootCauseIdentified` → `CorrectiveActionProposed` → `ActionApproved` → `Verified` → `Closed` | Records cost of rework where quantified; may originate a back-charge deduction on a subcontractor certificate. |
| **PunchItem** (snag) | `Open` → `InProgress` → `ReadyForVerification` → `Verified` → `Closed` \| `Rejected` | Belongs to a punch list per area/system; handover-gating. |

**Technical query** is modelled as an RFI with `Kind = TechnicalQuery` rather than a separate table — the shape and the
clock are identical, and two tables would fork the register.

## 3. Document and drawing control

### 3.1 The problem in one sentence

`LibraryItem` (`Models/Context/Library/LibraryItem.cs:6-18`) has `Name`, `StoredPath`, `ContentType`, `Size`,
`ParentId`, `DeletedAt` — and nothing that distinguishes revision C from revision D. **A superseded drawing is
therefore indistinguishable from the current one**, which is the highest-consequence gap in this document (CR-13): the
site can build to a withdrawn revision and the cost of that is rework.

### 3.2 Two entities, no second file store

**`DocumentRegister`** — the controlled document identity (one row per document, forever):

`CompanyID` · `ProjectId` · `DocumentNo` (unique per project) · `Title`, `TitleEn` · `DocumentType`
(`Drawing` \| `ShopDrawing` \| `AsBuilt` \| `MaterialSubmittal` \| `MethodStatement` \| `Specification` \|
`ContractDocument` \| `ConsultantInstruction` \| `SiteInstruction` \| `Correspondence`) · `DisciplineId` ·
`WbsNodeId` (optional) · `Confidentiality` (`Public` \| `ProjectTeam` \| `Commercial` \| `Restricted`) ·
`CurrentRevisionId` · `Status` (`Active` \| `Withdrawn`) · `RowVersion`.

**`DocumentRevision`** — one row per revision, **immutable once issued**:

`DocumentRegisterId` · `RevisionCode` (`A`, `B`, `C`, `0`, `1`…) · `RevisionSequence` (integer, orders revisions
regardless of code style) · `LibraryItemId` (**the only place bytes are referenced**) · `Status`
(`Draft` → `ForReview` → `Approved` (`A`/`B`/`C` per **D-13**) → `Issued` → `Superseded` → `Withdrawn`) ·
`IssueDate`, `ReceivedDate` · `SubmittedBy`, `ReviewedBy`, `ApprovedBy` (+ timestamps) ·
`SupersedesRevisionId`, `SupersededByRevisionId` · `IsCurrent` · `ChangeDescription` · `RowVersion`.

**`DocumentDistribution`** — who was issued which revision, when, and whether they acknowledged. Without it, "the site
was informed" is an assertion rather than a record.

### 3.3 The current-revision invariant

> **At most one `DocumentRevision` per `DocumentRegister` may have `IsCurrent = true`.**

Enforced three ways, because one is not enough:

1. A **filtered unique index** in `deploy/sql` on `(DocumentRegisterId)` where `IsCurrent = 1`.
2. Service logic that supersedes the previous current revision **in the same transaction** as issuing the new one.
3. `DocumentRegister.CurrentRevisionId` kept consistent, and a test that asserts the three agree.

### 3.4 Prevention and warning behaviour

| Where | Behaviour |
|---|---|
| Document Revision Viewer (screen 26) | A superseded revision opens with a **persistent, non-dismissible banner** naming the current revision and linking to it. Whether download is *blocked* or merely warned is decision **D-13**'s consequence. |
| Any document reference on a site document (RFI, IR, DSR) | Stores `DrawingRevisionId`, not just `DocumentRegisterId`. If that revision has since been superseded, the referencing document shows the drift explicitly ("raised against Rev C; current is Rev D"). |
| Mobile | The offline cache holds only revisions that were current at sync time and marks its own staleness; a document that superseded while offline shows as stale rather than as current. |
| Notification | `DrawingRevision.Issued` (event catalogue) drives a distribution notification to the site team — third-tab consumer, requested not built. |
| Search / register list | Superseded revisions are visible (history stays whole) but visually distinct and never the default. |

### 3.5 As-built documents

`DocumentType = AsBuilt` with a link to the drawing it as-builts, and a **handover completeness check**: the handover
document cannot be completed while any WBS node marked `RequiresAsBuilt` has no approved as-built revision.

---

## 4. Handover, defects liability and closeout

These consume the quality output and are placed here because their gates are quality gates.

### 4.1 `Handover`

`ProjectId` · `ClientContractId` · `HandoverType` (`Partial` \| `Substantial` \| `Final`) · scope (WBS nodes / systems) ·
`RequestedDate`, `AchievedDate` · `Status` (`Requested` → `InspectionInProgress` → `SnagsIssued` → `SnagsCleared` →
`CertificateIssued` → `Accepted`) · `PunchListId` · `AsBuiltComplete` (derived) · client signatory · attachments.

**Gate:** `Accepted` requires zero open punch items above a configured severity, and as-built completeness.

### 4.2 Defects liability period

`DlpStartDate` (from the handover event named by `ClientContract.DlpStartEvent`) · `DlpMonths` · `DlpEndDate` (derived) ·
`Status` (`Active` \| `Expired` \| `Extended`) · `Defect` children (reported date, description, responsible party —
own/subcontractor, rectification cost, `NcrId`, closed date).

**Gate (closes CR-12):** retention release is refused while the DLP is `Active`, unless an authorised override with a
reason is recorded. Today `BL/ContractService.ReleaseRetentionAsync` checks only that the amount does not exceed the GL
retention balance (`ContractService.cs:104-105`) — the DLP does not exist to check.

### 4.3 Closeout

`ProjectCloseout` with a **checklist**: final certificate posted · retention released or scheduled · all subcontracts
closed and their retention settled · all variations closed · all claims settled or written off · all RFIs/IRs/NCRs
closed · as-builts complete · bonds returned · insurance closed · DLP expired · final cost reconciled to the GL.

**On close:** new cost allocations, certificates and site documents against the project are **blocked** (the meaning
that `ProjectsActions.Close` — `BL/ProjectsAccessService.cs:41` — currently has no process behind it). Decision **D-15**
sets whether `Closed` and `DlpActive` are separate states; the recommendation there is that they are.

---

## 5. Reuse summary for this phase

| Needed | Reused from | Not built |
|---|---|---|
| File bytes, folders, soft delete | `LibraryItem` + `FileManagerService` | any construction file store |
| Document numbering | `NumberSequence` | a construction numbering engine |
| Comments and mentions on a document | THIRD tab, when the entity is onboarded | a construction comment system |
| Notifications on issue/overdue | `INotificationService` (direct is permitted where an entity is not onboarded) → projection later | a construction notifier |
| Approval steps | platform approval engine if offered (CR-14); otherwise a construction-local chain | a copy of the HR leave workflow |
| Discipline / trade lookups | new construction lookups, user-defined (the `ProjectActivityType` precedent, `Dimensions.cs:49`) | hard-coded lists |