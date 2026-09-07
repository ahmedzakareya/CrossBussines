# Stage-Construction-11 — Permissions, Data Isolation, Traceability and Concurrency

Covers Phase 20 (permissions — **vocabulary only, nothing wired**), the Data Isolation section, Phase 18 (traceability
and audit) and the Concurrency section.

> **Nothing in this document modifies production authorization.** Authorization, bootstrap policies, access services and
> the security console are **FIRST-TAB** owned. What follows is a proposal to be reviewed and wired by that tab.

---

## 1. What authorization exists today

`BL/ProjectsAccessService.cs` (264 lines) is the Projects module's access service — and it is well built:

- **8 actions:** `read`, `create`, `edit`, `manage`, `budget-view`, `budget-manage`, `billing`, `close`
  (`ProjectsActions`, lines 32-45).
- **3 module roles:** `ProjectsAdministrator`, `ProjectsFinance`, `ProjectsViewer` (lines 49-57).
- **Record level:** `ProjectMembers` with `Manager` / `Member` / `Observer` (`Models/Context/Accounting/ProjectMember.cs:51-66`).
- **Two rules worth preserving verbatim in the construction vocabulary:**
  1. `billing` requires the **Accounting module's own `post` decision** as well as a projects right (lines 104-117) —
     project administration is not a back door into the ledger.
  2. **Budget is never granted by membership** (lines 123-130) — "a Member sees the project, not its money".
- The project's company is read from the **project row**, never from the request (lines 215-222), and a foreign project
  and a non-existent project answer identically so ids cannot be enumerated.

**The gap (CR-09, first-tab owned).** `Controllers/ProjectController.cs` gates **24 of 46** actions and uses the literal
`DefaultCompanyId = 1` **61 times**. `SaveProject` and `DeleteProject` (lines 180-203) have no gate at all. So today any
signed-in employee can create, edit or delete a company-1 project regardless of their own company. Reported, not
modified.

---

## 2. Proposed construction permission vocabulary

Scope: `Construction` (a new `EntityRegistry` scope, alongside `ScopeProjects`).

| Action | Meaning | Notes |
|---|---|---|
| `project.read` | See a construction project | |
| `project.manage` | Create/edit construction project attributes | not budget, not billing |
| `project.close` | Handover / closeout | mirrors today's `close` staying with the module role, not the project manager |
| `wbs.read` / `wbs.manage` | View / build the work breakdown | |
| `wbs.restructure` | Move a node that already has financial or progress data | **distinct right** — an impact-preview action |
| `boq.read` / `boq.manage` / `boq.approve` | BOQ revision lifecycle | `approve` freezes a revision |
| `budget.read` / `budget.manage` / `budget.approve` | Budget versions | keeps today's separation of view vs manage |
| `budget.transfer` | Move budget between lines | |
| `cost.read` | Cost control views | |
| `cost.confidential` | See rates, margin, subcontractor rates vs client rates | **the commercial-confidentiality right** |
| `commitment.read` | Committed cost | |
| `contract.manage` / `contract.approve` | Client contract | |
| `subcontract.manage` / `subcontract.approve` | Subcontract + scope allocation | |
| `certificate.create` / `certificate.review` / `certificate.approve` / `certificate.post` | Client certificate chain | `post` additionally requires Accounting `post` |
| `subcertificate.create` / `.review` / `.approve` / `.post` | Subcontractor certificate chain | same rule |
| `retention.release` | Release retention | additionally requires Accounting `post` and the DLP gate |
| `variation.create` / `variation.estimate` / `variation.approve` | Variation chain | |
| `claim.manage` | Claims, delay events, EOT | |
| `sitereport.create` / `sitereport.review` / `sitereport.approve` | DSR, manpower, equipment logs | |
| `materialrequest.create` / `.approve` | Site material demand | issuing stock also needs the Inventory right |
| `site.operate` | Site store operations | |
| `rfi.create` / `rfi.respond` / `rfi.approve` | RFI | |
| `inspection.create` / `inspection.respond` / `inspection.approve` | IR / MIR | |
| `submittal.manage` / `submittal.approve` | Submittals, method statements | |
| `drawing.read` / `drawing.manage` / `drawing.approve` | Document register and revisions | |
| `progress.record` / `progress.approve` | Progress records | |
| `crossproject.access` | Act across projects without membership | |
| `crossbranch.access` | Act across branches | |
| `financial.detail` | See amounts at all (vs quantities only) | a site engineer may need quantities without values |
| `export` | Export construction data | export of confidential cost requires `cost.confidential` too |

### 2.1 Scope dimensions

| Scope | Meaning |
|---|---|
| `own` | Projects the caller created or is `Manager` of |
| `assigned` | Projects with an active `ProjectMembers` row (the existing relationship) |
| `team` | Projects of the caller's org subtree (`Hierarchicals`) |
| `branch` | Projects of a branch |
| `company` | Every project in the company (role-driven, as today) |
| `confidential` | An orthogonal flag: within any of the above, whether commercial data is visible |

`confidential` is deliberately **not** a scope level but a separate axis — a project manager with company scope may
still be excluded from margin and subcontractor rates, and a commercial analyst with no project membership may need
exactly the opposite.

### 2.2 Rules the vocabulary must preserve

1. **Financial actions require the Accounting decision as well** — extend the existing `billing` pattern to
   `certificate.post`, `subcertificate.post`, `retention.release`.
2. **Cost/margin visibility is never granted by membership.** Same principle as today's budget rule.
3. **Hiding a UI control is not a control.** Every action is checked server-side in the service, and the check is what
   the screen reflects.
4. **A request-supplied `companyId` or `projectId` is a lookup key only** — validated against the resolved
   `BusinessContext` and the entity row, never trusted (the pattern already at `ProjectsAccessService.cs:215-222`).
5. Refusal and absence must be **indistinguishable** (already the practice — `ProjectController.cs:229-230`).

---

## 3. Data isolation

### 3.1 Classification of every construction entity

| Isolation | Entities |
|---|---|
| **Company-scoped** (every row, always) | *all* construction entities without exception |
| **Project-scoped** | WbsNode, BoqRevision, BoqItem, ProjectBudget(+Line), BudgetTransfer, ProjectCommitment, CostAllocation, ClientContract, Subcontract, SubcontractScope, Certificate(+Line), Variation, Claim, DelayEvent, ExtensionOfTime, ProgressRecord, CashFlowPlan(+Line), DocumentRegister(+Revision), Rfi, InspectionRequest, Submittal, MethodStatement, Ncr, PunchItem, Handover, ProjectCloseout, ConstructionAudit |
| **Branch-scoped** (where resolvable) | ConstructionSite, CostAllocation, ProjectCommitment |
| **Site-scoped** | ConstructionSite (self), MaterialRequest, DailySiteReport + all children, ManpowerLog, EquipmentLog, SiteWarehouse |
| **Global reference (per company)** | CostCode, TradeId lookup, DisciplineId lookup, unit lookup |

### 3.2 Non-negotiable rules

1. **No `CompanyID = 1` fallback anywhere.** Not a constant, not a default parameter, not a `?? 1`. An unresolved
   company reads nothing and writes nothing — fail closed. (The existing `DefaultCompanyId` in `ProjectController` is
   exactly what this forbids, CR-09.)
2. **No cross-company reference.** Every FK target must be in the same company; enforced by service check **and** DB
   constraint.
3. **No cross-project child.** A WBS parent, a BOQ line, a certificate line, a scope allocation and a site all belong to
   one project. Enforced by a composite `(CompanyID, ProjectId)` guard, not by application code alone.
4. **`IgnoreQueryFilters()` remains forbidden** outside the platform bypass implementation.
5. Cross-company access, if ever needed for a group-level construction report, uses the platform's authorised, reasoned,
   scoped and audited bypass — never an ad-hoc query.

### 3.3 Database constraints to add (design only — no SQL executed in this increment)

For each construction table, in additive idempotent `deploy/sql` scripts:

- `CompanyID` NOT NULL; `ProjectId` NOT NULL where project-scoped.
- FK to `Projects(ID)`; FK to parent tables — today `BoqItems` has **no** FK to `Projects` at all
  (`deploy/sql/boq.sql`).
- **Composite guard** so a child cannot belong to a different project than its parent: e.g. a unique
  `(ID, CompanyID, ProjectId)` on the header and a composite FK from the child on all three columns.
- `CHECK` constraints for: status domains, percentages in `0..100`, non-negative quantities and amounts, and
  `ParentId <> ID`. Across the whole current construction schema there are **zero** CHECK constraints (CR-07).
- Filtered unique indexes: one current document revision per register; one DSR per site per date; one approved baseline
  budget per project.
- `ROWVERSION` column on every commercially significant table (CR-06).

### 3.4 Service-level validation

Every construction service method: resolve company from the validated `BusinessContext` (never a parameter default) →
verify the target row's own company → verify project membership or module role via the access service → verify the
project/contract state permits the operation → then act. Reads are `AsNoTracking`, and any acceptance re-reads from a
**new context**.

---

## 4. Traceability and audit (Phase 18)

### 4.1 What exists

Construction entities carry only `CreatedBy`/`CreatedAt` and, on postable documents, `PostedBy`/`PostedAt`
(e.g. `Models/Context/Accounting/ProgressBilling.cs:31-33`). There is **no** field-level history table, no reason
capture and no correlation id. So a rate that changed yesterday cannot be shown as having changed, by whom, or why.

### 4.2 `ConstructionAudit`

| Field | Rule |
|---|---|
| `CompanyID`, `ProjectId` | Required. |
| `EntityType`, `EntityId`, `LineId` | What changed — **line-level, not just header**. |
| `FieldName`, `OldValue`, `NewValue` | Stored as text with the original type recorded; numeric values also in a decimal column for reporting. |
| `ChangeKind` | `Created` \| `Updated` \| `Approved` \| `Posted` \| `Reversed` \| `Cancelled` \| `Restructured` |
| `Reason` | **Required** for any change to a commercial value (quantity, rate, value, percentage, date-with-contractual-effect). A change without a reason is refused. |
| `ActorEmployeeId`, `ActorUserId`, `OccurredAt` | |
| `SourceScreen`, `CorrelationId` | The request/operation that produced it — one user action producing twelve rows must be recognisable as one action. |
| `RevisionId` | The BOQ/budget/document revision in force. |
| `IsImmutable` | The table is **append-only**: no update, no delete, ever. Enforced by permission and by having no update path in the service. |

### 4.3 Required coverage

BOQ lines · budget lines · contract lines and terms · certificate lines · variation lines · cost allocations · material
quantities · progress quantities · document revisions · scope allocations. Written **in the same transaction** as the
change it records — an audit row that can be lost independently of its change is worse than none, because it makes the
history look complete when it is not.

### 4.4 What audit is *not*

- It is **not** the timeline. Activity/timeline integration is the **THIRD TAB's**; this tab defines events and payloads
  only (`Stage-Construction-12`).
- It is **not** a substitute for reversal. A posted document is corrected by `JournalEntryService.ReverseAsync` through
  the owning service, and the audit row records that a reversal happened.
- It does **not** replace `UpdatedBy`/`UpdatedAt`; those stay for cheap "last touched" reads.

---

## 5. Concurrency

### 5.1 Current state

**Zero** `ROWVERSION`/`timestamp` columns across `deploy/sql/boq.sql` and every `deploy/sql/projects_p*.sql`, and no
construction entity declares a concurrency token. Two quantity surveyors editing the same measurement is a normal
event; today the second save wins silently.

### 5.2 Required behaviour

| Area | Concurrency rule |
|---|---|
| BOQ revisions | `RowVersion` on revision and line. A conflicting line edit shows both values field by field. |
| Budget revisions | `RowVersion` on version and line; an approved version is immutable so conflicts can only occur in draft. |
| Certificate quantities | `RowVersion` on header and line; **plus** a re-validation at post that recomputes previously-certified from the database (already the pattern at `BL/ProgressBillingService.cs:212-214`, which recomputes authoritatively before posting — keep it). |
| Contract changes | `RowVersion`; an amendment records previous values. |
| Variation approvals | `RowVersion` + an idempotent approve (a double-approve must not produce two BOQ revisions). |
| Progress updates | `RowVersion` per record; the measurement's `Confirmed` state must be checked before transition (today `ConfirmAsync` does not, `BL/ProgressService.cs:256`). |
| Document revisions | `RowVersion` + the filtered unique index on `IsCurrent`, so two simultaneous issues cannot both become current. |

### 5.3 Compare/reload contract

On a concurrency failure the service returns a **structured conflict**, not an exception message: the entity, the
fields that differ, the caller's value, the stored value, and who changed it when. The screen shows all of it and the
user chooses per field. The resulting write records `ChangeKind = Updated` with a reason noting the conflict.

**Never** last-write-wins on a commercial value, and never a silent merge.