# Stage 1 Batch C — pre-implementation analysis

**Requirement status: NOT STARTED.** No code has been changed. This document exists to be reviewed before any
implementation, per the Phase 1 instruction.

**Method:** source inspection of every controller, service and entity in scope · the controller-action scanner ·
raw grep · the endpoint inventory · plus inspection of the existing in-body checks. Reconciliation in §2.

---

## 1. Five findings that change what Batch C can honestly deliver

Stated first because they are decisive, and two of them need a decision before implementation.

### 1.1 None of the three modules has a role table

Every module that has an access service today owns a role table. **None of the three in scope does.**

| Module | Role table | Access service |
|---|---|---|
| Accounting | `AccountingUserRoles` | ✔ `AccountingAccessService` |
| Inventory | `InventoryUserRoles` | ✔ `InventoryAccessService` |
| CRM | `CrmUserRoles` | ✔ `CrmAccessService` |
| POS | `BranchUserRoles` | ✔ `PosAccessService` |
| **HR** | **none** | ✖ |
| **Projects** | **none** | ✖ |
| **Tasks / Communication** | **none** | ✖ |

Verified by enumerating every `DbSet` on `CrossDbContext`. The only role-like tables outside the four are
`ConversationMember` (chat membership, `Role = Owner\|Member`), `CampaignMember` and `CrmListMember`.

So the roles named in the brief — HR officer, HR supervisor, payroll officer, project manager, project member,
department manager, project viewer — **do not exist in this codebase**, in data or in code. The brief anticipated
this (*"Do not assume these roles exist. Verify them."*).

### 1.2 These modules currently have NO authorization at all

`AdminController` (+3 partials), `PeopleController`, `ProjectController`, `TasksController`: **zero**
`[Authorize(Roles=…)]`, **zero** `IsInRole`, **zero** permission attributes, **zero** in-body access-service calls.
Their only guard is the global `SessionValidationMiddleware` — i.e. *authentication*.

This matches the backlog exactly: AdminController 42, ProjectController 30, TasksController 13, PeopleController 5
mutating actions with no permission. **Any signed-in employee can today reach employee administration, payroll
paths, project administration and every task.**

Consequence for compatibility: there is no existing authorization to preserve. Every rule Batch C introduces is a
**new restriction**, so the compatibility risk is not "will authorized users keep working" but "will *currently
unrestricted* users suddenly be blocked". That is the opposite shape from Hotfix A.1 and it drives §12.

### 1.3 Project record-level access is NOT derivable from data — the one genuine blocker

`Project` (`Models/Context/Accounting/Dimensions.cs:23`) carries: `ID`, `CompanyID`, `Code`, `Name`, `NameEn`,
`IsActive`, `StartDate`, `EndDate`, `Budget`, `CustomerId`, `Location`, `ContractValue`, `Status`, `ActivityTypeId`,
`CostCenterId`, `AdvancePercent`, `RetentionPercent`.

**There is no manager, no owner, no member, and no employee reference of any kind.** A search across the whole model
for an employee↔project relationship (`ProjectId` on any employee/labour/timesheet entity) returns **nothing**.

So C4's rules — project manager, project member, department owner — **cannot be implemented from stored data**.
`TaskItem` can carry `EntityType = "Project"`, which is a *task* link, not project membership.

### 1.4 Task and Communication record-level access ARE derivable

| Module | Real anchors in data |
|---|---|
| **Tasks** | `TaskItem.AssigneeEmployeeId`, `CreatedByEmployeeId`, `EntityType`/`EntityId` (linked object), `CompanyId` |
| **Communication** | `ConversationMember` (ConversationId, EmployeeId, `Role = Owner\|Member`), `Conversation.CompanyID`, `Conversation.CreatedByEmployeeId`, `Kind = Direct\|Group` |

`TaskItem` has **no** team, no followers, no visibility field and no `BranchId` — so those parts of C5 have nothing
to bind to. `ChatService` already enforces membership in-body (`IsMemberAsync`, `ConversationId + CompanyID`), which
is the real behaviour to canonicalise rather than replace.

**Announcements:** `Announcement.Scope = Company | Branch` + `BranchID`; `AnnouncementRead` is a read receipt only.
That is the existing audience rule — company-wide or branch-wide, with no per-employee targeting.

**Email outbox (`CommMessage`):** `CompanyID`, `ToAddress`, `Subject`, `Body`, `Status`, `Attempts`, `DeletedAt`.
**No owner, no sender employee, no participant field.** So "operationally protected" can only mean company scope +
an explicit administrative permission — there is no participant rule to derive.

### 1.5 A verified manager hierarchy exists — and it has no CompanyID

Two independent implementations already walk it:

* `CrmAccessService.TeamOwnerIdsAsync` — `Hierarchical` nodes where `H_Type == 5` carry `H_ObjectID = EmployeeId`;
  descend `H_Parent`, with a **cycle guard**;
* `LeaveWorkflowService` — employee node → its position → parent employee node, climbing to the unit head.
  *"Level 1 = direct manager, last = the unit head (final approver)."* This is the only existing record-level HR rule
  in the codebase.

**`Hierarchical` has no `CompanyID`** (B1 classified it `CrossCompanyOperational` for exactly this reason). A naive
team walk can therefore return employees of **another company**, so any hierarchy-derived access must be intersected
with the caller's company. Reusing the existing walk is right; reusing it unintersected would be a cross-company leak.

---

## 2. Action-count reconciliation (5 sources)

| Module | Controller | Scanner | Source inspection | Raw grep (`[Http{Post,Put,Delete,Patch}]`) | Endpoint inventory | In-body checks found |
|---|---|---|---|---|---|---|
| HR | `AdminController` (+3 partials) | 85 actions / **42 mutating** | same | 42 | same | **0** |
| HR | `PeopleController` | 18 / **5** | same | 5 | same | **0** |
| Projects | `ProjectController` | 46 / **30** | same | 30 | same | **0** |
| Tasks | `TasksController` | 21 / **13** | same | 13 | same | **0** |
| Comm | `ChatController` | 14 / **9** | same | 9 | same | in-body membership in `ChatService`, not the controller |
| Comm | `FileManagerController` | 9 / **5** | same | 5 | same | **0** |
| Comm | `CommController` | 8 / **4** | same | 4 | same | **0** |
| Comm | `CalendarController` | 7 / **2** | same | 2 | same | **0** |
| Comm | `AnnouncementsController` | 5 / **3** | same | 3 | same | **0** |
| Comm | `CommentsController` | 3 / **2** | same | 2 | same | **0** |
| Comm | `NotificationsController` | 3 / **1** | same | 1 | same | **0** |
| Comm | `NotificationsApiController` | 3 / **2** | same | 2 | same | class-level `[Authorize]` only |

**All five sources agree.** In-scope mutating actions: **HR 47 · Projects 30 · Tasks 13 · Communication 28 = 118**
of the 183 backlog (64%). The remaining 65 are POS setup (42), Accounting MVC (2), Inventory (1), Identity (2), AI (4),
Brand (3), Service (2), HrApi (2), LeaveApi (2), and 5 others.

---

## 3-6. Role and permission maps — what can be derived, and from what

### 3. HR

| Actor that genuinely exists | Evidence | Can it drive a rule? |
|---|---|---|
| The employee themself | `Employee.ID` vs `BusinessContext.EmployeeId` | **Yes** — self-service reads |
| Direct manager / approver chain | `LeaveWorkflowService` + `Hierarchical` H_Type 5 | **Yes** — direct reports, leave approval |
| Platform administrator | `PlatformOpsAttribute.AdminRoles` = Admin, Administrator, SuperAdmin, PlatformOps (Identity roles) | **Yes** — but these are *platform* roles, not HR roles |
| HR officer / supervisor / payroll officer | **nothing** | **No — does not exist** |

Operations found (from the 47 mutating actions): employees, attendance, leave requests + approval steps, employee
requests + steps, payroll/payslips, official holidays, leave types/policies/encashment/provision, salary policies,
attendance policies, policy assignments, job titles, hierarchicals, appraisals (cycles, templates, criteria, lines),
training (courses, enrolments), recruitment (job applications, documents, required document types), employee
documents, employment contracts, HR document attachments.

### 4. Projects

| Actor | Evidence | Can it drive a rule? |
|---|---|---|
| Company member | `Project.CompanyID` | **Yes** |
| Platform administrator | Identity admin roles | **Yes** |
| Finance user (billing/costs) | `AccountingAccessService` `post`/`pay`/`manage` already governs the GL side | **Yes — by delegation** |
| Project manager / member / viewer / department owner | **nothing** | **No — see §1.3** |

Operations: projects CRUD, activity types, progress (`ProjectProgress`, `ProjectProgressLine`), material issues,
budgets (`ProjectBudgetService`), labour (`ProjectLaborService`), billing (`TaskBillingService`), dashboards.

### 5. Tasks

| Rule | Anchor | Derivable |
|---|---|---|
| Assignee access | `TaskItem.AssigneeEmployeeId` | **Yes** |
| Creator access | `TaskItem.CreatedByEmployeeId` | **Yes** |
| Manager access to a report's task | `Hierarchical` walk ∩ company | **Yes** |
| Linked-entity access | `TaskItem.EntityType`/`EntityId` → `IPlatformPermissionProvider` | **Yes** |
| Company | `TaskItem.CompanyId` *(note the casing — `CompanyId`, not `CompanyID`; and TaskItem is **not** a B2 pilot entity, so it has no query filter)* | **Yes** |
| Team / followers / visibility tier / branch | **nothing** | **No** |
| Workflow-task distinction | `IsScheduled`, `TaskAutoRule`, `MatchedAt` distinguish *auto-generated* from manual — not workflow | Partly |

### 6. Communication

| Rule | Anchor | Derivable |
|---|---|---|
| Direct conversation → participant only | `ConversationMember` | **Yes** (already enforced in `ChatService`) |
| Group conversation → member, or Owner for management | `ConversationMember.Role` | **Yes** |
| Announcement audience | `Announcement.Scope` + `BranchID` | **Yes** |
| Entity-scoped conversation → linked-entity access | `DocComment.EntityType/EntityId` | **Yes** |
| Attachment follows its conversation | `CommAttachment` → `CommMessage` → CompanyID | **Yes** |
| Email outbox | `CommMessage.CompanyID` only — no owner | **Company + admin permission only** |
| Cross-company participant | `Conversation.CompanyID` + member's `Employee.EmpCompanyID` | **Yes — reject** |
| SignalR joins | `ChatHub` — needs inspection against the same rule | **Yes, in principle** |

---

## 7-8. Company isolation and existing behaviour

* None of the four modules' entities is in the B2 pilot, so **no query filter protects any of them**: `TaskItem`,
  `Conversation`, `ChatMessage`, `Announcement`, `CommMessage`, `Project`, `Employee` (deliberately excluded),
  `LeaveRequest`, `AttendanceRecord`, `Payslip`. Company isolation here must be explicit in the access service.
* `Employee` and the role tables are excluded from filtering **by design** (B1 §2.2) — filtering them would be
  circular. So HR's own company checks must be written, not inherited.
* `Hierarchical` has no `CompanyID` (§1.5).
* `TaskItem.CompanyId` uses different casing from every other entity — a real trap for a copied predicate.

---

## 9. Proposed vocabularies — narrowed to what exists

Deliberately **not** the brief's example lists; each action below has an anchor in §3-§6.

**HR** (`ScopeHr`): `read` · `employee-view` · `employee-manage` · `attendance-manage` · `leave-manage` ·
`leave-approve` · `payroll-view` · `payroll-manage` · `organization-manage` · `performance-manage` ·
`confidential-view`
*Dropped from the example list:* nothing — all eleven have a real operation. But **`leave-approve` is the only one
with an existing record-level rule** (the approver chain); the rest are company + role.

**Projects** (`ScopeProjects`): `read` · `create` · `edit` · `manage` · `budget-view` · `budget-manage` · `billing` ·
`close`
*Dropped:* **`member-manage`** — there is no membership to manage (§1.3). **`approve`** — no project approval exists
in code.

**Tasks** (`ScopeTasks`): `read` · `create` · `edit` · `assign` · `reassign` · `complete` · `reopen` · `manage`
*Dropped:* **`delete`** — `TasksController` has no delete action. **`confidential-view`** — `TaskItem` has no
visibility tier.

**Communication** (`ScopeCommunication`): `read` · `send` · `create-group` · `manage-group` · `announcement-send` ·
`outbox-manage`
*Dropped:* **`moderate`** — no moderation feature exists. **`participant-manage`** — folded into `manage-group`,
which is what `ConversationMember.Role = Owner` actually expresses. **`confidential-view`** — no visibility tier on
any communication entity. *Added:* **`outbox-manage`**, because `CommMessage` has no participant rule and needs an
explicit administrative gate (§1.4).

## 10. Proposed adapter mappings

Following `ModulePermissionAdapterBase`. **Precedent that matters:** `ManufacturingPermissionAdapter` has no RBAC
table and **delegates to Inventory rather than inventing a policy** — the honest template for a module without roles.

| Adapter | View | ViewConfidential | ViewRestricted |
|---|---|---|---|
| `HrPermissionAdapter` | `read` | `employee-view` | `confidential-view` |
| `ProjectsPermissionAdapter` | `read` | `budget-view` | `manage` |
| `TasksPermissionAdapter` | `read` | `edit` | `manage` |
| `CommunicationPermissionAdapter` | `read` | `send` | `manage-group` |

**Gap found in the base class:** `ModulePermissionAdapterBase.CanAsync` passes `target: null`, so record-level
targets are **not** threaded through `IPlatformPermissionProvider` today. C7's record-level requirement therefore
needs the base class to forward a `PermissionTarget` — a change to shared platform code used by all four existing
adapters, so it must be additive and behaviour-preserving.

---

## 11-13. Files, and the SQL question

**To create:** `BL/HrAccessService.cs` · `BL/ProjectsAccessService.cs` · `BL/TasksAccessService.cs` ·
`BL/CommunicationAccessService.cs` · four adapters in `BL/Platform/PlatformPermissionProvider.cs` · four attributes
(`HrPerm`, `ProjectPerm`, `TaskPerm`, `CommunicationPerm`) · `CrossBuy.Tests/BatchC*Tests.cs` · four ADRs · vocabulary
doc · analysis + delivery report + maturity record.

**To modify:** `Program.cs` (DI) · `BL/Platform/EntityRegistry.cs` (four scope constants; entity codes for
`LeaveRequest`, `TaskItem`, `Conversation`, `Announcement`) · `ModulePermissionAdapterBase` (forward the target) ·
the C9 proof endpoints only.

**SQL requirements — and this is the decision point.** Two schema options, both requiring **idempotent scripts in
`deploy/sql` that I would NOT execute**:

| | Option A — add role tables | Option B — derive from what exists |
|---|---|---|
| New tables | `HrUserRoles`, `ProjectUserRoles`, `TaskUserRoles`, `CommUserRoles` (+ `ProjectMembers` for §1.3) | none |
| Follows the established pattern | **Yes** — identical to the four existing modules | No — diverges |
| Enables the brief's roles (HR officer, project manager/member…) | **Yes** | **No** — those roles cannot exist |
| C4 record-level project access | **Implementable** via `ProjectMembers` | **Blocked** (§1.3) |
| Deployment | 5 scripts to review + apply to a production database | nothing to deploy |
| Risk | new schema; roles must be populated or every module stays bootstrap-open | zero schema risk; access limited to company + self + hierarchy + admin |

Under **either** option the services use the **bootstrap-open** rule the four existing modules already use — dormant
until at least one role row exists in that company — so nothing currently working breaks on day one.

---

## 14-17. Risks, tests, rollback

**Compatibility risk (the dominant one):** §1.2 — these modules have no authorization today, so any enforcement is a
new restriction. Mitigation: bootstrap-open per company, plus proof endpoints only (C9), plus Batch D for the rest.

**Security risks:** (a) the hierarchy walk without a company intersection (§1.5) would leak cross-company;
(b) `TaskItem.CompanyId` casing invites a copied predicate that silently compiles against the wrong property;
(c) `CommMessage` has no participant rule, so an over-permissive `read` would expose the whole company's email
outbox; (d) adding a `PermissionTarget` to the shared adapter base touches four working adapters.

**Test plan:** the 38 cases in C10, plus per-module vocabulary tests, plus the cross-company hierarchy case, plus
regression of all 492 existing tests, on SQLite **and** SQL Server.

**Rollback:** four new services + four adapters + four attributes are additive; revert the files, remove the DI lines,
revert the `EntityRegistry` constants and the adapter-base change. No data change under Option B; under Option A the
tables are additive and unused until populated.

## 18. Requirement status

| Requirement | Status |
|---|---|
| Phase 1 analysis | **Completed** (this document) |
| C1-C13, maturity, docs | **Not Started** |

---

## Decision requested before implementation

1. **Option A or Option B** (§11-13)? A follows the established four-module pattern and makes C4's project
   record-level access implementable, at the cost of five new tables to review and deploy. B ships no schema and
   cannot implement project membership, so C4 record-level would be reported **Blocked** with this analysis as the
   reason.
2. If **A**: confirm that idempotent scripts written to `deploy/sql` and **not executed** is the expected handling,
   as with every previous slice.
