# Stage-Construction-04 — WBS and BOQ Design

Two entities that the current model conflates into one. `BoqItem.ParentId` is the only tree in the system, and
`BL/BoqService.ReplaceAllAsync` rebuilds it **from row order** (`BoqService.cs:128-132`) — that is a presentation
grouping for an editor, not a work breakdown.

---

## 1. Seven concepts that must not collapse into one entity

| Concept | Answers | Owner | Stable? | Carries money? |
|---|---|---|---|---|
| **WBS node** | *What work, decomposed how?* | Construction | **Yes — identity is permanent** | Owns budget and cost, not values of its own |
| **BOQ item** | *What did we contract to deliver, at what quantity and rate?* | Construction | Yes, **per revision** | Contractual quantity × rate |
| **Cost code** | *What kind of cost is this?* | Construction | Yes, company-wide | Categorises cost; holds no amount |
| **Activity** | *What is scheduled, in what sequence, for how long?* | Construction (later, C7) | Yes | No |
| **Task** | *Who must do what by when?* | Tasks module (`TaskItem`) | Yes | No (timesheet hours only) |
| **Cost centre** | *Which part of the organisation?* | Accounting (`CostCenter`, org-tree mirror) | Yes | Rollup dimension |
| **Project phase** | *Which stage of the project lifecycle?* | Construction (a WBS level or a project attribute) | Yes | No |

The distinctions that matter most in practice:

- **WBS ≠ BOQ.** One WBS node may cover many BOQ items; one BOQ item may span WBS nodes (allocated by quantity). The
  BOQ is *contractual*; the WBS is *managerial* and survives BOQ revisions untouched.
- **WBS ≠ Cost code.** WBS says *where in the work*; cost code says *what kind of cost*. Cost analysis needs both axes —
  the current model has neither.
- **WBS ≠ Task.** A task is an assignment with an assignee and a due date; a WBS node is a scope container that exists
  whether or not anyone is assigned. Tasks may *reference* a WBS node later (C-phase, Tasks module owned).
- **Cost code ≠ Cost centre.** `CostCenter.SourceHierarchicalId` (`Dimensions.cs:15`) proves the existing entity is an
  organisational mirror. Overloading it as a construction cost code would destroy the branch/department rollup that
  already depends on it.

---

## 2. WBS model

### 2.1 Required hierarchy

```
Project
 └── WbsNode  (unlimited practical depth, hard-limited)
      ├── child WbsNodes
      ├── Activities            (C7 — schedule)
      ├── BoqItem allocations   (many-to-many by quantity)
      ├── CostCode budget lines (budget owned at the node)
      ├── ProgressRecords       (per method)
      ├── Resource plans        (manpower / equipment)
      └── Documents             (drawings, method statements)
```

### 2.2 `WbsNode` — fields and rules

| Field | Rule |
|---|---|
| `ID` | **Stable identity. Never reissued, never reused, never re-created on edit.** This is the direct answer to CR-01. |
| `CompanyID`, `ProjectId` | Both required. Company-scoped and project-scoped (see `Stage-Construction-11`). |
| `ParentId` | Nullable (root). **Must belong to the same project and the same company** — enforced in service *and* by a DB CHECK/FK, not only in code. |
| `Code` | Unique per project. Human-facing (`1.2.3`), independent of `ID`. |
| `Name`, `NameEn` | `Name` required. Every user-facing string via Resources (screen side). |
| `Path` | Materialised path (`/1/1.2/1.2.3/`) maintained by the service; used for subtree reads and rollups. Recomputed on move, never hand-edited. |
| `Depth` | Derived, persisted, and **bounded** — proposed hard limit **10** levels (a limit that exists is the requirement; the number is tunable per company policy). |
| `Sequence` | Sort order among siblings. |
| `Status` | `Draft` → `Active` → `Suspended` → `Completed` → `Archived`. Explicit state machine; **no free-string status** (the current `Project.Status` is a free string, `Dimensions.cs:39`). |
| `StartDate`, `EndDate` | Planned dates; baseline captured separately (C7). |
| `ResponsibleEmployeeId` | The accountable owner. References HR; no duplication. |
| `ProgressMethod` | `Quantity` \| `Milestone` \| `WeightedActivity` \| `ManualApproved` (see `Stage-Construction-10`). Chosen per node — this is what makes physical progress definable (decision **D-11**). |
| `OwnsBudget` | Whether budget lines may attach at this node (typically leaves + designated control accounts). |
| `OwnsCost` | Whether cost may be allocated here (typically leaves only). |
| `IsArchived` | Archived nodes are **retained**, never deleted — history stays whole. |
| `RowVersion` | Concurrency token. Mandatory (CR-06). |

### 2.3 Structural-change rules

1. **No cross-project parent.** Service check + DB constraint.
2. **Archived, never deleted.** Deletion of a node with any financial or progress reference is refused outright.
3. **No silent re-parenting after financial use.** If a node (or its subtree) has budget, commitment, cost, progress or
   a certificate line, a move requires:
   - an **impact preview** (what budget, committed, actual, certified value and how many documents move with it),
   - an explicit **audit reason**, and
   - the `wbs.restructure` permission — a right distinct from `wbs.manage`.
4. **Rollups are derived, never stored as truth.** A parent's budget/cost/progress is computed from its subtree; a
   stored rollup would be a second source of truth that drifts.
5. **Impact preview is a read-only projection** — it must not lock or mutate anything.

### 2.4 Migration from today's BOQ tree

The existing `BoqItem.ParentId` tree is **not** promoted to a WBS automatically. The C1 plan is:

- Create WBS nodes explicitly (optionally *seeded* from the BOQ section headers as a convenience, with the user
  confirming each).
- Add `BoqItem.WbsNodeId` (nullable at first) so existing BOQs keep working unchanged.
- The BOQ tree remains, but its meaning is narrowed to *presentation/section grouping*, and `ReplaceAllAsync` is
  retired (§3.4).

---

## 3. BOQ model

### 3.1 The six BOQ kinds the brief requires

| Kind | Represented as | Note |
|---|---|---|
| Original BOQ | `BoqRevision` where `Kind = Original`, `RevisionNo = 0` | The as-awarded contractual set. |
| Revised BOQ | `BoqRevision` where `Kind = Revised` | Working revision, editable while `Draft`. |
| Approved BOQ | `BoqRevision.Status = Approved` | **Immutable from that moment.** |
| Client BOQ | `BoqRevision.Audience = Client`, linked to `ClientContract` | Contractual quantities and client rates. |
| Subcontractor BOQ | `SubcontractScope` lines referencing client BOQ items | Not a separate BOQ — an *allocation* of scope with its own rate (§3.5). |
| Internal cost BOQ | `BoqRevision.Audience = Internal` | The estimate. **Decision D-02** governs whether this is a separate structure or the current shared row. |

### 3.2 `BoqRevision` — the immutability carrier

| Field | Rule |
|---|---|
| `ID`, `CompanyID`, `ProjectId` | Required. |
| `ClientContractId` | Which contract this revision belongs to (decision **D-01**). |
| `RevisionNo` | Sequential per contract. |
| `Kind` | `Original` \| `Revised`. |
| `Audience` | `Client` \| `Internal`. |
| `Source` | `Contract` \| `Variation` \| `Correction`. A revision created by a variation records `VariationId`. |
| `Status` | `Draft` → `Submitted` → `Approved` → `Superseded`. |
| `ApprovedBy`, `ApprovedAt`, `Reason` | Required to reach `Approved`. |
| `RowVersion` | Concurrency token. |

**Rule: an `Approved` revision is immutable.** No quantity, rate, unit or description on its lines may change. A change
produces a **new revision** (or a variation, §3.6). This is the mechanism that closes CR-01 and CR-03 together.

### 3.3 `BoqItem` — extended fields

Existing fields stay (`Boq.cs:8-32`). Added:

| Field | Purpose |
|---|---|
| `BoqRevisionId` | Which revision this line belongs to. |
| `WbsNodeId` | Where in the work it sits. Nullable during migration, required for new BOQs. |
| `CostCodeId` | Default cost code for costs against this item. |
| `Status` | `Active` \| `Cancelled` (a cancelled item is retained, never deleted). |
| `RetentionApplicable` | Per-item retention flag (decision **D-04**). |
| `TaxApplicable`, `TaxRateOverride` | Tax treatment per item, not only per certificate. |
| `RevenueAccountId` | Optional account mapping override (default stays 4102 as today). |
| `UnitId` | A **unit reference**, replacing free-text `Unit`. Free text is why `Unit` can be changed without anyone noticing. |
| `RowVersion` | Concurrency token. |

Per-item quantity **state** is derived, never stored as a mutable field:

| Derived quantity | Source |
|---|---|
| Contractual quantity | `BoqItem.Quantity` in the approved revision |
| Executed quantity | latest `ProjectProgress` cumulative for the item |
| Approved quantity | approved progress record |
| Certified quantity | Σ certificate line quantities where certificate is `Approved`/`Posted` |
| Invoiced quantity | Σ certificate lines where `Posted` |
| Remaining quantity | contractual − certified |
| Progress % | per the node's `ProgressMethod` |

### 3.4 Rules that replace `ReplaceAllAsync`

`BL/BoqService.ReplaceAllAsync` must be **retired**, not patched. Its replacement:

1. Editing is per line, inside a `Draft` revision.
2. A `Draft` revision starts as a **copy** of the current approved revision (new line ids, `SourceLineId` back-pointer).
3. Approving the draft supersedes the previous revision; **no line of the previous revision is touched**.
4. A line may never be hard-deleted once referenced by progress, a certificate, an issue or a commitment — refuse, and
   offer `Cancelled` instead. (Today `DeleteItemAsync` checks only for child items, `BoqService.cs:97`.)
5. Every line change writes a `BoqLineHistory` row: old/new quantity, rate, unit, description, reason, actor, revision.

### 3.5 Subcontractor scope, not a second BOQ

`SubcontractScope` allocates client BOQ scope to a subcontract:

| Field | Rule |
|---|---|
| `SubcontractId`, `BoqItemId`, `WbsNodeId` | What is allocated. |
| `AllocatedQuantity`, `SubRate` | The sub's own rate — usually below the client rate; the margin is commercial data. |
| `RetentionApplicable`, `RowVersion` | |

**Cap rule (closes CR-02):** for any BOQ item, `Σ AllocatedQuantity` across active subcontracts may not exceed the
item's contractual quantity, and certified quantity per scope line may not exceed `AllocatedQuantity`. Exceeding either
is refused, or permitted only with an approved variation — decision **D-07**.

### 3.6 Change rules (the brief's list, made concrete)

| Brief rule | Mechanism |
|---|---|
| Approved revisions remain immutable | `BoqRevision.Status = Approved` blocks all line writes; enforced in service **and** by a DB trigger-free guard (status checked in the update path + CHECK on revision status transitions). |
| Quantity changes require a revision or variation | No write path to an approved line exists; the only routes are a new revision or a variation-produced revision. |
| No silent overwrite of contractual values | `BoqLineHistory` is written in the same transaction as any line change; a change with no reason is refused. |
| Historical certificates preserve the BOQ revision used | `CertificateLine.BoqRevisionId` + snapshot of quantity/rate at certification. |
| Subcontractor quantity cannot exceed allocated scope without approval | §3.5 cap rule. |
| Unit changes require impact analysis | A unit change is only possible in a new revision, and the revision approval screen shows every executed/certified quantity affected. |
| Price changes require authority and audit reason | `boq.approve` permission + mandatory `Reason` on the revision. |

---

## 4. Concurrency

Every entity in this document carries `RowVersion`. The required behaviour on conflict is **compare and reload**, never
last-write-wins:

1. The write fails with a concurrency result (not an exception surfaced to the user).
2. The user is shown *their* value and the *current stored* value, field by field.
3. The user chooses explicitly; the chosen outcome is written with an audit reason recording that a conflict occurred.

This applies with particular force to BOQ revisions and certificate quantities, where two quantity surveyors editing
the same measurement is a normal event, and silently discarding one of them is a commercial error.

---

## 5. Open decisions carried by this design

| Decision | Effect if answered differently |
|---|---|
| **D-01** one or many client contracts per project | Determines whether `BoqRevision` keys on `ClientContractId` or `ProjectId`. |
| **D-02** shared or separate client/internal BOQ | Determines whether `Audience` splits revisions or the current four cost buckets stay on the client line. |
| **D-04** retention method | Determines whether `RetentionApplicable` per item is needed at all. |
| **D-07** over-certification policy | Determines whether the §3.5 cap is a hard block or an approval gate. |
| **D-11** physical progress method | Determines the `ProgressMethod` values that must be supported at C1. |